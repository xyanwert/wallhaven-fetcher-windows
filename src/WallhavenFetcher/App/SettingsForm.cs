using System.Text.Json;
using WallhavenFetcher.Sync;

namespace WallhavenFetcher.App;

/// <summary>
/// Tabbed settings window. Owns everything the tray menu used to have
/// scattered across submenus: search params, folder cap, image fit, presets,
/// and the library (saved/banned, imports, open-folder, open-log).
///
/// The bottom Save & sync button writes all changed values to overrides
/// in one shot and fires a background sync. Tab-specific buttons (Apply
/// preset, Import file…, Unsave, etc.) act immediately and don't depend
/// on the Save button.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly ConfigFile _cfgFile;
    private readonly PresetEngine _presets;
    private readonly ImportEngine _importer;
    private readonly SaveBanEngine _saveBan;
    private readonly FitFolderEngine _fitFolder;
    private readonly Action<string, string> _notify;

    // Form-field controls (referenced when persisting overrides)
    private readonly ComboBox _source     = new();
    private readonly TextBox  _query      = new();
    private readonly ComboBox _sorting    = new();
    private readonly ComboBox _order      = new();
    private readonly ComboBox _topRange   = new();
    private readonly ComboBox _categories = new();
    private readonly ComboBox _purity     = new();
    private readonly TextBox  _atleast    = new();
    private readonly TextBox  _ratios     = new();
    private readonly TextBox  _folder     = new();
    private readonly NumericUpDown _maxWallpapers    = new();
    private readonly NumericUpDown _newPerRun        = new();
    private readonly NumericUpDown _rolloutPct       = new();
    private readonly CheckBox      _fitToRatio       = new();
    private readonly TextBox       _targetResolution = new();
    private readonly NumericUpDown _fitTolerancePct  = new();
    private readonly NumericUpDown _cropThresholdPct = new();
    private readonly NumericUpDown _maxImageDimFactor = new();

    // Presets tab
    private readonly ListBox _presetList = new();

    // Library tab
    private readonly ListView _savedList  = new();
    private readonly ListView _bannedList = new();
    private readonly Label    _savedCount  = new();
    private readonly Label    _bannedCount = new();

    public SettingsForm(
        ConfigFile cfgFile,
        PresetEngine presets,
        ImportEngine importer,
        SaveBanEngine saveBan,
        FitFolderEngine fitFolder,
        Action<string, string> notify)
    {
        _cfgFile   = cfgFile;
        _presets   = presets;
        _importer  = importer;
        _saveBan   = saveBan;
        _fitFolder = fitFolder;
        _notify    = notify;

        Text = "Wallhaven Fetcher — Settings";
        Width = 720;
        Height = 600;
        MinimumSize = new Size(660, 520);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildSearchTab());
        tabs.TabPages.Add(BuildFolderTab());
        tabs.TabPages.Add(BuildFitTab());
        tabs.TabPages.Add(BuildPresetsTab());
        tabs.TabPages.Add(BuildLibraryTab());
        Controls.Add(tabs);

        // Bottom button bar
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        var save   = new Button { Text = "Save & sync",  Width = 110, Top = 12 };
        var cancel = new Button { Text = "Cancel",        Width = 80,  Top = 12 };
        save.Click   += (_, _) => SaveAndSync();
        cancel.Click += (_, _) => Close();
        bar.Resize += (_, _) =>
        {
            cancel.Left = bar.Width - cancel.Width - 14;
            save.Left   = cancel.Left - save.Width - 8;
        };
        bar.Controls.Add(save);
        bar.Controls.Add(cancel);
        Controls.Add(bar);
        AcceptButton = save;
        CancelButton = cancel;

        LoadInitialValues();
    }

    // ─── Tab construction ───────────────────────────────────────────────

    private TabPage BuildSearchTab()
    {
        var tab = new TabPage("Search");
        var t = NewGrid(8, 2);

        AddRow(t, 0, "Source:", _source, ComboItems(("Wallhaven", "wallhaven"), ("Konachan", "konachan")));
        AddRow(t, 1, "Query (q):", _query, hint: "wallhaven mini-DSL or konachan tag string");
        AddRow(t, 2, "Sort:", _sorting, ComboItems(
            ("Random", "random"), ("Top (toplist)", "toplist"), ("Views", "views"),
            ("Favorites", "favorites"), ("Date added", "date_added"),
            ("Relevance (wallhaven only)", "relevance")));
        AddRow(t, 3, "Order:", _order, ComboItems(
            ("Default", ""), ("Descending", "desc"), ("Ascending", "asc")));
        AddRow(t, 4, "Top range:", _topRange, ComboItems(
            ("1 day", "1d"), ("3 days", "3d"), ("1 week", "1w"),
            ("1 month", "1M"), ("3 months", "3M"), ("6 months", "6M"),
            ("1 year", "1y"), ("All time", "all")));
        AddRow(t, 5, "Categories:", _categories, ComboItems(
            ("All (general + anime + people)", "111"),
            ("General only", "100"), ("Anime only", "010"), ("People only", "001"),
            ("General + anime", "110"), ("General + people", "101"), ("Anime + people", "011")),
            hint: "wallhaven only");
        AddRow(t, 6, "Purity:", _purity, ComboItems(
            ("SFW only", "100"), ("SFW + suggestive", "110"),
            ("All (incl. restricted)", "111"),
            ("Suggestive + restricted", "011"), ("Restricted only", "001")));
        AddRow(t, 7, "Min resolution:", _atleast, hint: "WxH; blank = no filter");
        AddRow(t, 8, "Aspect ratios:", _ratios, hint: "comma-separated; blank = no filter");

        tab.Controls.Add(t);
        return tab;
    }

    private TabPage BuildFolderTab()
    {
        var tab = new TabPage("Folder & cap");
        var t = NewGrid(4, 2);

        // Folder row with Browse button
        AddLabel(t, 0, "Wallpaper folder:");
        var folderWrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2,
            RowCount = 1, AutoSize = true,
        };
        folderWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderWrap.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folder.Dock = DockStyle.Fill;
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseFolder();
        folderWrap.Controls.Add(_folder, 0, 0);
        folderWrap.Controls.Add(browse, 1, 0);
        t.Controls.Add(folderWrap, 1, 0);

        AddRow(t, 1, "Max wallpapers:", _maxWallpapers, 1, 10000,
               hint: "rotating; saved files don't count");
        AddRow(t, 2, "New per run:",    _newPerRun,    0, 100,
               hint: "steady-state download count");
        AddRow(t, 3, "Rollout %:",      _rolloutPct,   0, 100,
               hint: "% of max swapped per run after search change");

        tab.Controls.Add(t);
        return tab;
    }

    private TabPage BuildFitTab()
    {
        var tab = new TabPage("Image fit");
        var t = NewGrid(7, 2);

        _fitToRatio.Text = "Auto-fit downloads to display ratio";
        _fitToRatio.AutoSize = true;
        t.Controls.Add(_fitToRatio, 0, 0);
        t.SetColumnSpan(_fitToRatio, 2);

        var info = new Label
        {
            Text = "Off-ratio images get center-cropped if loss is small, " +
                   "or padded with a blurred background otherwise.",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        };
        t.Controls.Add(info, 0, 1);
        t.SetColumnSpan(info, 2);

        AddRow(t, 2, "Target resolution:", _targetResolution,
               hint: "WxH; blank = auto-detect display");
        AddRow(t, 3, "Fit tolerance %:",   _fitTolerancePct,  0, 100,
               hint: "skip fit if ratio drift ≤ this");
        AddRow(t, 4, "Crop threshold %:",  _cropThresholdPct, 0, 100,
               hint: "prefer crop over pad if loss ≤ this");

        // Cap every image to monitor_dim × this factor. NumericUpDown
        // supports decimals via DecimalPlaces; 2.0 = up to 2× monitor on
        // each axis (Retina margin), 1.0 = exact monitor size, 0 = disabled.
        _maxImageDimFactor.DecimalPlaces = 1;
        _maxImageDimFactor.Increment     = 0.5m;
        AddRow(t, 5, "Max size × monitor:", _maxImageDimFactor, 0, 8,
               hint: "0 disables; 1.0 = exact monitor; 2.0 = Retina margin");

        var fitNow = new Button { Text = "Fit existing wallpapers now", AutoSize = true };
        fitNow.Click += async (_, _) => await FitExistingAsync();
        t.Controls.Add(fitNow, 0, 6);
        t.SetColumnSpan(fitNow, 2);

        tab.Controls.Add(t);
        return tab;
    }

    private TabPage BuildPresetsTab()
    {
        var tab = new TabPage("Presets");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Padding = new Padding(16),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _presetList.Dock = DockStyle.Fill;
        _presetList.DoubleClick += (_, _) => ApplySelectedPreset();
        root.Controls.Add(_presetList, 0, 0);

        var btns = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        var apply  = new Button { Text = "Apply",     AutoSize = true };
        var del    = new Button { Text = "Delete",    AutoSize = true };
        var fromU  = new Button { Text = "From URL…", AutoSize = true };
        var saveP  = new Button { Text = "Save current as preset…", AutoSize = true };
        apply.Click += (_, _) => ApplySelectedPreset();
        del.Click   += (_, _) => DeleteSelectedPreset();
        fromU.Click += (_, _) => CreatePresetFromUrl();
        saveP.Click += (_, _) => SaveCurrentAsPreset();
        btns.Controls.AddRange(new Control[] { apply, del, fromU, saveP });
        root.Controls.Add(btns, 0, 1);

        tab.Controls.Add(root);
        return tab;
    }

    private TabPage BuildLibraryTab()
    {
        var tab = new TabPage("Library");
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
            Padding = new Padding(16),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Top: action buttons
        var actions = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        var impFile   = new Button { Text = "Import file(s)…",      AutoSize = true };
        var impFolder = new Button { Text = "Import folder…",        AutoSize = true };
        var impUrl    = new Button { Text = "Import URL(s)…",        AutoSize = true };
        var openFold  = new Button { Text = "Open wallpaper folder", AutoSize = true };
        var openFav   = new Button { Text = "Open favorites/",       AutoSize = true };
        var openLog   = new Button { Text = "Open log",              AutoSize = true };
        impFile.Click   += async (_, _) => await ImportFilesAsync();
        impFolder.Click += async (_, _) => await ImportFolderAsync();
        impUrl.Click    += async (_, _) => await ImportUrlAsync();
        openFold.Click  += (_, _) => OpenFolder("");
        openFav.Click   += (_, _) => OpenFolder("favorites");
        openLog.Click   += (_, _) => OpenLog();
        actions.Controls.AddRange(new Control[] { impFile, impFolder, impUrl, openFold, openFav, openLog });
        root.Controls.Add(actions, 0, 0);
        root.SetColumnSpan(actions, 2);

        // Section headers
        var savedHdr  = new Label { Text = "Saved",  AutoSize = true,
                                     Font = new Font(Font, FontStyle.Bold) };
        var bannedHdr = new Label { Text = "Banned", AutoSize = true,
                                     Font = new Font(Font, FontStyle.Bold) };
        var savedHdrWrap  = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                                                  FlowDirection = FlowDirection.LeftToRight };
        var bannedHdrWrap = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                                                  FlowDirection = FlowDirection.LeftToRight };
        savedHdrWrap.Controls.AddRange(new Control[] { savedHdr, _savedCount });
        bannedHdrWrap.Controls.AddRange(new Control[] { bannedHdr, _bannedCount });
        _savedCount.AutoSize  = true; _savedCount.ForeColor  = SystemColors.GrayText;
        _bannedCount.AutoSize = true; _bannedCount.ForeColor = SystemColors.GrayText;
        _savedCount.Margin    = new Padding(8, 3, 0, 0);
        _bannedCount.Margin   = new Padding(8, 3, 0, 0);
        root.Controls.Add(savedHdrWrap,  0, 1);
        root.Controls.Add(bannedHdrWrap, 1, 1);

        // Lists
        InitListView(_savedList);
        InitListView(_bannedList);
        root.Controls.Add(_savedList,  0, 2);
        root.Controls.Add(_bannedList, 1, 2);

        // Bottom row: per-list action buttons
        var savedBtns  = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                                                FlowDirection = FlowDirection.LeftToRight };
        var bannedBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                                                FlowDirection = FlowDirection.LeftToRight };
        var unsave  = new Button { Text = "Unsave selected", AutoSize = true };
        var unban   = new Button { Text = "Unban selected",  AutoSize = true };
        var refresh1 = new Button { Text = "Refresh", AutoSize = true };
        var refresh2 = new Button { Text = "Refresh", AutoSize = true };
        unsave.Click  += (_, _) => UnsaveSelected();
        unban.Click   += (_, _) => UnbanSelected();
        refresh1.Click += (_, _) => RefreshLibrary();
        refresh2.Click += (_, _) => RefreshLibrary();
        savedBtns.Controls.AddRange(new Control[]  { unsave, refresh1 });
        bannedBtns.Controls.AddRange(new Control[] { unban,  refresh2 });
        root.Controls.Add(savedBtns,  0, 3);
        root.Controls.Add(bannedBtns, 1, 3);

        tab.Controls.Add(root);
        return tab;
    }

    // ─── Layout helpers ─────────────────────────────────────────────────

    private TableLayoutPanel NewGrid(int rows, int cols)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = cols,
            Padding = new Padding(16),
        };
        for (int i = 0; i < cols; i++)
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles[t.ColumnStyles.Count - 1] =
            new ColumnStyle(SizeType.Percent, 100);
        for (int i = 0; i <= rows; i++)
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return t;
    }

    private static void InitListView(ListView lv)
    {
        lv.View = View.Details;
        lv.HeaderStyle = ColumnHeaderStyle.None;
        lv.FullRowSelect = true;
        lv.MultiSelect = true;
        lv.HideSelection = false;
        lv.Dock = DockStyle.Fill;
        lv.Margin = new Padding(0, 6, 6, 6);
        lv.Columns.Add(new ColumnHeader { Width = 200 });
        // auto-resize column on parent resize
        lv.Resize += (_, _) =>
        {
            if (lv.Columns.Count > 0 && lv.ClientSize.Width > 0)
                lv.Columns[0].Width = lv.ClientSize.Width - 4;
        };
    }

    private void AddLabel(TableLayoutPanel t, int row, string text)
    {
        var lbl = new Label
        {
            Text = text, AutoSize = true,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            Margin = new Padding(0, 6, 12, 4),
        };
        t.Controls.Add(lbl, 0, row);
    }

    private void AddRow(TableLayoutPanel t, int row, string label, TextBox tb, string? hint = null)
    {
        AddLabel(t, row, label);
        tb.Dock = DockStyle.Fill;
        tb.Margin = new Padding(0, 4, 0, 4);
        if (hint is not null)
        {
            var wrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true,
            };
            wrap.Controls.Add(tb, 0, 0);
            wrap.Controls.Add(new Label
            {
                Text = hint, ForeColor = SystemColors.GrayText,
                AutoSize = true, Margin = new Padding(0, 0, 0, 0),
            }, 0, 1);
            t.Controls.Add(wrap, 1, row);
        }
        else
        {
            t.Controls.Add(tb, 1, row);
        }
    }

    private void AddRow(TableLayoutPanel t, int row, string label, NumericUpDown nud,
                        int lo, int hi, string? hint = null)
    {
        AddLabel(t, row, label);
        nud.Minimum = lo; nud.Maximum = hi;
        nud.Width = 90;
        nud.Margin = new Padding(0, 4, 0, 4);
        if (hint is not null)
        {
            var wrap = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true, Dock = DockStyle.Fill,
            };
            wrap.Controls.Add(nud);
            wrap.Controls.Add(new Label
            {
                Text = "  " + hint, ForeColor = SystemColors.GrayText,
                AutoSize = true, Margin = new Padding(8, 7, 0, 0),
            });
            t.Controls.Add(wrap, 1, row);
        }
        else
        {
            t.Controls.Add(nud, 1, row);
        }
    }

    private void AddRow(TableLayoutPanel t, int row, string label, ComboBox cb,
                        List<KeyValuePair<string, string>> items, string? hint = null)
    {
        AddLabel(t, row, label);
        cb.DropDownStyle = ComboBoxStyle.DropDownList;
        cb.Dock = DockStyle.Fill;
        cb.Margin = new Padding(0, 4, 0, 4);
        cb.Items.Clear();
        foreach (var (label2, value) in items)
            cb.Items.Add(new ComboItem(label2, value));
        cb.DisplayMember = nameof(ComboItem.Label);
        cb.ValueMember   = nameof(ComboItem.Value);

        if (hint is not null)
        {
            var wrap = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, AutoSize = true,
            };
            wrap.Controls.Add(cb, 0, 0);
            wrap.Controls.Add(new Label
            {
                Text = hint, ForeColor = SystemColors.GrayText, AutoSize = true,
            }, 0, 1);
            t.Controls.Add(wrap, 1, row);
        }
        else
        {
            t.Controls.Add(cb, 1, row);
        }
    }

    private static List<KeyValuePair<string, string>> ComboItems(
        params (string Label, string Value)[] items) =>
        items.Select(p => new KeyValuePair<string, string>(p.Label, p.Value)).ToList();

    private sealed record ComboItem(string Label, string Value)
    {
        public override string ToString() => Label;
    }

    // ─── Data loading / persistence ─────────────────────────────────────

    private void LoadInitialValues()
    {
        var cfg = _cfgFile.MaterializeEffective();

        SelectByValue(_source,     cfg.Source);
        _query.Text = cfg.Q;
        SelectByValue(_sorting,    cfg.Sorting);
        SelectByValue(_order,      cfg.Order);
        SelectByValue(_topRange,   cfg.TopRange);
        SelectByValue(_categories, cfg.Categories);
        SelectByValue(_purity,     cfg.Purity);
        _atleast.Text = cfg.Atleast;
        _ratios.Text  = cfg.Ratios;

        _folder.Text          = cfg.Folder;
        _maxWallpapers.Value  = Math.Clamp(cfg.MaxWallpapers, (int)_maxWallpapers.Minimum, (int)_maxWallpapers.Maximum);
        _newPerRun.Value      = Math.Clamp(cfg.NewPerRun, 0, 100);
        _rolloutPct.Value     = Math.Clamp(cfg.RolloutPct, 0, 100);

        _fitToRatio.Checked      = cfg.FitToRatio;
        _targetResolution.Text   = cfg.TargetResolution;
        _fitTolerancePct.Value   = Math.Clamp(cfg.FitTolerancePct, 0, 100);
        _cropThresholdPct.Value  = Math.Clamp(cfg.CropThresholdPct, 0, 100);
        _maxImageDimFactor.Value = Math.Clamp((decimal)cfg.MaxImageDimFactor,
                                              _maxImageDimFactor.Minimum,
                                              _maxImageDimFactor.Maximum);

        RefreshPresetsList();
        RefreshLibrary();
    }

    private static void SelectByValue(ComboBox cb, string value)
    {
        for (int i = 0; i < cb.Items.Count; i++)
            if (cb.Items[i] is ComboItem c && c.Value == value)
            { cb.SelectedIndex = i; return; }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }

    private static string ValueOf(ComboBox cb) =>
        (cb.SelectedItem as ComboItem)?.Value ?? "";

    private void SaveAndSync()
    {
        var o = _cfgFile.Overrides;

        // Wipe PRESET_KEYS-equivalent so this acts as authoritative
        foreach (var k in new[]
        {
            "source", "q", "categories", "purity", "sorting", "order", "topRange",
            "atleast", "ratios", "folder",
            "maxWallpapers", "newPerRun", "rolloutPct",
            "fitToRatio", "targetResolution", "fitTolerancePct", "cropThresholdPct",
        }) o.Remove(k);

        Set("source",     ValueOf(_source));
        Set("q",          _query.Text);
        Set("sorting",    ValueOf(_sorting));
        Set("order",      ValueOf(_order));
        Set("topRange",   ValueOf(_topRange));
        Set("categories", ValueOf(_categories));
        Set("purity",     ValueOf(_purity));
        Set("atleast",    _atleast.Text);
        Set("ratios",     _ratios.Text);
        Set("folder",     _folder.Text);

        SetInt("maxWallpapers", (int)_maxWallpapers.Value);
        SetInt("newPerRun",     (int)_newPerRun.Value);
        SetInt("rolloutPct",    (int)_rolloutPct.Value);

        o["fitToRatio"] = JsonSerializer.SerializeToElement(_fitToRatio.Checked);
        Set("targetResolution", _targetResolution.Text);
        SetInt("fitTolerancePct",  (int)_fitTolerancePct.Value);
        SetInt("cropThresholdPct", (int)_cropThresholdPct.Value);
        _cfgFile.Overrides["maxImageDimFactor"] =
            JsonSerializer.SerializeToElement((double)_maxImageDimFactor.Value);

        _cfgFile.Save(Paths.ConfigFile);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _cfgFile.Overrides[key] = JsonSerializer.SerializeToElement(value);
    }

    private void SetInt(string key, int value)
        => _cfgFile.Overrides[key] = JsonSerializer.SerializeToElement(value);

    // ─── Folder browse ──────────────────────────────────────────────────
    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Pick wallpaper folder",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrEmpty(_folder.Text)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                : _folder.Text,
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            _folder.Text = dlg.SelectedPath;
    }

    // ─── Presets ────────────────────────────────────────────────────────
    private void RefreshPresetsList()
    {
        _presetList.Items.Clear();
        var p = _cfgFile.Presets;
        if (p.Count == 0)
        {
            _presetList.Items.Add("(no presets yet)");
            _presetList.Enabled = false;
            return;
        }
        _presetList.Enabled = true;
        foreach (var name in p.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            var vals = p[name];
            var src = vals.TryGetValue("source", out var s) ? s.GetString() ?? "wallhaven" : "wallhaven";
            var q = vals.TryGetValue("q", out var qq) ? qq.GetString() ?? "" : "";
            _presetList.Items.Add($"{name}    [{src}]    q={q}");
        }
    }

    private string? SelectedPresetName()
    {
        if (_presetList.SelectedItem is not string row) return null;
        if (row.StartsWith("(")) return null;
        return row.Split("    ", 2)[0];
    }

    private void ApplySelectedPreset()
    {
        var name = SelectedPresetName();
        if (name is null) return;
        if (_presets.Apply(name, _cfgFile))
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void DeleteSelectedPreset()
    {
        var name = SelectedPresetName();
        if (name is null) return;
        if (MessageBox.Show($"Delete preset '{name}'?", "Confirm",
                             MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            != DialogResult.Yes) return;
        _presets.Delete(name, _cfgFile);
        RefreshPresetsList();
    }

    private void CreatePresetFromUrl()
    {
        using var dlg = new PresetUrlForm();
        if (dlg.ShowDialog() != DialogResult.OK) return;
        var src = _presets.CreateFromUrl(dlg.PresetName, dlg.Url, _cfgFile);
        if (src is null)
        {
            MessageBox.Show("URL not recognized by any registered source.",
                            "From URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _presets.Apply(dlg.PresetName, _cfgFile);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void SaveCurrentAsPreset()
    {
        var name = Prompt("Save current settings as preset:", "Preset name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        _presets.SaveCurrent(name, _cfgFile);
        RefreshPresetsList();
    }

    // ─── Library tab actions ────────────────────────────────────────────
    private void RefreshLibrary()
    {
        var state = State.Load(Paths.StateFile);
        _savedList.BeginUpdate();
        _savedList.Items.Clear();
        foreach (var cid in state.Saved.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            _savedList.Items.Add(cid);
        _savedList.EndUpdate();
        _bannedList.BeginUpdate();
        _bannedList.Items.Clear();
        foreach (var cid in state.Banned.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            _bannedList.Items.Add(cid);
        _bannedList.EndUpdate();
        _savedCount.Text  = state.Saved.Count  > 0 ? $"({state.Saved.Count})"  : "";
        _bannedCount.Text = state.Banned.Count > 0 ? $"({state.Banned.Count})" : "";
    }

    private void UnsaveSelected()
    {
        var state = State.Load(Paths.StateFile);
        var cids  = _savedList.SelectedItems.Cast<ListViewItem>()
                     .Select(i => i.Text)
                     .Where(s => CanonicalId.TryParse(s, out _))
                     .Select(CanonicalId.Parse).ToList();
        if (cids.Count == 0) return;
        _saveBan.Unsave(cids, state);
        RefreshLibrary();
    }

    private void UnbanSelected()
    {
        var state = State.Load(Paths.StateFile);
        var cids  = _bannedList.SelectedItems.Cast<ListViewItem>()
                     .Select(i => i.Text)
                     .Where(s => CanonicalId.TryParse(s, out _))
                     .Select(CanonicalId.Parse).ToList();
        if (cids.Count == 0) return;
        _saveBan.Unban(cids, state);
        RefreshLibrary();
    }

    private async Task ImportFilesAsync()
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

    private async Task ImportFolderAsync()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Pick folder to import images from (recursive)",
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        if (string.IsNullOrEmpty(dlg.SelectedPath)) return;
        await DoImportAsync(new[] { dlg.SelectedPath });
    }

    /// <summary>
    /// Prompt for one-or-more direct image URLs (one per line). Wallhaven /
    /// konachan CDN URLs are recognized so they save under their canonical
    /// source ID instead of becoming generic local-hash entries.
    /// </summary>
    private async Task ImportUrlAsync()
    {
        using var dlg = new UrlImportForm();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var urls = dlg.Urls;
        if (urls.Count == 0)
        {
            _notify("Wallhaven Fetcher", "No valid http(s) URL provided.");
            return;
        }
        await DoImportAsync(urls);
    }

    private async Task DoImportAsync(IEnumerable<string> paths)
    {
        var state = State.Load(Paths.StateFile);
        var cfg = _cfgFile.MaterializeEffective();
        var result = await _importer.ImportAsync(paths, state, cfg);
        if (result.NoneFound)
            _notify("Wallhaven Fetcher", "No importable files found.");
        else
            _notify("Wallhaven Fetcher — Imported",
                $"{result.Imported.Count} new, {result.AlreadyImported.Count} already in collection.");
        RefreshLibrary();
    }

    private void OpenFolder(string subdir)
    {
        var folder = _cfgFile.MaterializeEffective().ResolveFolder();
        if (!string.IsNullOrEmpty(subdir))
            folder = Path.Combine(folder, subdir);
        Directory.CreateDirectory(folder);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = folder,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void OpenLog()
    {
        var path = Paths.LogFile;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    // ─── Image fit tab action ───────────────────────────────────────────
    private async Task FitExistingAsync()
    {
        var cfg = _cfgFile.MaterializeEffective();
        // Reflect any unsaved fit-tab changes so the fit run uses them.
        cfg.FitToRatio        = _fitToRatio.Checked;
        cfg.TargetResolution  = _targetResolution.Text;
        cfg.FitTolerancePct   = (int)_fitTolerancePct.Value;
        cfg.CropThresholdPct  = (int)_cropThresholdPct.Value;

        _notify("Wallhaven Fetcher", "Fitting existing wallpapers…");
        var result = await _fitFolder.RunAsync(cfg);
        _notify("Wallhaven Fetcher — Fit complete", result.Summary());
    }

    // ─── Generic text prompt ────────────────────────────────────────────
    private static string? Prompt(string title, string label)
    {
        using var f = new Form
        {
            Text = title, Width = 420, Height = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false,
        };
        f.Controls.Add(new Label { Text = label, Bounds = new Rectangle(12, 12, 380, 20) });
        var tb = new TextBox { Bounds = new Rectangle(12, 40, 380, 24) };
        f.Controls.Add(tb);
        var ok = new Button { Text = "OK",     DialogResult = DialogResult.OK,
                              Bounds = new Rectangle(232, 80, 80, 28) };
        var cn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel,
                              Bounds = new Rectangle(316, 80, 76, 28) };
        f.Controls.Add(ok); f.Controls.Add(cn);
        f.AcceptButton = ok; f.CancelButton = cn;
        return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
    }
}
