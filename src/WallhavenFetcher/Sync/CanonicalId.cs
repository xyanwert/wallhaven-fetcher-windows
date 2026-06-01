namespace WallhavenFetcher.Sync;

/// <summary>
/// "source:id" string pair — wallhaven:abc123, konachan:42, local:d4e1a2f3b8.
/// Saved/banned lists store these so a single list can span multiple sources
/// without collisions. Filenames in the wallpaper folder are
/// "wallhaven-abc123.jpg" etc.; these helpers convert.
/// </summary>
public readonly record struct CanonicalId(string Source, string ItemId)
{
    public override string ToString() => $"{Source}:{ItemId}";

    public static CanonicalId Parse(string s)
    {
        var idx = s.IndexOf(':');
        if (idx < 0) throw new FormatException($"Not a canonical id: {s}");
        return new CanonicalId(s[..idx], s[(idx + 1)..]);
    }

    public static bool TryParse(string s, out CanonicalId cid)
    {
        var idx = s.IndexOf(':');
        if (idx < 0) { cid = default; return false; }
        cid = new CanonicalId(s[..idx], s[(idx + 1)..]);
        return true;
    }

    /// <summary>Filename in folder, e.g. "wallhaven-abc123.jpg".</summary>
    public string FileName(string extension) => $"{Source}-{ItemId}{extension}";

    /// <summary>Filename glob pattern (no extension), e.g. "wallhaven-abc123.*".</summary>
    public string FileGlob() => $"{Source}-{ItemId}.*";
}

public static class FileNameHelpers
{
    // wallhaven-abc.jpg, konachan-42.png, local-d4e1.jpg
    private static readonly System.Text.RegularExpressions.Regex NameRe = new(
        @"^(?<src>wallhaven|konachan|local)-(?<id>[A-Za-z0-9]+)\.(?<ext>\w+)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static CanonicalId? TryParse(string fileName)
    {
        var m = NameRe.Match(System.IO.Path.GetFileName(fileName));
        if (!m.Success) return null;
        return new CanonicalId(m.Groups["src"].Value, m.Groups["id"].Value);
    }

    public static bool IsManagedWallpaper(string fileName)
        => NameRe.IsMatch(System.IO.Path.GetFileName(fileName));
}
