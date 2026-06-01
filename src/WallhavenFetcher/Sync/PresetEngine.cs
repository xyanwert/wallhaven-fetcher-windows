using System.Text.Json;

namespace WallhavenFetcher.Sync;

/// <summary>
/// Apply / save / delete / create-from-URL preset operations.
/// PRESET_KEYS are the search-affecting + behavior knobs that participate.
/// Applying a preset wipes those keys from overrides then sets only what
/// the preset specifies — clean source-of-truth semantics.
/// </summary>
public sealed class PresetEngine
{
    private static readonly string[] PresetKeys = new[]
    {
        "source", "q", "categories", "purity", "sorting", "order",
        "topRange", "atleast", "ratios",
        "maxWallpapers", "rolloutPct", "newPerRun",
    };

    private readonly SourceRegistry _registry;
    public PresetEngine(SourceRegistry registry) => _registry = registry;

    public IReadOnlyDictionary<string, Dictionary<string, JsonElement>> List(ConfigFile cfg)
        => cfg.Presets;

    public bool Apply(string name, ConfigFile cfg)
    {
        if (!cfg.Presets.TryGetValue(name, out var preset)) return false;

        foreach (var k in PresetKeys) cfg.Overrides.Remove(k);
        foreach (var kvp in preset) cfg.Overrides[kvp.Key] = kvp.Value;

        cfg.Save(Paths.ConfigFile);
        return true;
    }

    public void SaveCurrent(string name, ConfigFile cfg)
    {
        var eff = cfg.MaterializeEffective();
        var json = JsonSerializer.Serialize(eff,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        using var doc = JsonDocument.Parse(json);

        var preset = new Dictionary<string, JsonElement>();
        foreach (var k in PresetKeys)
        {
            if (doc.RootElement.TryGetProperty(k, out var v))
                preset[k] = v.Clone();
        }
        cfg.Presets[name] = preset;
        cfg.Save(Paths.ConfigFile);
    }

    public bool Delete(string name, ConfigFile cfg)
    {
        if (!cfg.Presets.Remove(name)) return false;
        cfg.Save(Paths.ConfigFile);
        return true;
    }

    public string? CreateFromUrl(string name, string url, ConfigFile cfg)
    {
        var parsed = _registry.ParseUrl(url);
        if (parsed is null) return null;

        var preset = new Dictionary<string, JsonElement>();
        // Bake source into preset so applying switches active source.
        preset["source"] = JsonSerializer.SerializeToElement(parsed.Value.Source);
        foreach (var (k, v) in parsed.Value.Preset)
        {
            if (v is null) continue;
            preset[k] = JsonSerializer.SerializeToElement(v);
        }
        cfg.Presets[name] = preset;
        cfg.Save(Paths.ConfigFile);
        return parsed.Value.Source;
    }
}
