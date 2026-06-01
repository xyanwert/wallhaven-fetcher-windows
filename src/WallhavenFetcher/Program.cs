using WallhavenFetcher.App;

namespace WallhavenFetcher;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Single-instance gate so double-launch doesn't spawn two trays
        using var mutex = new Mutex(true, "WallhavenFetcher_SingleInstance", out var first);
        if (!first) return;

        Application.Run(new TrayApp());
    }
}
