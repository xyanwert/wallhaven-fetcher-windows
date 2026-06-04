using System.Net.Http;
using WallhavenFetcher.Imaging;

namespace WallhavenFetcher.Sync;

/// <summary>
/// Orchestrates a single sync run: rollout phase (retire stale) → bootstrap
/// or steady-state fill (fetch + download + fit). Idempotent: safe to call
/// every N minutes via the tray timer.
/// </summary>
public sealed class SyncEngine
{
    private readonly HttpClient _http;
    private readonly SourceRegistry _registry;
    private readonly Action<string> _log;
    private readonly Action<string, string> _notify;

    public SyncEngine(HttpClient http, SourceRegistry registry,
                      Action<string> log, Action<string, string> notify)
    {
        _http = http;
        _registry = registry;
        _log = log;
        _notify = notify;
    }

    public async Task<SyncResult> RunAsync(Config cfg, State state,
                                            bool force = false,
                                            CancellationToken ct = default)
    {
        _log("─── SYNC START ───");
        _log($"[SYNC] config: source={cfg.Source}  q={cfg.Q.Substring(0, Math.Min(60, cfg.Q.Length))}" +
             $"{(cfg.Q.Length > 60 ? "…" : "")}");
        _log($"[SYNC] config: categories={cfg.Categories}  purity={cfg.Purity}  " +
             $"sorting={cfg.Sorting}  order={cfg.Order}");
        _log($"[SYNC] config: atleast={cfg.Atleast}  ratios={cfg.Ratios}  topRange={cfg.TopRange}");
        _log($"[SYNC] config: maxWallpapers={cfg.MaxWallpapers}  newPerRun={cfg.NewPerRun}  " +
             $"rolloutPct={cfg.RolloutPct}");
        _log($"[SYNC] config: fitToRatio={cfg.FitToRatio}  target={cfg.TargetResolution}  " +
             $"fitTol={cfg.FitTolerancePct}  cropThr={cfg.CropThresholdPct}");
        _log($"[SYNC] force={force}  frozen={cfg.Frozen}");

        if (cfg.Frozen && !force)
        {
            _log("[SYNC] frozen — skipping (this run is automatic; use 'Sync now' to override)");
            _log("─── SYNC END (frozen no-op) ───");
            return new SyncResult(0, 0, "frozen");
        }

        var src = _registry.Get(cfg.Source);
        _log($"[SYNC] resolved source: {src.GetType().Name} (name={src.Name})");

        // Inject parse-diagnostic logger if the source supports it.
        if (src is WallhavenSource ws)  ws.Log = _log;
        if (src is KonachanSource  ks)  ks.Log = _log;

        var folder = cfg.ResolveFolder();
        Directory.CreateDirectory(folder);
        _log($"[SYNC] folder: {folder}");

        var fp = Fingerprint.Compute(cfg, src.Name, src.FingerprintKeys);
        var srcState = state.Of(src.Name);

        // Enumerate folder contents
        var onDisk = EnumerateFolder(folder);

        // Defensive sweep: banned IDs that resurface on disk get cleaned
        SweepBanned(onDisk, state, folder);

        // Stale = any non-saved file whose source+fp doesn't match the
        // current active query.
        var staleCids = new List<CanonicalId>();
        foreach (var kvp in onDisk)
        {
            var cid = kvp.Key;
            if (state.IsSaved(cid)) continue;
            if (cid.Source != src.Name) { staleCids.Add(cid); continue; }
            srcState.Images.TryGetValue(cid.ItemId, out var fileFp);
            if (fileFp != fp) staleCids.Add(cid);
        }

        var savedCount = state.Saved.Count(s => onDisk.Keys.Any(c => c.ToString() == s));
        var effectiveMax = cfg.MaxWallpapers + savedCount;
        var sameSource = onDisk.Keys.Count(c => c.Source == src.Name);
        var otherSource = onDisk.Count - sameSource;

        _log($"Folder: {onDisk.Count}/{effectiveMax} files " +
             $"({sameSource} {src.Name}, {otherSource} other-source, " +
             $"{savedCount} saved, {staleCids.Count} stale, " +
             $"{state.Banned.Count} banned globally, fp={fp})");

        // ── Phase 1: Rollout ──
        int rolledOut = 0;
        if (staleCids.Count > 0 && cfg.RolloutPct > 0)
        {
            var perRun = Math.Max(1, (int)Math.Ceiling(cfg.MaxWallpapers * cfg.RolloutPct / 100.0));
            var toRetire = Math.Min(perRun, staleCids.Count);
            _log($"Rollout: retiring {toRetire} stale file(s) " +
                 $"({cfg.RolloutPct}% of rotating max={cfg.MaxWallpapers})");

            var staleSorted = staleCids
                .Select(c => (cid: c, path: onDisk[c]))
                .OrderBy(p => File.GetLastWriteTimeUtc(p.path))
                .Take(toRetire)
                .ToList();

            foreach (var (cid, path) in staleSorted)
            {
                try
                {
                    File.Delete(path);
                    state.Of(cid.Source).Images.Remove(cid.ItemId);
                    onDisk.Remove(cid);
                    _log($"  ↳ retired {Path.GetFileName(path)}");
                    rolledOut++;
                }
                catch (Exception ex)
                {
                    _log($"  ↳ couldn't retire {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }

        // ── Phase 2: Fill ──
        string mode;
        int need;
        if (onDisk.Count < effectiveMax)
        {
            mode = "bootstrap";
            need = effectiveMax - onDisk.Count;
            _log($"Bootstrap: filling {onDisk.Count} → {effectiveMax} " +
                 $"(max {cfg.MaxWallpapers} rotating + {savedCount} saved; need {need})");
        }
        else
        {
            mode = "steady";
            need = cfg.NewPerRun;
            _log($"Steady state: pulling up to {need} fresh wallpapers");
        }

        if (need <= 0)
        {
            _log("Nothing to fetch");
            state.Save(Paths.StateFile);
            return new SyncResult(rolledOut, 0, mode);
        }

        // Page cursor: only stable sorts. Random pages are independent.
        int startPage = cfg.Sorting == "random" ? 1 : (srcState.Cursor.GetValueOrDefault(fp, 1));

        var haveCids = onDisk.Keys.Select(c => c.ToString()).ToHashSet();
        foreach (var b in state.Banned) haveCids.Add(b);

        var (newCandidates, nextPage) = await FetchNewCandidatesAsync(src, cfg, haveCids, need, startPage, ct);
        if (cfg.Sorting != "random") srcState.Cursor[fp] = nextPage;

        if (newCandidates.Count == 0)
        {
            _log("No new candidates from API — touching nothing");
            state.Save(Paths.StateFile);
            return new SyncResult(rolledOut, 0, mode);
        }

        _log($"Got {newCandidates.Count} new candidate(s) from API (next cursor: page {nextPage})");

        // Display target for fitting
        var (tw, th) = DisplayDetector.Resolve(cfg.TargetResolution);
        var fitter = new ImageFitter(tw, th, cfg.FitTolerancePct, cfg.CropThresholdPct,
                                     cfg.MaxImageDimFactor);

        int downloaded = 0, failed = 0, fitOk = 0, fitErr = 0;
        for (int i = 0; i < newCandidates.Count; i++)
        {
            var c = newCandidates[i];
            ct.ThrowIfCancellationRequested();
            var cid = new CanonicalId(src.Name, c.Id);
            var dest = Path.Combine(folder, cid.FileName(c.Ext));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                _log($"[DL {i + 1}/{newCandidates.Count}] {c.Id} ({c.Resolution}) " +
                     $"→ {Path.GetFileName(dest)}");
                var bytes = await DownloadAsync(c.Url, dest, ct);
                sw.Stop();
                _log($"[DL {i + 1}/{newCandidates.Count}] OK  size={bytes:N0}B  in {sw.ElapsedMilliseconds}ms");

                srcState.Images[c.Id] = fp;
                downloaded++;

                if (cfg.FitToRatio)
                {
                    var fitSw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var (modified, reason) = await fitter.FitInPlaceAsync(dest, ct);
                        fitSw.Stop();
                        if (modified)
                        {
                            _log($"[FIT {i + 1}/{newCandidates.Count}] modified: {reason} " +
                                 $"in {fitSw.ElapsedMilliseconds}ms");
                            fitOk++;
                        }
                        else
                        {
                            _log($"[FIT {i + 1}/{newCandidates.Count}] no-op: {reason}");
                        }
                    }
                    catch (Exception fitEx)
                    {
                        fitSw.Stop();
                        _log($"[FIT {i + 1}/{newCandidates.Count}] EXCEPTION after " +
                             $"{fitSw.ElapsedMilliseconds}ms — {fitEx.GetType().Name}: {fitEx.Message}");
                        _log($"[FIT]   stack: {fitEx.StackTrace}");
                        fitErr++;
                    }
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                failed++;
                _log($"[DL {i + 1}/{newCandidates.Count}] FAILED after {sw.ElapsedMilliseconds}ms — " +
                     $"{ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException is not null)
                    _log($"[DL]   inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                _log($"[DL]   stack: {ex.StackTrace}");
            }
        }

        _log($"[SYNC] downloaded={downloaded}  failed={failed}  fitOk={fitOk}  fitErr={fitErr}  " +
             $"(of {newCandidates.Count} candidates)");

        // Final cap: keep at most max_wallpapers non-saved files (across sources)
        EnforceCap(folder, state, cfg);

        // GC cursors for fingerprints no longer present
        var liveFps = srcState.Images.Values.ToHashSet();
        liveFps.Add(fp);
        var deadFps = srcState.Cursor.Keys.Where(k => !liveFps.Contains(k)).ToList();
        foreach (var dead in deadFps) srcState.Cursor.Remove(dead);

        state.Save(Paths.StateFile);

        if (downloaded > 0 || rolledOut > 0)
        {
            var bits = new List<string>();
            if (rolledOut > 0) bits.Add($"{rolledOut} retired");
            if (downloaded > 0) bits.Add($"{downloaded} new ({mode})");
            _notify("Wallhaven — Synced", $"[{src.Name}] {string.Join(", ", bits)}");
        }

        return new SyncResult(rolledOut, downloaded, mode);
    }

    private static Dictionary<CanonicalId, string> EnumerateFolder(string folder)
    {
        var dict = new Dictionary<CanonicalId, string>();
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            var cid = FileNameHelpers.TryParse(f);
            if (cid is not null) dict[cid.Value] = f;
        }
        return dict;
    }

    private void SweepBanned(Dictionary<CanonicalId, string> onDisk, State state, string folder)
    {
        var bannedSet = state.Banned.ToHashSet();
        var toRemove = onDisk.Where(kv => bannedSet.Contains(kv.Key.ToString())).ToList();
        foreach (var kv in toRemove)
        {
            try
            {
                File.Delete(kv.Value);
                _log($"Removed banned file that resurfaced: {Path.GetFileName(kv.Value)}");
                state.Of(kv.Key.Source).Images.Remove(kv.Key.ItemId);
                onDisk.Remove(kv.Key);
            }
            catch (Exception ex)
            {
                _log($"Could not remove banned {Path.GetFileName(kv.Value)}: {ex.Message}");
            }
        }
    }

    private async Task<(List<Candidate>, int)> FetchNewCandidatesAsync(
        ISource src, Config cfg, HashSet<string> haveCids, int need, int startPage,
        CancellationToken ct)
    {
        _log($"[FETCH] start  source={src.Name}  need={need}  startPage={startPage}  " +
             $"haveCids={haveCids.Count} (dedup set)");

        var collected = new List<Candidate>();
        var seen = new HashSet<string>(haveCids, StringComparer.OrdinalIgnoreCase);
        int page = startPage;
        const int maxPages = 20;
        bool exhausted = false;
        int totalRaw = 0, totalDup = 0;

        while (collected.Count < need && page < startPage + maxPages)
        {
            string url = src.BuildUrl(cfg, page);
            _log($"[FETCH] page {page}: querying {Redact(url)}");

            string body;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                body = await _http.GetStringAsync(url, ct);
                sw.Stop();
                _log($"[FETCH] page {page}: HTTP 200, {body.Length} bytes in {sw.ElapsedMilliseconds}ms");
            }
            catch (HttpRequestException ex)
            {
                sw.Stop();
                _log($"[FETCH] page {page}: HTTP ERROR after {sw.ElapsedMilliseconds}ms — " +
                     $"{ex.GetType().Name}: {ex.Message}");
                if (ex.InnerException is not null)
                    _log($"[FETCH]   inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                _log($"[FETCH] page {page}: UNEXPECTED after {sw.ElapsedMilliseconds}ms — " +
                     $"{ex.GetType().Name}: {ex.Message}");
                _log($"[FETCH]   stack: {ex.StackTrace}");
                break;
            }

            IReadOnlyList<Candidate> batch;
            try
            {
                batch = src.ParseResponse(body);
            }
            catch (Exception ex)
            {
                _log($"[FETCH] page {page}: PARSE FAILED — {ex.GetType().Name}: {ex.Message}");
                _log($"[FETCH]   body preview: {body.Substring(0, Math.Min(200, body.Length))}");
                break;
            }

            totalRaw += batch.Count;

            if (batch.Count == 0)
            {
                _log($"[FETCH] page {page}: parser returned 0 candidates — assuming exhausted, " +
                     "wrapping cursor to 1");
                exhausted = true;
                break;
            }

            int pageNew = 0, pageDup = 0;
            foreach (var c in batch)
            {
                var cid = new CanonicalId(src.Name, c.Id).ToString();
                if (seen.Contains(cid))
                {
                    pageDup++;
                    continue;
                }
                collected.Add(c);
                seen.Add(cid);
                pageNew++;
                if (collected.Count >= need) break;
            }
            totalDup += pageDup;

            _log($"[FETCH] page {page}: parsed {batch.Count}  new={pageNew}  dedup={pageDup}  " +
                 $"collected so far: {collected.Count}/{need}");

            if (collected.Count >= need) break;
            page++;
        }

        _log($"[FETCH] done  collected={collected.Count}  walked {page - startPage + 1} page(s)  " +
             $"totalRaw={totalRaw}  totalDedup={totalDup}  exhausted={exhausted}");
        return (collected, exhausted ? 1 : page);
    }

    private async Task<long> DownloadAsync(string url, string dest, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync(url, ct);
        await using var file = File.Create(dest);
        await stream.CopyToAsync(file, ct);
        try { return new FileInfo(dest).Length; }
        catch { return -1; }
    }

    private void EnforceCap(string folder, State state, Config cfg)
    {
        var onDisk = EnumerateFolder(folder);
        var nonSaved = onDisk
            .Where(kv => !state.IsSaved(kv.Key))
            .Select(kv => (cid: kv.Key, path: kv.Value))
            .OrderByDescending(p => File.GetLastWriteTimeUtc(p.path))
            .ToList();

        if (nonSaved.Count <= cfg.MaxWallpapers) return;

        foreach (var (cid, path) in nonSaved.Skip(cfg.MaxWallpapers))
        {
            try
            {
                File.Delete(path);
                state.Of(cid.Source).Images.Remove(cid.ItemId);
                _log($"Pruned to cap: removed {Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                _log($"Could not remove {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private static string Redact(string url) =>
        System.Text.RegularExpressions.Regex.Replace(url, @"(apikey|api_key)=[^&]+", "$1=REDACTED");
}

public sealed record SyncResult(int Retired, int Downloaded, string Mode);
