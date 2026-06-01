using System.Net.Http;
using WallhavenFetcher.Notifications;
using WallhavenFetcher.Sync;
using WallhavenFetcher.Updater;
using WallhavenFetcher.Wallpaper;

namespace WallhavenFetcher.App;

/// <summary>
/// System tray application. Owns the NotifyIcon, the context menu, the
/// sync timer, and routes user actions to the sync engine + updater.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _syncTimer;
    private readonly HttpClient _http;
    private readonly SourceRegistry _registry;
    private readonly Notifier _notifier;
    private readonly UpdateChecker _updater;
    private readonly UpdateInstaller _updateInstaller;

    private ToolStripMenuItem? _updateMenuItem;
    private string? _pendingInstallerUrl;
    private Version? _pendingVersion;

    private const int SyncIntervalHours = 6;
    private readonly string _logPath = Paths.LogFile;

    public TrayApp()
    {
        // Init HTTP client with sane User-Agent
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("wallhaven-fetcher-windows/1.0");
        _http.Timeout = TimeSpan.FromSeconds(60);

        var apiKey = LoadApiKey();
        _registry = new SourceRegistry(apiKey);
        _notifier = new Notifier(fallbackBalloon: ShowBalloon);
        _updater = new UpdateChecker(_http);
        _updateInstaller = new UpdateInstaller(_http);

        // Tray icon + menu
        _menu = BuildMenu();
        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Wallhaven Fetcher",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseDoubleClick += (_, _) => RunSyncAsync();

        // Periodic sync timer (default: every 6h)
        _syncTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromHours(SyncIntervalHours).TotalMilliseconds,
        };
        _syncTimer.Tick += (_, _) => RunSyncAsync();
        _syncTimer.Start();

        // First-run sync on launch, but delayed so the tray icon appears first.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => RunSyncAsync());

        // Background update check (silent unless newer found)
        _ = _updater.CheckAsync((ver, url) =>
        {
            _pendingVersion = ver;
            _pendingInstallerUrl = url;
            BeginInvokeOnMenu(() =>
            {
                if (_updateMenuItem is not null)
                {
                    _updateMenuItem.Text = $"⬇  Update to v{ver}…";
                    _updateMenuItem.Visible = true;
                }
                _notifier.Show("Wallhaven Fetcher",
                    $"Update available: v{ver} — click to install");
            });
        });
    }

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        _updateMenuItem = new ToolStripMenuItem("⬇  Update available…", null, (_, _) => InstallUpdate())
        {
            Visible = false,
        };
        m.Items.Add(_updateMenuItem);
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("🔄  Sync now", null, (_, _) => RunSyncAsync());
        m.Items.Add("♥  Save current wallpaper", null, (_, _) => SaveCurrent());
        m.Items.Add("🗑  Ban current wallpaper", null, (_, _) => BanCurrent());
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("📁  Open wallpaper folder", null, (_, _) => OpenFolder());
        m.Items.Add("📄  Open log", null, (_, _) => OpenLog());
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("Settings…", null, (_, _) =>
            _notifier.Show("Wallhaven Fetcher", "Settings UI coming soon — edit config.json for now."));
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("Quit", null, (_, _) => ExitApp());
        return m;
    }

    private async void RunSyncAsync()
    {
        try
        {
            var cfgFile = ConfigFile.Load(Paths.ConfigFile);
            var cfg = cfgFile.MaterializeEffective();
            var state = State.Load(Paths.StateFile);

            var engine = new SyncEngine(_http, _registry, AppendLog, _notifier.Show);
            await engine.RunAsync(cfg, state);

            WallpaperRefresher.Refresh();
        }
        catch (Exception ex)
        {
            AppendLog($"Sync failed: {ex}");
            _notifier.Show("Wallhaven Fetcher — Sync failed", ex.Message);
        }
    }

    private void SaveCurrent()
    {
        // TODO: wire up to current-wallpaper detection
        _notifier.Show("Wallhaven Fetcher", "Save: coming in next version.");
    }

    private void BanCurrent()
    {
        // TODO: same — needs current-wallpaper detection on Windows
        _notifier.Show("Wallhaven Fetcher", "Ban: coming in next version.");
    }

    private void OpenFolder()
    {
        var cfg = ConfigFile.Load(Paths.ConfigFile).MaterializeEffective();
        var folder = cfg.ResolveFolder();
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private void OpenLog()
    {
        if (!File.Exists(_logPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            File.WriteAllText(_logPath, "");
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _logPath,
            UseShellExecute = true,
        });
    }

    private async void InstallUpdate()
    {
        if (_pendingInstallerUrl is null || _pendingVersion is null) return;
        try
        {
            _notifier.Show("Wallhaven Fetcher", $"Downloading v{_pendingVersion}…");
            var dest = await _updateInstaller.DownloadAsync(_pendingInstallerUrl);
            UpdateInstaller.LaunchInstallerAndExit(dest);
        }
        catch (Exception ex)
        {
            _notifier.Show("Wallhaven Fetcher — Update failed", ex.Message);
        }
    }

    private void AppendLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(_logPath, line);
        }
        catch { }
    }

    private void ShowBalloon(string title, string message)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText  = message;
        _tray.ShowBalloonTip(5000);
    }

    private void BeginInvokeOnMenu(Action a)
    {
        if (_menu.InvokeRequired) _menu.BeginInvoke(a);
        else a();
    }

    private static string? LoadApiKey()
    {
        var fromEnv = Environment.GetEnvironmentVariable("WALLHAVEN_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv.Trim();
        if (File.Exists(Paths.ApiKeyFile))
        {
            try { return File.ReadAllText(Paths.ApiKeyFile).Trim(); }
            catch { }
        }
        return null;
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var resourcePath = Path.Combine(AppContext.BaseDirectory, "Resources", "tray-icon.ico");
            if (File.Exists(resourcePath)) return new Icon(resourcePath);
        }
        catch { }
        return SystemIcons.Application;  // fallback
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        _tray.Dispose();
        _syncTimer.Stop();
        _syncTimer.Dispose();
        ExitThread();
    }
}
