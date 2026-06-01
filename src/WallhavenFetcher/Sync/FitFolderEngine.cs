using WallhavenFetcher.Imaging;

namespace WallhavenFetcher.Sync;

/// <summary>
/// Walk the wallpaper folder (and favorites/) and run ImageFitter over
/// every managed file. Off-ratio files get cropped or padded per the
/// current Config.fit_to_ratio / fit_tolerance_pct / crop_threshold_pct
/// thresholds.
///
/// Equivalent to the macOS `--fit-folder` CLI subcommand.
/// </summary>
public sealed class FitFolderEngine
{
    private readonly Action<string> _log;
    public FitFolderEngine(Action<string> log) => _log = log;

    public async Task<FitFolderResult> RunAsync(Config cfg, CancellationToken ct = default)
    {
        var result = new FitFolderResult();
        if (!cfg.FitToRatio)
        {
            result.Reason = "fit_to_ratio is disabled in config";
            return result;
        }

        var (tw, th) = DisplayDetector.Resolve(cfg.TargetResolution);
        result.TargetResolution = $"{tw}x{th}";
        var fitter = new ImageFitter(tw, th, cfg.FitTolerancePct, cfg.CropThresholdPct);

        var folder = cfg.ResolveFolder();
        var dirs = new[] { folder, FavoritesManager.FavoritesDir(folder) };

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            var files = Directory.EnumerateFiles(dir)
                .Where(p => FileNameHelpers.IsManagedWallpaper(p))
                .ToList();
            _log($"Fitting {files.Count} file(s) in {dir}");

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var (modified, reason) = await fitter.FitInPlaceAsync(f, ct);
                if (modified)
                {
                    _log($"  ✓ {Path.GetFileName(f)}: {reason}");
                    result.Fitted++;
                }
                else if (reason.Contains("within tolerance") ||
                         reason.Contains("already-fits"))
                {
                    result.AlreadyOk++;
                }
                else
                {
                    _log($"  · {Path.GetFileName(f)}: skipped ({reason})");
                    result.Skipped++;
                }
            }
        }

        return result;
    }
}

public sealed class FitFolderResult
{
    public int    Fitted             { get; set; }
    public int    AlreadyOk          { get; set; }
    public int    Skipped            { get; set; }
    public string TargetResolution   { get; set; } = "";
    public string Reason             { get; set; } = "";

    public string Summary() =>
        $"{Fitted} fitted, {AlreadyOk} already OK, {Skipped} skipped " +
        $"(target {TargetResolution})";
}
