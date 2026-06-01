using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WallhavenFetcher.Sync;

public static class Fingerprint
{
    /// <summary>
    /// Compute a 12-hex-char fingerprint of the search-affecting config keys
    /// for the given source. Changing any of these triggers rollout.
    /// </summary>
    public static string Compute(Config cfg, string sourceName, IReadOnlyCollection<string> keys)
    {
        // Build a deterministic dictionary of (key → value) for the requested keys.
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["_source"] = sourceName,
        };

        foreach (var k in keys)
        {
            parts[k] = k switch
            {
                "q"          => cfg.Q,
                "categories" => cfg.Categories,
                "purity"     => cfg.Purity,
                "sorting"    => cfg.Sorting,
                "order"      => cfg.Order,
                "atleast"    => cfg.Atleast,
                "ratios"     => cfg.Ratios,
                "topRange"   => cfg.TopRange,
                _            => "",
            };
        }

        // topRange only contributes when sorting=toplist (it's API-ignored
        // otherwise; we don't want a spurious rollout on its change).
        if (parts.ContainsKey("topRange") && cfg.Sorting != "toplist")
            parts.Remove("topRange");

        var json = JsonSerializer.Serialize(parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
