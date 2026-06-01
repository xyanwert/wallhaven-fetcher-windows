using System.Diagnostics;
using System.Net.Http;

namespace WallhavenFetcher.Updater;

/// <summary>
/// Download the installer .exe to %TEMP%, then launch it. The current app
/// exits so the installer can replace its files.
/// </summary>
public sealed class UpdateInstaller
{
    private readonly HttpClient _http;
    public UpdateInstaller(HttpClient http) => _http = http;

    public async Task<string> DownloadAsync(string installerUrl, Action<long, long>? progress = null,
                                             CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(new Uri(installerUrl).AbsolutePath);
        var dest = Path.Combine(Path.GetTempPath(), fileName);

        using var resp = await _http.GetAsync(installerUrl,
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);

        var buffer = new byte[81920];
        long sofar = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            sofar += n;
            progress?.Invoke(sofar, total);
        }
        return dest;
    }

    public static void LaunchInstallerAndExit(string installerPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
            // /SILENT for unattended; let the user see it for now so they can
            // pick install dir if needed. Inno Setup defaults are fine.
        };
        Process.Start(psi);
        // Give the installer a moment to start before we exit.
        Thread.Sleep(500);
        Application.Exit();
    }
}
