using System.Net.Http;
using WallhavenFetcher.Imaging;
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
        _importer        = new ImportEngine(AppendLog, _http);
        _presets         = new PresetEngine(_registry);
        _fitFolder       = new FitFolderEngine(AppendLog);

        LogStartupDiagnostics(apiKey is not null);

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
        _syncTimer.Tick += (_, _) => RunSyncAsync(force: false);
        _syncTimer.Start();

        // First-run sync after a short delay so the tray icon appears first.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => RunSyncAsync(force: false));

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

    private ToolStripMenuItem? _freezeMenuItem;

    private ContextMenuStrip BuildMenu()
    {
        // Slim menu: just the four hot actions + freeze + Settings + Quit.
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
        m.Items.Add("🔄  Sync now",               null, (_, _) => RunSyncAsync(force: true));
        m.Items.Add("🔗  Import URL…",            null, async (_, _) => await ImportUrlFromTrayAsync());
        m.Items.Add("✨  Fix existing wallpapers", null, (_, _) => FitFolderInBackground());
        m.Items.Add(new ToolStripSeparator());

        _freezeMenuItem = new ToolStripMenuItem("❄  Freeze sync", null, (_, _) => ToggleFreeze());
        m.Items.Add(_freezeMenuItem);
        m.Items.Add("⚙  Settings…",                null, (_, _) => OpenSettings());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quit",                        null, (_, _) => ExitApp());

        UpdateFreezeMenu();
        return m;
    }

    private void UpdateFreezeMenu()
    {
        var cfg = ConfigFile.Load(Paths.ConfigFile).MaterializeEffective();
        if (_freezeMenuItem is not null)
        {
            _freezeMenuItem.Text = cfg.Frozen
                ? "▶  Unfreeze sync"
                : "❄  Freeze sync";
        }
        if (_tray is not null)
        {
            _tray.Text = cfg.Frozen
                ? "Wallhaven Fetcher (frozen)"
                : "Wallhaven Fetcher";
        }
    }

    private void ToggleFreeze()
    {
        var cfgFile = ConfigFile.Load(Paths.ConfigFile);
        var current = cfgFile.MaterializeEffective().Frozen;
        cfgFile.Overrides["frozen"] = System.Text.Json.JsonSerializer.SerializeToElement(!current);
        cfgFile.Save(Paths.ConfigFile);
        _notifier.Show("Wallhaven Fetcher",
            !current ? "Sync frozen — automatic syncs paused" : "Sync resumed");
        UpdateFreezeMenu();
    }

    // ─── Actions ─────────────────────────────────────────────────────────

    private async void RunSyncAsync(bool force = false)
    {
        try
        {
            var cfgFile = ConfigFile.Load(Paths.ConfigFile);
            var cfg = cfgFile.MaterializeEffective();
            var state = State.Load(Paths.StateFile);

            var engine = new SyncEngine(_http, _registry, AppendLog, _notifier.Show);
            var result = await engine.RunAsync(cfg, state, force: force);
            if (result.Mode == "frozen" && !force)
            {
                // Silent — already logged. We surface a small notice when the
                // user clicks Sync now while frozen and DIDN'T pass force.
                // (Currently impossible since Sync now always passes force=true.)
            }
            else
            {
                WallpaperRefresher.Refresh();
            }
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

    /// <summary>
    /// Tray entry point for "Import URL…". Pops the same modal the Settings
    /// Library tab uses, then runs the import on the threadpool and shows a
    /// summary toast — no Settings round-trip required.
    /// </summary>
    private async Task ImportUrlFromTrayAsync()
    {
        using var dlg = new UrlImportForm();
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var urls = dlg.Urls;
        if (urls.Count == 0)
        {
            _notifier.Show("Wallhaven Fetcher", "No valid http(s) URL provided.");
            return;
        }
        try
        {
            var state  = State.Load(Paths.StateFile);
            var cfg    = ConfigFile.Load(Paths.ConfigFile).MaterializeEffective();
            var result = await _importer.ImportAsync(urls, state, cfg);
            if (result.NoneFound)
                _notifier.Show("Wallhaven Fetcher", "No importable URLs found.");
            else
                _notifier.Show("Wallhaven Fetcher — Imported",
                    $"{result.Imported.Count} new, {result.AlreadyImported.Count} already in collection.");
        }
        catch (Exception ex)
        {
            AppendLog($"Tray URL import failed: {ex}");
            _notifier.Show("Wallhaven Fetcher — Import failed", ex.Message);
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

    private void LogStartupDiagnostics(bool hasApiKey)
    {
        try
        {
            var asm = System.Reflection.Assembly.GetEntryAssembly();
            AppendLog("════════════════════════════════════════════════════════════");
            AppendLog($"[BOOT] WallhavenFetcher starting");
            AppendLog($"[BOOT] version: {asm?.GetName().Version}");
            AppendLog($"[BOOT] OS: {Environment.OSVersion}  ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
            AppendLog($"[BOOT] CLR: {Environment.Version}");
            AppendLog($"[BOOT] cwd: {Environment.CurrentDirectory}");
            AppendLog($"[BOOT] paths.config: {Paths.ConfigFile}  exists={File.Exists(Paths.ConfigFile)}");
            AppendLog($"[BOOT] paths.state : {Paths.StateFile}   exists={File.Exists(Paths.StateFile)}");
            AppendLog($"[BOOT] paths.apiKey: {Paths.ApiKeyFile}  exists={File.Exists(Paths.ApiKeyFile)}  loaded={hasApiKey}");
            AppendLog($"[BOOT] paths.log   : {_logPath}");

            var cfgFile = ConfigFile.Load(Paths.ConfigFile);
            var cfg = cfgFile.MaterializeEffective();
            AppendLog($"[BOOT] overrides: {cfgFile.Overrides.Count} key(s) — " +
                      $"{string.Join(", ", cfgFile.Overrides.Keys.OrderBy(k => k))}");
            AppendLog($"[BOOT] presets: {cfgFile.Presets.Count} — " +
                      $"{string.Join(", ", cfgFile.Presets.Keys.OrderBy(k => k))}");
            AppendLog($"[BOOT] effective.source={cfg.Source}  q={cfg.Q.Substring(0, Math.Min(60, cfg.Q.Length))}" +
                      $"{(cfg.Q.Length > 60 ? "…" : "")}");
            AppendLog($"[BOOT] effective.maxWallpapers={cfg.MaxWallpapers}  newPerRun={cfg.NewPerRun}  " +
                      $"rolloutPct={cfg.RolloutPct}");
            AppendLog($"[BOOT] effective.fitToRatio={cfg.FitToRatio}  target={cfg.TargetResolution}  " +
                      $"frozen={cfg.Frozen}");

            var state = State.Load(Paths.StateFile);
            AppendLog($"[BOOT] state.schema={state.Schema}  saved={state.Saved.Count}  banned={state.Banned.Count}");
            foreach (var src in new[] { "wallhaven", "konachan", "local" })
            {
                var ss = state.Of(src);
                AppendLog($"[BOOT] state.{src}: images={ss.Images.Count}  cursor entries={ss.Cursor.Count}");
            }

            var folder = cfg.ResolveFolder();
            AppendLog($"[BOOT] folder: {folder}  exists={Directory.Exists(folder)}");
            if (Directory.Exists(folder))
            {
                int totalFiles = Directory.EnumerateFiles(folder).Count();
                int managed = Directory.EnumerateFiles(folder)
                    .Count(f => FileNameHelpers.IsManagedWallpaper(f));
                AppendLog($"[BOOT] folder contents: {totalFiles} total file(s)  {managed} managed wallpaper(s)");
            }

            var (tw, th) = DisplayDetector.Resolve(cfg.TargetResolution);
            AppendLog($"[BOOT] display target resolved: {tw}x{th}");

            AppendLog($"[BOOT] sources registered: {string.Join(", ", _registry.Names)}");
            AppendLog("════════════════════════════════════════════════════════════");
        }
        catch (Exception ex)
        {
            AppendLog($"[BOOT] diagnostics failed: {ex.GetType().Name}: {ex.Message}");
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
