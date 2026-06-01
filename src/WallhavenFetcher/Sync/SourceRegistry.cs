namespace WallhavenFetcher.Sync;

/// <summary>
/// Registry of available sources. Add new sources here; everything else
/// routes via Config.Source string.
/// </summary>
public sealed class SourceRegistry
{
    private readonly Dictionary<string, ISource> _sources;

    public SourceRegistry(string? apiKey)
    {
        _sources = new Dictionary<string, ISource>(StringComparer.OrdinalIgnoreCase)
        {
            ["wallhaven"] = new WallhavenSource(apiKey),
            ["konachan"]  = new KonachanSource(),
            // "local" has no API — handled separately by import command
        };
    }

    public ISource Get(string name)
    {
        if (_sources.TryGetValue(name, out var s)) return s;
        return _sources["wallhaven"];  // fallback
    }

    public IEnumerable<ISource> All => _sources.Values;
    public IEnumerable<string> Names => _sources.Keys;

    /// <summary>
    /// Try each registered source's URL parser; return first match's
    /// (source_name, preset) tuple.
    /// </summary>
    public (string Source, Dictionary<string, object?> Preset)? ParseUrl(string urlString)
    {
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var url)) return null;
        foreach (var src in _sources.Values)
        {
            var preset = src.TryParseUrlToPreset(url);
            if (preset is not null) return (src.Name, preset);
        }
        return null;
    }
}
