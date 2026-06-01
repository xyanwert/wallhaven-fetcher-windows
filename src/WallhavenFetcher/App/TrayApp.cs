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

    private ToolStripMenuItem? _updateMenuItem;
    private ToolStripMenuItem? _presetsMenuItem;
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
        var m = new ContextMenuStrip();

        _updateMenuItem = new ToolStripMenuItem("⬇  Update available…", null, (_, _) => InstallUpdate())
        {
            Visible = false,
        };
        m.Items.Add(_updateMenuItem);
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("🔄  Sync now",                null, (_, _) => RunSyncAsync());
        m.Items.Add("♥  Save current wallpaper",   null, (_, _) => SaveCurrent());
        m.Items.Add("🗑  Ban current wallpaper",   null, (_, _) => BanCurrent());
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("⬇  Import file(s)…",          null, (_, _) => ImportFiles());
        m.Items.Add("📁  Import folder…",           null, (_, _) => ImportFolder());
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("⚙  Settings…",                 null, (_, _) => OpenSettings());

        _presetsMenuItem = new ToolStripMenuItem("★  Presets");
        m.Items.Add(_presetsMenuItem);
        RebuildPresetsSubmenu();

        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("📂  Open wallpaper folder",    null, (_, _) => OpenFolder());
        m.Items.Add("📄  Open log",                  null, (_, _) => OpenLog());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quit",                          null, (_, _) => ExitApp());
        return m;
    }

    private void RebuildPresetsSubmenu()
    {
        if (_presetsMenuItem is null) return;
        _presetsMenuItem.DropDownItems.Clear();

        _presetsMenuItem.DropDownItems.Add("🔗  Create from URL…", null, (_, _) => OpenPresetUrl());
        _presetsMenuItem.DropDownItems.Add("💾  Save current as preset…", null, (_, _) => SaveCurrentAsPreset());
        _presetsMenuItem.DropDownItems.Add(new ToolStripSeparator());

        var cfgFile = ConfigFile.Load(Paths.ConfigFile);
        var presets = cfgFile.Presets;
        if (presets.Count == 0)
        {
            _presetsMenuItem.DropDownItems.Add(new ToolStripMenuItem("(no presets yet)") { Enabled = false });
            return;
        }

        var apply = new ToolStripMenuItem("Apply");
        var delete = new ToolStripMenuItem("Delete");
        foreach (var name in presets.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var n = name;
            apply.DropDownItems.Add(n, null, (_, _) => ApplyPreset(n));
            delete.DropDownItems.Add(n, null, (_, _) => DeletePreset(n));
        }
        _presetsMenuItem.DropDownItems.Add(apply);
        _presetsMenuItem.DropDownItems.Add(delete);
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

    private async void ImportFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Pick image file(s) to import",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp;*.tiff;*.tif|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        await DoImportAsync(dlg.FileNames);
    }

    private async void ImportFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Pick folder to import images from (walks recursively)",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        if (string.IsNullOrEmpty(dlg.SelectedPath)) return;
        await DoImportAsync(new[] { dlg.SelectedPath });
    }

    private async Task DoImportAsync(IEnumerable<string> paths)
    {
        try
        {
            var cfgFile = ConfigFile.Load(Paths.ConfigFile);
            var cfg     = cfgFile.MaterializeEffective();
            var state   = State.Load(Paths.StateFile);
            var result  = await _importer.ImportAsync(paths, state, cfg);

            if (result.NoneFound)
                _notifier.Show("Wallhaven Fetcher — Import", "No importable files found.");
            else if (result.Imported.Count > 0)
                _notifier.Show("Wallhaven Fetcher — Imported",
                    $"{result.Imported.Count} file(s) imported");
            else if (result.AlreadyImported.Count > 0)
                _notifier.Show("Wallhaven Fetcher — Import",
                    $"{result.AlreadyImported.Count} already in collection.");
        }
        catch (Exception ex)
        {
            AppendLog($"Import failed: {ex}");
            _notifier.Show("Wallhaven Fetcher — Import failed", ex.Message);
        }
    }

    private void OpenSettings()
    {
        var cfg = ConfigFile.Load(Paths.ConfigFile);
        using var form = new SettingsForm(cfg);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _notifier.Show("Wallhaven Fetcher", "Settings saved. Syncing…");
            RunSyncAsync();
            BeginInvokeOnMenu(RebuildPresetsSubmenu);
        }
    }

    private async void OpenPresetUrl()
    {
        using var form = new PresetUrlForm();
        if (form.ShowDialog() != DialogResult.OK) return;
        if (string.IsNullOrEmpty(form.PresetName) || string.IsNullOrEmpty(form.Url))
        {
            _notifier.Show("Wallhaven Fetcher", "Name and URL are required.");
            return;
        }

        var cfg = ConfigFile.Load(Paths.ConfigFile);
        var source = _presets.CreateFromUrl(form.PresetName, form.Url, cfg);
        if (source is null)
        {
            _notifier.Show("Wallhaven Fetcher — URL not recognized",
                "Couldn't match URL to any registered source (wallhaven / konachan).");
            return;
        }

        _presets.Apply(form.PresetName, cfg);
        _notifier.Show("Wallhaven — Preset applied",
            $"'{form.PresetName}' [{source}] — syncing…");

        BeginInvokeOnMenu(RebuildPresetsSubmenu);
        await Task.Run(RunSyncAsync);
    }

    private void SaveCurrentAsPreset()
    {
        var input = PromptText("Save current settings as preset:", "Preset name:");
        if (string.IsNullOrWhiteSpace(input)) return;

        var cfg = ConfigFile.Load(Paths.ConfigFile);
        _presets.SaveCurrent(input, cfg);
        _notifier.Show("Wallhaven Fetcher", $"Saved preset: {input}");
        BeginInvokeOnMenu(RebuildPresetsSubmenu);
    }

    private void ApplyPreset(string name)
    {
        var cfg = ConfigFile.Load(Paths.ConfigFile);
        if (!_presets.Apply(name, cfg))
        {
            _notifier.Show("Wallhaven Fetcher", $"Unknown preset: {name}");
            return;
        }
        _notifier.Show("Wallhaven — Preset", $"Applied: {name} — syncing…");
        BeginInvokeOnMenu(RebuildPresetsSubmenu);
        RunSyncAsync();
    }

    private void DeletePreset(string name)
    {
        var cfg = ConfigFile.Load(Paths.ConfigFile);
        if (_presets.Delete(name, cfg))
            _notifier.Show("Wallhaven Fetcher", $"Deleted preset: {name}");
        BeginInvokeOnMenu(RebuildPresetsSubmenu);
    }

    private void OpenFolder()
    {
        var folder = ConfigFile.Load(Paths.ConfigFile).MaterializeEffective().ResolveFolder();
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = folder,
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
            FileName        = _logPath,
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

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string? PromptText(string prompt, string title)
    {
        using var f = new Form
        {
            Text = title,
            Width = 420,
            Height = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false,
        };
        var lbl = new Label { Text = prompt, Bounds = new Rectangle(12, 12, 380, 20) };
        var txt = new TextBox { Bounds = new Rectangle(12, 40, 380, 24) };
        var ok = new Button { Text = "OK",     DialogResult = DialogResult.OK,     Bounds = new Rectangle(232, 80, 80, 28) };
        var cn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(316, 80, 76, 28) };
        f.Controls.AddRange(new Control[] { lbl, txt, ok, cn });
        f.AcceptButton = ok; f.CancelButton = cn;
        return f.ShowDialog() == DialogResult.OK ? txt.Text.Trim() : null;
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
