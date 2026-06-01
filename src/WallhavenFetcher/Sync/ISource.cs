namespace WallhavenFetcher.Sync;

/// <summary>
/// One image candidate returned by a source's search. Source-specific id is
/// the bare per-source id (NOT canonical); the engine pairs with source name
/// when building canonical IDs and file paths.
/// </summary>
public sealed record Candidate(
    string Id,
    string Url,
    string Ext,        // ".jpg", ".png", etc. (with leading dot)
    int Width,
    int Height,
    string Resolution  // human-readable "1920x1080" or "?"
);

/// <summary>
/// Wallpaper source abstraction. Each source knows how to:
///   - build a search URL for page N from the current Config
///   - parse the API response into Candidate records
///   - declare which Config keys participate in its fingerprint
///   - optionally parse one of its own /search URLs into a preset dict
/// </summary>
public interface ISource
{
    string Name { get; }
    IReadOnlyCollection<string> FingerprintKeys { get; }

    /// <summary>Build the API URL for a given page (1-indexed).</summary>
    string BuildUrl(Config cfg, int page);

    /// <summary>Parse the JSON response body into candidates.</summary>
    IReadOnlyList<Candidate> ParseResponse(string jsonBody);

    /// <summary>
    /// Try to parse a /search URL from this source's website into a preset
    /// (search-affecting Config overrides). Returns null if URL isn't this
    /// source's domain.
    /// </summary>
    Dictionary<string, object?>? TryParseUrlToPreset(Uri url);
}
