using Microsoft.Win32;
using WallhavenFetcher.Sync;

namespace WallhavenFetcher.Wallpaper;

/// <summary>
/// Find the wallpaper file currently displayed by Windows, restricted to
/// files inside our wallpaper folder.
///
/// Detection layers, in order:
///   1. HKCU\Control Panel\Desktop\Wallpaper        — the current static or
///      currently-displayed slideshow image path. Reliable on most setups.
///   2. HKCU\...\Explorer\Wallpapers\BackgroundHistoryPath0  — slideshow
///      history list, index 0 is "most recent". Used when (1) is stale or
///      points to a generated cache.
///   3. Folder atime/last-access fallback — find the wallhaven-*/konachan-*/
///      local-* file in our folder with the newest LastAccessTime. Windows
///      bumps atime when Slideshow reads a file. Works when the registry
///      paths can't be resolved (e.g. multi-monitor with different sources).
/// </summary>
public static class CurrentWallpaperDetector
{
    /// <summary>
    /// Returns canonical IDs for the currently-displayed wallpaper(s) that
    /// live in our wallpaper folder, newest-likely-first. Empty if nothing
    /// matches.
    /// </summary>
    public static IReadOnlyList<CanonicalId> Detect(string folder, int topN = 1)
    {
        var folderFull = Path.GetFullPath(folder);
        var results = new List<CanonicalId>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string? path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(folderFull, StringComparison.OrdinalIgnoreCase)) return;
                if (!seen.Add(full)) return;
                var cid = FileNameHelpers.TryParse(full);
                if (cid is not null && results.All(r => !r.Equals(cid.Value)))
                    results.Add(cid.Value);
            }
            catch { }
        }

        // Layer 1: HKCU\Control Panel\Desktop\Wallpaper
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            TryAdd(k?.GetValue("Wallpaper") as string);
        }
        catch { }

        // Layer 2: BackgroundHistoryPath0..4 (slideshow rotation history)
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers");
            if (k is not null)
            {
                for (int i = 0; i < 5 && results.Count < topN; i++)
                {
                    TryAdd(k.GetValue($"BackgroundHistoryPath{i}") as string);
                }
            }
        }
        catch { }

        if (results.Count >= topN) return results.Take(topN).ToList();

        // Layer 3: atime fallback within our folder
        try
        {
            var byAtime = Directory.EnumerateFiles(folder)
                .Where(p => FileNameHelpers.IsManagedWallpaper(p))
                .Select(p => (path: p, atime: File.GetLastAccessTimeUtc(p)))
                .OrderByDescending(x => x.atime)
                .Take(topN);

            foreach (var (path, _) in byAtime)
            {
                TryAdd(path);
                if (results.Count >= topN) break;
            }
        }
        catch { }

        return results;
    }
}
