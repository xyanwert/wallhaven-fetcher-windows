using System.Security.Cryptography;
using WallhavenFetcher.Imaging;

namespace WallhavenFetcher.Sync;

/// <summary>
/// Move user-supplied image files into the wallpaper folder as
/// local-&lt;sha1prefix&gt;.&lt;ext&gt;. Each imported file is auto-saved,
/// auto-copied to favorites/, and run through fit_to_target.
///
/// Accepts directories: walks recursively, ignores non-image extensions,
/// skips files already inside the wallpaper folder tree.
/// </summary>
public sealed class ImportEngine
{
    private static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tiff", ".tif"
    };

    private readonly Action<string> _log;
    public ImportEngine(Action<string> log) => _log = log;

    public async Task<ImportResult> ImportAsync(
        IEnumerable<string> paths, State state, Config cfg,
        CancellationToken ct = default)
    {
        var result = new ImportResult();
        var folder = cfg.ResolveFolder();
        Directory.CreateDirectory(folder);
        var folderFull = Path.GetFullPath(folder);

        // Expand directories into individual files
        var files = new List<string>();
        foreach (var raw in paths)
        {
            try
            {
                if (Directory.Exists(raw))
                {
                    int before = files.Count;
                    foreach (var f in Directory.EnumerateFiles(raw, "*", SearchOption.AllDirectories))
                    {
                        if (!SupportedExts.Contains(Path.GetExtension(f))) continue;
                        // Skip anything already inside our wallpaper folder
                        var ff = Path.GetFullPath(f);
                        if (ff.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase)) continue;
                        files.Add(f);
                    }
                    _log($"{raw}: found {files.Count - before} image(s)");
                }
                else if (File.Exists(raw))
                {
                    files.Add(raw);
                }
                else
                {
                    _log($"Not found: {raw}");
                    result.Failed.Add(raw);
                }
            }
            catch (Exception ex)
            {
                _log($"Couldn't enumerate {raw}: {ex.Message}");
                result.Failed.Add(raw);
            }
        }

        if (files.Count == 0)
        {
            result.NoneFound = true;
            return result;
        }

        var (tw, th) = DisplayDetector.Resolve(cfg.TargetResolution);
        var fitter = cfg.FitToRatio
            ? new ImageFitter(tw, th, cfg.FitTolerancePct, cfg.CropThresholdPct)
            : null;

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
