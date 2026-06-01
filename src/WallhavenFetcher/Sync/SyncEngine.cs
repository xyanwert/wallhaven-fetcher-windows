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
        if (cfg.Frozen && !force)
        {
            _log("Sync is frozen — skipping (this run is automatic; use 'Sync now' to override)");
            return new SyncResult(0, 0, "frozen");
        }

        var src = _registry.Get(cfg.Source);
        var folder = cfg.ResolveFolder();
        Directory.CreateDirectory(folder);

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
        var fitter = new ImageFitter(tw, th, cfg.FitTolerancePct, cfg.CropThresholdPct);

        int downloaded = 0;
        foreach (var c in newCandidates)
        {
            ct.ThrowIfCancellationRequested();
            var cid = new CanonicalId(src.Name, c.Id);
            var dest = Path.Combine(folder, cid.FileName(c.Ext));
            try
            {
                _log($"Downloading {c.Id} ({c.Resolution}) → {Path.GetFileName(dest)}");
                await DownloadAsync(c.Url, dest, ct);
                srcState.Images[c.Id] = fp;
                downloaded++;

                if (cfg.FitToRatio)
                {
                    var (modified, reason) = await fitter.FitInPlaceAsync(dest, ct);
                    if (modified) _log($"  ↳ fit to target ratio: {reason}");
                }
            }
            catch (Exception ex)
            {
                _log($"Failed to download {c.Id}: {ex.Message}");
            }
        }

        _log($"Downloaded {downloaded}/{newCandidates.Count} wallpapers");

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
        var collected = new List<Candidate>();
        var seen = new HashSet<string>(haveCids, StringComparer.OrdinalIgnoreCase);
        int page = startPage;
        const int maxPages = 20;
        bool exhausted = false;

        while (collected.Count < need && page < startPage + maxPages)
        {
            string url = src.BuildUrl(cfg, page);
            _log($"Querying: {Redact(url)}");

            string body;
            try
            {
                body = await _http.GetStringAsync(url, ct);
            }
            catch (HttpRequestException ex)
            {
                _log($"HTTP error on page {page}: {ex.Message}");
                break;
            }

            var batch = src.ParseResponse(body);
            if (batch.Count == 0)
            {
                _log($"Page {page} empty — wrapping cursor to 1");
                exhausted = true;
                break;
            }

            foreach (var c in batch)
            {
                var cid = new CanonicalId(src.Name, c.Id).ToString();
                if (!seen.Contains(cid))
                {
                    collected.Add(c);
                    seen.Add(cid);
                    if (collected.Count >= need) break;
                }
            }
            page++;
        }
        return (collected, exhausted ? 1 : page);
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        await using var stream = await _http.GetStreamAsync(url, ct);
        await using var file = File.Create(dest);
        await stream.CopyToAsync(file, ct);
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
