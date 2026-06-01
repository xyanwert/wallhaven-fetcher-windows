using System.Net.Http;
using WallhavenFetcher.Notifications;
using WallhavenFetcher.Sync;
using WallhavenFetcher.Updater;
using WallhavenFetcher.Wallpaper;

namespace WallhavenFetcher.App;

/// <summary>
/// System tray application. Owns the NotifyIcon, the context menu, the
/// sync timer, and routes user actions to the sync/save/ban/import engines
/// + updater.
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
    private readonly SaveBanEngine _saveBan;
    private readonly ImportEngine _importer;
    private readonly PresetEngine _presets;
    private readonly FitFolderEngine _fitFolder;

    private ToolStripMenuItem? _updateMenuItem;
    private string? _pendingInstallerUrl;
    private Version? _pendingVersion;

    private const int SyncIntervalHours = 6;
    private readonly string _logPath = Paths.LogFile;

    public TrayApp()
    {
        _http = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("wallhaven-fetcher-windows/1.0");
        _http.Timeout = TimeSpan.FromSeconds(60);

        var apiKey = LoadApiKey();
        _registry        = new SourceRegistry(apiKey);
        _notifier        = new Notifier(ShowBalloon);
        _updater         = new UpdateChecker(_http);
        _updateInstaller = new UpdateInstaller(_http);
        _saveBan         = new SaveBanEngine(AppendLog);
        _importer        = new ImportEngine(AppendLog);
        _presets         = new PresetEngine(_registry);
        _fitFolder       = new FitFolderEngine(AppendLog);

        _menu = BuildMenu();
        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "Wallhaven Fetcher",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseDoubleClick += (_, _) => RunSyncAsync();

        _syncTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromHours(SyncIntervalHours).TotalMilliseconds,
        };
        _syncTimer.Tick += (_, _) => RunSyncAsync();
        _syncTimer.Start();

        // First-run sync after a short delay so the tray icon appears first.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => RunSyncAsync());

        // Background update check.
        _ = _updater.CheckAsync((ver, url) =>
        {
            _pendingVersion       = ver;
            _pendingInstallerUrl  = url;
            BeginInvokeOnMenu(() =>
            {
                if (_updateMenuItem is not null)
                {
                    _updateMenuItem.Text    = $"⬇  Update to v{ver}…";
                    _updateMenuItem.Visible = true;
                }
                _notifier.Show("Wallhaven Fetcher",
                    $"Update available: v{ver} — click to install");
            });
        });
    }

    // ─── Menu construction ───────────────────────────────────────────────

    private ContextMenuStrip BuildMenu()
    {
        // Slim menu: just the four hot actions + Settings + Quit.
        // Imports, presets, library, open-folder/log, knobs — all moved into
        // the unified Settings window.
        var m = new ContextMenuStrip();

        _updateMenuItem = new ToolStripMenuItem("⬇  Update available…", null, (_, _) => InstallUpdate())
        {
            Visible = false,
        };
        m.Items.Add(_updateMenuItem);
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("♥  Save current wallpaper",  null, (_, _) => SaveCurrent());
        m.Items.Add("🗑  Ban current wallpaper",  null, (_, _) => BanCurrent());
        m.Items.Add("🔄  Sync now",               null, (_, _) => RunSyncAsync());
        m.Items.Add("✨  Fix existing wallpapers", null, (_, _) => FitFolderInBackground());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("⚙  Settings…",                null, (_, _) => OpenSettings());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quit",                        null, (_, _) => ExitApp());
        return m;
    }

    // ─── Actions ─────────────────────────────────────────────────────────

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
        var cfg = ConfigFile.Load(Paths.ConfigFile).MaterializeEffective();
        var folder = cfg.ResolveFolder();
        var current = CurrentWallpaperDetector.Detect(folder);
        if (current.Count == 0)
        {
            _notifier.Show("Wallhaven Fetcher",
                "No managed wallpaper currently displayed from this folder.");
            return;
        }
        var state = State.Load(Paths.StateFile);
        var result = _saveBan.Save(current, state, folder);
        if (result.NewlySaved.Count > 0)
            _notifier.Show("Wallhaven — Saved",
                string.Join(", ", result.NewlySaved.Select(c => c.ToString())));
        else if (result.AlreadySaved.Count > 0)
            _notifier.Show("Wallhaven",
                $"Already saved: {string.Join(", ", result.AlreadySaved.Select(c => c.ToString()))}");
    }

    private void BanCurrent()
    {
        var cfg = ConfigFile.Load(Paths.ConfigFile).MaterializeEffective();
        var folder = cfg.ResolveFolder();
        var current = CurrentWallpaperDetector.Detect(folder);
        if (current.Count == 0)
        {
            _notifier.Show("Wallhaven Fetcher", "Nothing managed currently displayed — nothing to ban.");
            return;
        }
        var state = State.Load(Paths.StateFile);
        var result = _saveBan.Ban(current, state, folder);
        WallpaperRefresher.Refresh();
        if (result.NewlyBanned.Count > 0)
            _notifier.Show("Wallhaven — Banned",
                string.Join(", ", result.NewlyBanned.Select(c => c.ToString())));
    }

    private void OpenSettings()
    {
        var cfg = ConfigFile.Load(Paths.ConfigFile);
        using var form = new SettingsForm(cfg, _presets, _importer, _saveBan, _fitFolder, _notifier.Show);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _notifier.Show("Wallhaven Fetcher", "Settings saved. Syncing…");
            RunSyncAsync();
        }
    }

    private async void FitFolderInBackground()
    {
        try
        {
            var cfg = ConfigFile.Load(Paths.ConfigFile).MaterializeEffective();
            _notifier.Show("Wallhaven Fetcher", "Fitting existing wallpapers…");
            var result = await _fitFolder.RunAsync(cfg);
            _notifier.Show("Wallhaven Fetcher — Fit complete", result.Summary());
        }
        catch (Exception ex)
        {
            AppendLog($"Fit folder failed: {ex}");
            _notifier.Show("Wallhaven Fetcher — Fit failed", ex.Message);
        }
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

    // ─── Helpers ─────────────────────────────────────────────────────────

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
        return SystemIcons.Application;
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
