using System.Runtime.InteropServices;

namespace WallhavenFetcher.Imaging;

/// <summary>
/// Resolve target display resolution. Manual override (WxH string) wins,
/// otherwise auto-detect primary display via Win32 GetSystemMetrics.
/// </summary>
public static class DisplayDetector
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    // For "true" pixel dimensions accounting for DPI scaling:
    // SM_CXVIRTUALSCREEN/SM_CYVIRTUALSCREEN for spanning all monitors,
    // EnumDisplaySettings for per-monitor native.

    public static (int Width, int Height) Resolve(string overrideStr)
    {
        if (!string.IsNullOrWhiteSpace(overrideStr))
        {
            var m = System.Text.RegularExpressions.Regex.Match(overrideStr, @"(\d+)\s*x\s*(\d+)");
            if (m.Success)
                return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }

        try
        {
            return (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }
        catch (Exception)
        {
            // Fallback for non-Windows (won't happen in production, but keeps the
            // module testable on any platform).
            return (1920, 1080);
        }
    }
}
