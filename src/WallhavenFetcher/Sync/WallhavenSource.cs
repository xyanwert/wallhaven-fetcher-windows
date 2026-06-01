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

    public IReadOnlyList<Candidate> ParseResponse(string jsonBody)
    {
        using var doc = JsonDocument.Parse(jsonBody);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Candidate>();
        }

        var list = new List<Candidate>(data.GetArrayLength());
        foreach (var w in data.EnumerateArray())
        {
            var url = w.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(url)) continue;

            string ext = "";
            try
            {
                var u = new Uri(url);
                ext = Path.GetExtension(u.AbsolutePath);
            }
            catch (Exception) { }
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";

            int width  = w.TryGetProperty("dimension_x", out var dx) ? dx.GetInt32() : 0;
            int height = w.TryGetProperty("dimension_y", out var dy) ? dy.GetInt32() : 0;
            var res    = w.TryGetProperty("resolution",  out var rr) ? rr.GetString() ?? "?" : "?";
            var id     = w.TryGetProperty("id",          out var ii) ? ii.GetString() ?? "" : "";

            if (string.IsNullOrEmpty(id)) continue;
            list.Add(new Candidate(id, url, ext, width, height, res));
        }
        return list;
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
