namespace WallhavenFetcher.Sync;

/// <summary>
/// Maintain a `favorites/` subfolder inside the wallpaper folder. Saved files
/// get a real copy here so the user can configure a second Windows Slideshow
/// source pointed at curated favorites only.
/// </summary>
public static class FavoritesManager
{
    public static string FavoritesDir(string folder) => Path.Combine(folder, "favorites");

    /// <summary>
    /// Ensure favorites/&lt;source&gt;-&lt;id&gt;.&lt;ext&gt; exists. Idempotent.
    /// Returns true if a copy was made.
    /// </summary>
    public static bool Ensure(CanonicalId cid, string sourceFilePath, string folder)
    {
        var fav = FavoritesDir(folder);
        Directory.CreateDirectory(fav);
        var dest = Path.Combine(fav, Path.GetFileName(sourceFilePath));
        if (File.Exists(dest)) return false;
        try
        {
            File.Copy(sourceFilePath, dest);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Delete any favorites/&lt;source&gt;-&lt;id&gt;.* files. Returns the names removed.
    /// </summary>
    public static IReadOnlyList<string> Remove(CanonicalId cid, string folder)
    {
        var fav = FavoritesDir(folder);
        if (!Directory.Exists(fav)) return Array.Empty<string>();
        var removed = new List<string>();
        foreach (var f in Directory.EnumerateFiles(fav, cid.FileGlob()))
        {
            try
            {
                File.Delete(f);
                removed.Add(Path.GetFileName(f));
            }
            catch { }
        }
        return removed;
    }
}
