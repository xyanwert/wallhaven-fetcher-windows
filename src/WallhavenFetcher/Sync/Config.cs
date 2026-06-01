using System.Text.Json;
using System.Text.Json.Serialization;

namespace WallhavenFetcher.Sync;

/// <summary>
/// All search-affecting + behavior knobs. Defaults here; user overrides live
/// in %APPDATA%\WallhavenFetcher\config.json under "overrides". Presets stored
/// under "presets" in the same file.
/// </summary>
public sealed class Config
{
    // Source routing
    public string Source { get; set; } = "wallhaven";

    // Wallhaven mini-DSL query / free-text. Empty = no filter.
    //   "landscape"               plain text
    //   "+nature +sunset"         must have both tags
    //   "+landscape -people"      has landscape, excludes people
    //   "id:64"                   tag id
    //   "like:wallpaper_id"       similar to a given image
    public string Q { get; set; } = "";

    // Wallhaven 3-bit bitmasks (general/anime/people, sfw/suggestive/restricted).
    public string Categories { get; set; } = "111";
    public string Purity { get; set; } = "100";

    // Sort + order
    public string Sorting { get; set; } = "toplist";
    public string Order { get; set; } = "";         // "asc" / "desc" / "" (default)
    public string TopRange { get; set; } = "1M";    // only when sorting=toplist

    // Optional filters — empty = not sent to API
    public string Atleast { get; set; } = "";       // e.g. "1920x1080"
    public string Ratios { get; set; } = "";        // e.g. "16x9,16x10"

    // Folder + cap
    public string Folder { get; set; } = "";        // empty = default ~/Pictures/Wallhaven
    public int MaxWallpapers { get; set; } = 100;
    public int NewPerRun { get; set; } = 10;
    public int RolloutPct { get; set; } = 10;

    // Fit-to-target
    public bool FitToRatio { get; set; } = true;
    public string TargetResolution { get; set; } = "";  // empty = auto-detect display
    public int FitTolerancePct { get; set; } = 12;
    public int CropThresholdPct { get; set; } = 10;

    /// <summary>Resolve folder path, defaulting to %USERPROFILE%\Pictures\Wallhaven.</summary>
    public string ResolveFolder()
    {
        if (!string.IsNullOrWhiteSpace(Folder)) return Folder;
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(pictures, "Wallhaven");
    }

    public Config Clone()
    {
        var s = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<Config>(s)!;
    }
}

/// <summary>
/// On-disk config file. Defaults live in code; overrides+presets live here.
/// </summary>
public sealed class ConfigFile
{
    public Dictionary<string, JsonElement> Overrides { get; set; } = new();
    public Dictionary<string, Dictionary<string, JsonElement>> Presets { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ConfigFile Load(string path)
    {
        if (!File.Exists(path)) return new ConfigFile();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ConfigFile>(json, JsonOpts) ?? new ConfigFile();
        }
        catch (Exception)
        {
            return new ConfigFile();
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>Build effective Config by merging defaults with overrides.</summary>
    public Config MaterializeEffective()
    {
        var cfg = new Config();
        var json = JsonSerializer.Serialize(cfg, JsonOpts);
        var node = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        foreach (var kvp in Overrides) node[kvp.Key] = kvp.Value;
        return JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(node, JsonOpts), JsonOpts)!;
    }
}

/// <summary>
/// File paths under %APPDATA%\WallhavenFetcher\.
/// </summary>
public static class Paths
{
    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "WallhavenFetcher");

    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string StateFile  => Path.Combine(ConfigDir, "state.json");
    public static string ApiKeyFile => Path.Combine(ConfigDir, "api_key");
    public static string LogFile    => Path.Combine(ConfigDir, "Logs", "sync.log");
}
