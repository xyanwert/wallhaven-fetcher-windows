using System.Text.Json;

namespace WallhavenFetcher.Sync;

/// <summary>
/// state.json — per-source images+cursor, unified saved/banned (canonical IDs).
///
/// Schema 2:
/// {
///   "_schema": 2,
///   "wallhaven": { "images": { id: fp }, "cursor": { fp: page } },
///   "konachan":  { "images": { id: fp }, "cursor": { fp: page } },
///   "local":     { "images": { id: fp }, "cursor": { fp: page } },
///   "saved":  ["wallhaven:id", "konachan:id", ...],
///   "banned": ["wallhaven:id", ...]
/// }
/// </summary>
public sealed class State
{
    private const int SCHEMA = 2;
    private static readonly string[] KnownSources = { "wallhaven", "konachan", "local" };

    public int Schema { get; set; } = SCHEMA;

    public Dictionary<string, SourceState> Sources { get; set; } = new();

    public List<string> Saved { get; set; } = new();
    public List<string> Banned { get; set; } = new();

    public SourceState Of(string source)
    {
        if (!Sources.TryGetValue(source, out var s))
        {
            s = new SourceState();
            Sources[source] = s;
        }
        return s;
    }

    public bool IsSaved(CanonicalId cid)  => Saved.Contains(cid.ToString());
    public bool IsBanned(CanonicalId cid) => Banned.Contains(cid.ToString());

    public void AddSaved(CanonicalId cid)
    {
        var s = cid.ToString();
        if (!Saved.Contains(s)) Saved.Add(s);
    }

    public void RemoveSaved(CanonicalId cid) => Saved.Remove(cid.ToString());

    public void AddBanned(CanonicalId cid)
    {
        var s = cid.ToString();
        if (!Banned.Contains(s)) Banned.Add(s);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static State Load(string path)
    {
        if (!File.Exists(path)) return WithDefaults(new State());
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var state = MaterializeFromJson(doc.RootElement);
            return WithDefaults(state);
        }
        catch (Exception)
        {
            return WithDefaults(new State());
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Custom serialization to match the on-disk schema layout
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WriteNumber("_schema", Schema);
        writer.WritePropertyName("saved");
        JsonSerializer.Serialize(writer, Saved.OrderBy(x => x).ToList());
        writer.WritePropertyName("banned");
        JsonSerializer.Serialize(writer, Banned.OrderBy(x => x).ToList());

        foreach (var src in KnownSources)
        {
            writer.WritePropertyName(src);
            writer.WriteStartObject();
            var s = Of(src);
            writer.WritePropertyName("images");
            JsonSerializer.Serialize(writer, s.Images);
            writer.WritePropertyName("cursor");
            JsonSerializer.Serialize(writer, s.Cursor);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static State WithDefaults(State s)
    {
        foreach (var src in KnownSources) s.Of(src);
        return s;
    }

    private static State MaterializeFromJson(JsonElement root)
    {
        var s = new State();
        if (root.TryGetProperty("_schema", out var sch)) s.Schema = sch.GetInt32();
        if (root.TryGetProperty("saved", out var sv) && sv.ValueKind == JsonValueKind.Array)
            s.Saved = sv.EnumerateArray().Select(e => e.GetString()!).ToList();
        if (root.TryGetProperty("banned", out var bn) && bn.ValueKind == JsonValueKind.Array)
            s.Banned = bn.EnumerateArray().Select(e => e.GetString()!).ToList();

        foreach (var src in KnownSources)
        {
            if (!root.TryGetProperty(src, out var srcEl)) continue;
            var ss = new SourceState();
            if (srcEl.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Object)
                foreach (var p in imgs.EnumerateObject())
                    ss.Images[p.Name] = p.Value.GetString() ?? "";
            if (srcEl.TryGetProperty("cursor", out var cur) && cur.ValueKind == JsonValueKind.Object)
                foreach (var p in cur.EnumerateObject())
                    ss.Cursor[p.Name] = p.Value.GetInt32();
            s.Sources[src] = ss;
        }
        return s;
    }
}

public sealed class SourceState
{
    /// <summary>item_id → fingerprint hash (12-char hex) at time of download.</summary>
    public Dictionary<string, string> Images { get; set; } = new();

    /// <summary>fingerprint → next API page to fetch (for stable-sort sources).</summary>
    public Dictionary<string, int> Cursor { get; set; } = new();
}
