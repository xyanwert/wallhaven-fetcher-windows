using System.Text.Json;
using System.Web;

namespace WallhavenFetcher.Sync;

/// <summary>
/// Konachan is a "booru" — tags-only API. We fold our existing Config knobs
/// into the single `tags` query parameter.
/// </summary>
public sealed class KonachanSource : ISource
{
    public string Name => "konachan";

    public IReadOnlyCollection<string> FingerprintKeys { get; } = new[]
    {
        "q", "purity", "sorting", "order", "atleast", "ratios"
    };

    private const string ApiBase = "https://konachan.com/post.json";

    // purity bitmask → list of rating: tags (booru can't OR multiple ratings)
    private static readonly Dictionary<string, string[]> PurityToRatings = new()
    {
        ["100"] = new[] { "rating:s" },
        ["010"] = new[] { "rating:q" },
        ["001"] = new[] { "rating:e" },
        ["110"] = new[] { "rating:s", "rating:q" },
        ["101"] = new[] { "rating:s", "rating:e" },
        ["011"] = new[] { "rating:q", "rating:e" },
        ["111"] = Array.Empty<string>(),
    };

    private static readonly Dictionary<string, string> SortingToTag = new()
    {
        ["random"]     = "order:random",
        ["toplist"]    = "order:score",
        ["views"]      = "order:score",
        ["favorites"]  = "order:score",
        ["date_added"] = "order:id_desc",
        ["relevance"]  = "",  // konachan has no relevance sort
    };

    public string BuildUrl(Config cfg, int page)
    {
        var tags = new List<string>();
        if (!string.IsNullOrEmpty(cfg.Q)) tags.Add(cfg.Q);

        if (PurityToRatings.TryGetValue(cfg.Purity, out var ratings) && ratings.Length > 0)
        {
            tags.Add(ratings[0]);  // booru can't OR; take first
        }

        if (SortingToTag.TryGetValue(cfg.Sorting, out var sortTag) && !string.IsNullOrEmpty(sortTag))
            tags.Add(sortTag);

        if (!string.IsNullOrEmpty(cfg.Atleast))
        {
            var m = System.Text.RegularExpressions.Regex.Match(cfg.Atleast, @"(\d+)x(\d+)");
            if (m.Success)
            {
                tags.Add($"width:>={m.Groups[1].Value}");
                tags.Add($"height:>={m.Groups[2].Value}");
            }
        }

        if (!string.IsNullOrEmpty(cfg.Ratios))
        {
            var first = cfg.Ratios.Split(',')[0].Trim();
            var m = System.Text.RegularExpressions.Regex.Match(first, @"(\d+)x(\d+)");
            if (m.Success)
            {
                var w = int.Parse(m.Groups[1].Value);
                var h = int.Parse(m.Groups[2].Value);
                tags.Add($"ratio:{Math.Round((double)w / h, 2)}");
            }
        }

        var qs = HttpUtility.ParseQueryString("");
        qs["tags"]  = string.Join(" ", tags);
        qs["limit"] = "24";
        qs["page"]  = page.ToString();
        return $"{ApiBase}?{qs}";
    }

    public IReadOnlyList<Candidate> ParseResponse(string jsonBody)
    {
        // Konachan returns a top-level JSON array
        using var doc = JsonDocument.Parse(jsonBody);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<Candidate>();

        var list = new List<Candidate>(doc.RootElement.GetArrayLength());
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            var url = p.TryGetProperty("file_url", out var fu) ? fu.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(url)) continue;

            var ext = p.TryGetProperty("file_ext", out var fe) ? fe.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(ext) && !ext.StartsWith('.')) ext = "." + ext;
            if (string.IsNullOrEmpty(ext))
            {
                try { ext = Path.GetExtension(new Uri(url).AbsolutePath); }
                catch { }
            }
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";

            var id = p.TryGetProperty("id", out var ii) ? ii.GetInt64().ToString() : "";
            if (string.IsNullOrEmpty(id)) continue;

            int width  = p.TryGetProperty("width",  out var wd) ? wd.GetInt32() : 0;
            int height = p.TryGetProperty("height", out var ht) ? ht.GetInt32() : 0;
            var res = width > 0 && height > 0 ? $"{width}x{height}" : "?";

            list.Add(new Candidate(id, url, ext, width, height, res));
        }
        return list;
    }

    public Dictionary<string, object?>? TryParseUrlToPreset(Uri url)
    {
        if (!url.Host.Contains("konachan", StringComparison.OrdinalIgnoreCase)) return null;

        var qs = HttpUtility.ParseQueryString(url.Query);
        var preset = new Dictionary<string, object?>();
        var tags = qs["tags"]?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                              ?? Array.Empty<string>();

        var freeText = new List<string>();
        string? minW = null, minH = null;

        // Both rating:s and rating:safe are valid
        var ratingMap = new Dictionary<string, string>
        {
            ["s"] = "100", ["safe"] = "100",
            ["q"] = "010", ["questionable"] = "010",
            ["e"] = "001", ["explicit"] = "001",
        };

        foreach (var t in tags)
        {
            if (t.StartsWith("rating:"))
            {
                var bit = t[7..].ToLowerInvariant();
                if (ratingMap.TryGetValue(bit, out var purity))
                    preset["purity"] = purity;
            }
            else if (t.StartsWith("order:"))
            {
                var v = t[6..];
                preset["sorting"] = v switch
                {
                    "random"  => "random",
                    "score"   => "toplist",
                    _ when v.StartsWith("id") => "date_added",
                    _ => null,
                };
                if (preset["sorting"] is null) preset.Remove("sorting");
            }
            else if (t.StartsWith("width:>=")) { minW = t[8..]; }
            else if (t.StartsWith("height:>=")) { minH = t[9..]; }
            else if (t.StartsWith("ratio:"))
            {
                if (double.TryParse(t[6..], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var r))
                {
                    preset["ratios"] = r switch
                    {
                        _ when Math.Abs(r - 1.78) < 0.02 => "16x9",
                        _ when Math.Abs(r - 1.60) < 0.02 => "16x10",
                        _ when Math.Abs(r - 1.50) < 0.02 => "3x2",
                        _ when Math.Abs(r - 1.33) < 0.02 => "4x3",
                        _ when Math.Abs(r - 2.33) < 0.02 => "21x9",
                        _ => $"{(int)(r * 100)}x100",
                    };
                }
            }
            else
            {
                freeText.Add(t);
            }
        }

        if (freeText.Count > 0) preset["q"] = string.Join(" ", freeText);
        if (minW is not null && minH is not null) preset["atleast"] = $"{minW}x{minH}";

        // &rollout=N extension
        var rollout = qs["rollout"];
        if (!string.IsNullOrEmpty(rollout) && int.TryParse(rollout, out var n) && n >= 0 && n <= 100)
        {
            preset["rolloutPct"] = n;
        }

        return preset;
    }
}
