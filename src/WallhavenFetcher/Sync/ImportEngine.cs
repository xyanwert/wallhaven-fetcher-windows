using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WallhavenFetcher.Imaging;

namespace WallhavenFetcher.Sync;

/// <summary>
/// Move user-supplied image files into the wallpaper folder as
/// local-&lt;sha1prefix&gt;.&lt;ext&gt;. Each imported file is auto-saved,
/// auto-copied to favorites/, and run through fit_to_target.
///
/// Accepts:
///   • files     — moved with content-hash naming
///   • directories — walked recursively
///   • http(s) URLs — downloaded; wallhaven/konachan CDN naming is recognized
///                    so the file lands with its canonical &lt;source&gt;-&lt;id&gt;
///                    instead of falling back to local-hash
/// </summary>
public sealed class ImportEngine
{
    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tiff", ".tif"
    };

    // Detect canonical filenames embedded in CDN URLs so URL imports preserve
    // the source ID (and therefore line up with normal sync entries) instead
    // of becoming opaque local-hash files.
    private static readonly Regex CdnWallhavenRe = new(
        @"/wallhaven-(?<id>[A-Za-z0-9]+)\.(?<ext>jpg|jpeg|png|webp|gif|bmp)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CdnKonachanRe = new(
        @"[Kk]onachan\.com\s*-\s*(?<id>\d+)\s",
        RegexOptions.Compiled);

    private static readonly Regex KonachanExtRe = new(
        @"\.(?<ext>jpg|jpeg|png|webp|gif|bmp)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Action<string> _log;
    private readonly HttpClient _http;
    public ImportEngine(Action<string> log, HttpClient? http = null)
    {
        _log = log;
        _http = http ?? new HttpClient();
    }

    public async Task<ImportResult> ImportAsync(
        IEnumerable<string> paths, State state, Config cfg,
        CancellationToken ct = default)
    {
        var result = new ImportResult();
        var folder = cfg.ResolveFolder();
        Directory.CreateDirectory(folder);
        var folderFull = Path.GetFullPath(folder);

        // ── Split inputs ──────────────────────────────────────────────
        var urls  = new List<string>();
        var files = new List<string>();

        foreach (var raw in paths)
        {
            if (raw is null) continue;
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                urls.Add(trimmed);
                continue;
            }

            try
            {
                if (Directory.Exists(trimmed))
                {
                    int before = files.Count;
                    foreach (var f in Directory.EnumerateFiles(trimmed, "*", SearchOption.AllDirectories))
                    {
                        if (!SupportedExts.Contains(Path.GetExtension(f))) continue;
                        // Skip anything already inside our wallpaper folder
                        var ff = Path.GetFullPath(f);
                        if (ff.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase)) continue;
                        files.Add(f);
                    }
                    _log($"{trimmed}: found {files.Count - before} image(s)");
                }
                else if (File.Exists(trimmed))
                {
                    files.Add(trimmed);
                }
                else
                {
                    _log($"Not found: {trimmed}");
                    result.Failed.Add(trimmed);
                }
            }
            catch (Exception ex)
            {
                _log($"Couldn't enumerate {trimmed}: {ex.Message}");
                result.Failed.Add(trimmed);
            }
        }

        // Fitter is shared across URL + file phases.
        var (tw, th) = DisplayDetector.Resolve(cfg.TargetResolution);
        var fitter = cfg.FitToRatio
            ? new ImageFitter(tw, th, cfg.FitTolerancePct, cfg.CropThresholdPct,
                                     cfg.MaxImageDimFactor)
            : null;

        // ── URL download phase ────────────────────────────────────────
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ImportOneUrlAsync(url, folder, fitter, state, cfg, result, ct);
            }
            catch (Exception ex)
            {
                _log($"URL import failed for {url}: {ex.Message}");
                result.Failed.Add(url);
            }
        }

        if (files.Count == 0 && urls.Count == 0)
        {
            result.NoneFound = true;
            return result;
        }

        // ── File-move phase ───────────────────────────────────────────
        foreach (var src in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ImportOneAsync(src, folder, fitter, state, result, ct);
            }
            catch (Exception ex)
            {
                _log($"Failed import {Path.GetFileName(src)}: {ex.Message}");
                result.Failed.Add(Path.GetFileName(src));
            }
        }
        state.Save(Paths.StateFile);
        return result;
    }

    /// <summary>
    /// Download a URL into the wallpaper folder. If the URL matches a known
    /// CDN's canonical naming (wallhaven, konachan) the file is saved under
    /// &lt;source&gt;-&lt;id&gt;.&lt;ext&gt; and that canonical ID is tagged with
    /// the current source fingerprint so it isn't pruned as stale. Otherwise
    /// it's downloaded to a temp file and routed through the local-hash flow.
    /// </summary>
    private async Task ImportOneUrlAsync(
        string url, string folder, ImageFitter? fitter, State state, Config cfg,
        ImportResult result, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _log($"Not a valid URL: {url}");
            result.Failed.Add(url);
            return;
        }
        var path = Uri.UnescapeDataString(uri.AbsolutePath);

        // Try wallhaven first, then konachan.
        var mw = CdnWallhavenRe.Match(path);
        var mk = CdnKonachanRe.Match(path);

        if (mw.Success)
        {
            var id  = mw.Groups["id"].Value;
            var ext = "." + mw.Groups["ext"].Value.ToLowerInvariant();
            await DownloadCanonicalAsync(url, "wallhaven", id, ext,
                                          folder, fitter, state, cfg, result, ct);
            return;
        }

        if (mk.Success)
        {
            var id = mk.Groups["id"].Value;
            var extM = KonachanExtRe.Match(path);
            var ext = extM.Success ? "." + extM.Groups["ext"].Value.ToLowerInvariant() : ".jpg";
            await DownloadCanonicalAsync(url, "konachan", id, ext,
                                          folder, fitter, state, cfg, result, ct);
            return;
        }

        // ── Generic URL: download to temp, then content-hash like a local file ──
        var ext2 = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext2) || !SupportedExts.Contains(ext2))
        {
            _log($"URL: unsupported / missing extension on path; skipping: {url}");
            result.Failed.Add(url);
            return;
        }
        var tmpName = $".import-url-{Guid.NewGuid():N}{ext2}";
        var tmpPath = Path.Combine(folder, tmpName);
        _log($"Downloading URL (generic) → temp, will hash → local-…{ext2}");
        await DownloadToFileAsync(url, tmpPath, ct);
        // Hand off to the file-import path, which will hash + rename + fit + favorite.
        await ImportOneAsync(tmpPath, folder, fitter, state, result, ct);
    }

    private async Task DownloadCanonicalAsync(
        string url, string source, string itemId, string ext,
        string folder, ImageFitter? fitter, State state, Config cfg,
        ImportResult result, CancellationToken ct)
    {
        var cid  = new CanonicalId(source, itemId);
        var dest = Path.Combine(folder, cid.FileName(ext));

        if (File.Exists(dest))
        {
            _log($"URL import: already in folder — {Path.GetFileName(dest)}");
            if (!state.IsSaved(cid)) state.AddSaved(cid);
            FavoritesManager.Ensure(cid, dest, folder);
            result.AlreadyImported.Add(cid);
            return;
        }

        _log($"Downloading URL → {Path.GetFileName(dest)}");
        await DownloadToFileAsync(url, dest, ct);

        // Tag the new entry with the current fingerprint of its source so a
        // later sync doesn't immediately flag it as orphaned/stale.
        try
        {
            var fp = Fingerprint.Compute(cfg, source, Array.Empty<string>());
            state.Of(source).Images[itemId] = fp;
        }
        catch (Exception ex)
        {
            // Fingerprint failure shouldn't abort the import; just log it.
            _log($"  ↳ (could not stamp fingerprint, will still import): {ex.Message}");
        }

        state.AddSaved(cid);
        result.Imported.Add(cid);

        if (fitter is not null)
        {
            var (modified, reason) = await fitter.FitInPlaceAsync(dest, ct);
            if (modified)
            {
                _log($"  ↳ fit to target ratio: {reason}");
                result.Fitted.Add(cid);
            }
        }

        FavoritesManager.Ensure(cid, dest, folder);
    }

    private async Task DownloadToFileAsync(string url, string dest, CancellationToken ct)
    {
        using var req  = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("WallhavenFetcher/1.0 (+personal)");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(dest);
        await resp.Content.CopyToAsync(fs, ct);
    }

    private async Task ImportOneAsync(
        string src, string folder, ImageFitter? fitter, State state,
        ImportResult result, CancellationToken ct)
    {
        var ext = Path.GetExtension(src).ToLowerInvariant();
        if (!SupportedExts.Contains(ext))
        {
            result.Failed.Add(Path.GetFileName(src));
            return;
        }

        // Content hash for stable ID
        string hashId;
        await using (var stream = File.OpenRead(src))
        {
            var hash = await SHA1.HashDataAsync(stream, ct);
            hashId = Convert.ToHexString(hash)[..10].ToLowerInvariant();
        }
        var cid = new CanonicalId("local", hashId);
        var dest = Path.Combine(folder, cid.FileName(ext));

        if (File.Exists(dest))
        {
            _log($"Already imported: {Path.GetFileName(dest)}");
            result.AlreadyImported.Add(cid);
            if (!state.IsSaved(cid)) state.AddSaved(cid);
            FavoritesManager.Ensure(cid, dest, folder);
            // Still remove the duplicate at the original location ("move" semantic)
            if (!string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest),
                                StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(src); _log($"Removed duplicate at original: {src}"); }
                catch (Exception ex) { _log($"  ↳ could not remove {src}: {ex.Message}"); }
            }
            return;
        }

        File.Move(src, dest);
        _log($"Imported (moved) {Path.GetFileName(src)} → {Path.GetFileName(dest)}");
        state.AddSaved(cid);
        result.Imported.Add(cid);

        if (fitter is not null)
        {
            var (modified, reason) = await fitter.FitInPlaceAsync(dest, ct);
            if (modified)
            {
                _log($"  ↳ fit to target ratio: {reason}");
                result.Fitted.Add(cid);
            }
        }

        FavoritesManager.Ensure(cid, dest, folder);
    }
}

public sealed class ImportResult
{
    public List<CanonicalId> Imported        { get; } = new();
    public List<CanonicalId> AlreadyImported { get; } = new();
    public List<CanonicalId> Fitted          { get; } = new();
    public List<string>      Failed          { get; } = new();
    public bool              NoneFound       { get; set; }

    public bool Any() => Imported.Count > 0 || AlreadyImported.Count > 0;
}
