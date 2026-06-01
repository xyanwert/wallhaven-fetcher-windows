using WallhavenFetcher.Sync;

namespace WallhavenFetcher.App;

/// <summary>
/// Quick settings dialog — edit the most-touched overrides. Heavier
/// keys (fit thresholds, folder path) stay in config.json.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly ConfigFile _cfgFile;

    private readonly ComboBox _source     = new();
    private readonly TextBox  _query      = new();
    private readonly ComboBox _sorting    = new();
    private readonly ComboBox _categories = new();
    private readonly ComboBox _purity     = new();
    private readonly TextBox  _atleast    = new();
    private readonly TextBox  _ratios     = new();
    private readonly NumericUpDown _maxWallpapers = new();
    private readonly NumericUpDown _rolloutPct    = new();

    public SettingsForm(ConfigFile cfgFile)
    {
        _cfgFile = cfgFile;
        var cfg = cfgFile.MaterializeEffective();

        Text = "Wallhaven Fetcher — Settings";
        Width = 460;
        Height = 460;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var y = 12;
        AddLabel("Source:",      y); _source.Bounds      = FieldRect(y); Controls.Add(_source);
        _source.DropDownStyle = ComboBoxStyle.DropDownList;
        _source.Items.AddRange(new object[] { "wallhaven", "konachan" });
        _source.SelectedItem = cfg.Source;
        y += 32;

        AddLabel("Query (q):",   y); _query.Bounds = FieldRect(y); Controls.Add(_query);
        _query.Text = cfg.Q;
        y += 32;

        AddLabel("Sort:",        y); _sorting.Bounds = FieldRect(y); Controls.Add(_sorting);
        _sorting.DropDownStyle = ComboBoxStyle.DropDownList;
        _sorting.Items.AddRange(new object[] { "random", "toplist", "views", "favorites", "date_added", "relevance" });
        _sorting.SelectedItem = cfg.Sorting;
        y += 32;

        AddLabel("Categories:",  y); _categories.Bounds = FieldRect(y); Controls.Add(_categories);
        _categories.DropDownStyle = ComboBoxStyle.DropDownList;
        _categories.Items.AddRange(new object[]
        {
            "111 (all)", "100 (general)", "010 (anime)", "001 (people)",
            "110 (general+anime)", "101 (general+people)", "011 (anime+people)"
        });
        _categories.SelectedItem = _categories.Items.Cast<string>()
            .FirstOrDefault(s => s.StartsWith(cfg.Categories)) ?? "111 (all)";
        y += 32;

        AddLabel("Purity:",      y); _purity.Bounds = FieldRect(y); Controls.Add(_purity);
        _purity.DropDownStyle = ComboBoxStyle.DropDownList;
        _purity.Items.AddRange(new object[]
        {
            "100 (sfw)", "110 (sfw+suggestive)", "111 (all)",
            "011 (suggestive+restricted)", "001 (restricted)"
        });
        _purity.SelectedItem = _purity.Items.Cast<string>()
            .FirstOrDefault(s => s.StartsWith(cfg.Purity)) ?? "100 (sfw)";
        y += 32;

        AddLabel("Min resolution:", y); _atleast.Bounds = FieldRect(y); Controls.Add(_atleast);
        _atleast.Text = cfg.Atleast;
        y += 32;

        AddLabel("Aspect ratios:", y); _ratios.Bounds = FieldRect(y); Controls.Add(_ratios);
        _ratios.Text = cfg.Ratios;
        y += 32;

        AddLabel("Max wallpapers:", y); _maxWallpapers.Bounds = FieldRect(y); Controls.Add(_maxWallpapers);
        _maxWallpapers.Minimum = 1; _maxWallpapers.Maximum = 10_000;
        _maxWallpapers.Value = cfg.MaxWallpapers;
        y += 32;

        AddLabel("Rollout %:",   y); _rolloutPct.Bounds = FieldRect(y); Controls.Add(_rolloutPct);
        _rolloutPct.Minimum = 0; _rolloutPct.Maximum = 100;
        _rolloutPct.Value = cfg.RolloutPct;
        y += 40;

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Bounds = new Rectangle(280, y, 70, 28) };
        save.Click += (_, _) => Persist();
        Controls.Add(save);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(358, y, 70, 28) };
        Controls.Add(cancel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private void AddLabel(string text, int y) =>
        Controls.Add(new Label { Text = text, Bounds = new Rectangle(16, y + 4, 130, 20) });

    private static Rectangle FieldRect(int y) => new(150, y, 280, 24);

    private void Persist()
    {
        var o = _cfgFile.Overrides;

        // PRESET_KEYS-equivalent wipe so settings act as authoritative
        foreach (var k in new[] { "source", "q", "categories", "purity", "sorting",
                                   "order", "topRange", "atleast", "ratios",
                                   "maxWallpapers", "rolloutPct" })
            o.Remove(k);

        Set("source",      _source.SelectedItem?.ToString() ?? "wallhaven");
        Set("q",           _query.Text);
        Set("sorting",     _sorting.SelectedItem?.ToString() ?? "toplist");
        Set("categories",  FirstToken(_categories.SelectedItem?.ToString() ?? "111"));
        Set("purity",      FirstToken(_purity.SelectedItem?.ToString() ?? "100"));
        Set("atleast",     _atleast.Text);
        Set("ratios",      _ratios.Text);
        SetInt("maxWallpapers", (int)_maxWallpapers.Value);
        SetInt("rolloutPct",    (int)_rolloutPct.Value);

        _cfgFile.Save(Paths.ConfigFile);
    }

    private void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _cfgFile.Overrides[key] = System.Text.Json.JsonSerializer.SerializeToElement(value);
    }
    private void SetInt(string key, int value)
        => _cfgFile.Overrides[key] = System.Text.Json.JsonSerializer.SerializeToElement(value);

    private static string FirstToken(string s) =>
        s.Split(' ', 2)[0];
}
