using System.Text.Json;
using System.Web;

namespace WallhavenFetcher.Sync;

public sealed class WallhavenSource : ISource
{
    public string Name => "wallhaven";

    public IReadOnlyCollection<string> FingerprintKeys { get; } = new[]
    {
        "q", "categories", "purity", "sorting", "order", "atleast", "ratios"
    };

    private const string ApiBase = "https://wallhaven.cc/api/v1/search";
    private readonly string? _apiKey;

    public WallhavenSource(string? apiKey) => _apiKey = apiKey;

    public string BuildUrl(Config cfg, int page)
    {
        var qs = HttpUtility.ParseQueryString("");
        qs["categories"] = cfg.Categories;
        qs["purity"]     = cfg.Purity;
        qs["sorting"]    = cfg.Sorting;
        qs["page"]       = page.ToString();

        if (!string.IsNullOrEmpty(cfg.Q))       qs["q"]       = cfg.Q;
        if (!string.IsNullOrEmpty(cfg.Order))   qs["order"]   = cfg.Order;
        if (!string.IsNullOrEmpty(cfg.Atleast)) qs["atleast"] = cfg.Atleast;
        if (!string.IsNullOrEmpty(cfg.Ratios))  qs["ratios"]  = cfg.Ratios;
        if (cfg.Sorting == "toplist" && !string.IsNullOrEmpty(cfg.TopRange))
            qs["topRange"] = cfg.TopRange;
        if (!string.IsNullOrEmpty(_apiKey))
            qs["apikey"] = _apiKey;

        return $"{ApiBase}?{qs}";
    }

    /// <summary>Logger for parse diagnostics — set by SyncEngine before calling ParseResponse.</summary>
    public Action<string>? Log { get; set; }

    public IReadOnlyList<Candidate> ParseResponse(string jsonBody)
    {
        using var doc = JsonDocument.Parse(jsonBody);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            Log?.Invoke("[wallhaven.parse] no 'data' array in response — " +
                        $"root kind: {doc.RootElement.ValueKind}");
            return Array.Empty<Candidate>();
        }

        int rawCount = data.GetArrayLength();
        var list = new List<Candidate>(rawCount);
        int rejectedNoPath = 0, rejectedNoId = 0, rejectedException = 0;

        foreach (var w in data.EnumerateArray())
        {
            try
            {
                var url = w.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url)) { rejectedNoPath++; continue; }

                string ext = "";
                try
                {
                    var u = new Uri(url);
                    ext = Path.GetExtension(u.AbsolutePath);
                }
                catch (Exception) { }
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";

                int width  = TryGetInt32(w, "dimension_x");
                int height = TryGetInt32(w, "dimension_y");
                var res    = w.TryGetProperty("resolution",  out var rr) ? rr.GetString() ?? "?" : "?";

                // Wallhaven returns id as a string; tolerate numeric just in case.
                string id;
                if (w.TryGetProperty("id", out var ii))
                {
                    id = ii.ValueKind switch
                    {
                        JsonValueKind.String => ii.GetString() ?? "",
                        JsonValueKind.Number => ii.GetInt64().ToString(),
                        _ => "",
                    };
                }
                else id = "";
                if (string.IsNullOrEmpty(id)) { rejectedNoId++; continue; }

                list.Add(new Candidate(id, url, ext, width, height, res));
            }
            catch (Exception ex)
            {
                rejectedException++;
                Log?.Invoke($"[wallhaven.parse] per-item exception: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Log?.Invoke($"[wallhaven.parse] raw={rawCount} kept={list.Count} " +
                    $"rejected: noPath={rejectedNoPath} noId={rejectedNoId} exc={rejectedException}");
        return list;
    }

    private static int TryGetInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : (int)v.GetDouble(),
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => 0,
        };
    }

    public Dictionary<string, object?>? TryParseUrlToPreset(Uri url)
    {
        if (!url.Host.Contains("wallhaven", StringComparison.OrdinalIgnoreCase)) return null;

        var qs = HttpUtility.ParseQueryString(url.Query);
        var preset = new Dictionary<string, object?>();

        void CopyIf(string key)
        {
            var v = qs[key];
            if (!string.IsNullOrEmpty(v)) preset[key] = v;
        }
        CopyIf("q");
        CopyIf("categories");
        CopyIf("purity");
        CopyIf("sorting");
        CopyIf("order");
        CopyIf("topRange");
        CopyIf("atleast");
        CopyIf("ratios");

        // &rollout=N (0-100) → rollout_pct extension
        var rollout = qs["rollout"];
        if (!string.IsNullOrEmpty(rollout) && int.TryParse(rollout, out var n) && n >= 0 && n <= 100)
        {
            preset["rolloutPct"] = n;
        }

        return preset;
    }
}
