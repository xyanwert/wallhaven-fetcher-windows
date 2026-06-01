using System.Runtime.InteropServices;

namespace WallhavenFetcher.Wallpaper;

/// <summary>
/// Force Windows to re-scan the wallpaper folder. Equivalent to macOS's
/// `killall WallpaperAgent`. With Slideshow mode enabled, Windows polls the
/// folder on its own rotation interval, but calling SystemParametersInfo
/// with a null path triggers an immediate refresh.
/// </summary>
public static class WallpaperRefresher
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(uint uAction, uint uParam,
                                                    string? lpvParam, uint fuWinIni);

    private const uint SPI_SETDESKWALLPAPER = 0x0014;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE    = 0x02;

    public static void Refresh()
    {
        try
        {
            // Passing null re-applies the current wallpaper setting,
            // which for slideshow mode re-scans the source folder.
            SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, null,
                                 SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
        }
        catch (Exception)
        {
            // Non-Windows or DllNotFound — silently no-op.
        }
    }
}
