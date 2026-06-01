using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WallhavenFetcher.Updater;

/// <summary>
/// Polls GitHub releases for newer versions. Designed for fire-and-forget
/// on startup: spawn a Task, fire OnAvailable callback if newer.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly string _repo;

    public UpdateChecker(HttpClient http, string repo = "xyanwert/wallhaven-fetcher-windows")
    {
        _http = http;
        _repo = repo;
    }

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Background check. Calls onAvailable(latestVersion, installerUrl) if a
    /// newer release exists. Silent on network/parse errors.
    /// </summary>
    public async Task CheckAsync(Action<Version, string> onAvailable,
                                  CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_repo}/releases/latest";
            _http.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"WallhavenFetcher/{CurrentVersion}");
            var json = await _http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            var tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? t.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(tag)) return;

            // Strip a leading "v" if present
            if (tag.StartsWith("v") || tag.StartsWith("V"))
                tag = tag[1..];

            if (!Version.TryParse(tag, out var latest)) return;
            if (latest <= CurrentVersion) return;

            // Find the installer asset (.exe ending in "Setup" or matching name)
            string? dlUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        dlUrl = a.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }
            if (dlUrl is null) return;

            onAvailable(latest, dlUrl);
        }
        catch (Exception)
        {
            // Silent — try again next launch.
        }
    }
}
