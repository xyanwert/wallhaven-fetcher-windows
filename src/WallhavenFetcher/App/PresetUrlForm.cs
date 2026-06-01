namespace WallhavenFetcher.App;

/// <summary>
/// Prompt the user for "name:url" — equivalent to the macOS osascript dialog.
/// </summary>
public sealed class PresetUrlForm : Form
{
    public string PresetName { get; private set; } = "";
    public string Url        { get; private set; } = "";

    private readonly TextBox _name = new();
    private readonly TextBox _url  = new();

    public PresetUrlForm()
    {
        Text = "Wallhaven Fetcher — Create preset from URL";
        Width = 580;
        Height = 280;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var info = new Label
        {
            Text =
                "Paste a search URL from wallhaven.cc or konachan.com.\n" +
                "The source is auto-detected and the new preset is applied + synced immediately.\n" +
                "Optional: append &rollout=N (0–100) to bake a rollout percentage into the preset.",
            Bounds = new Rectangle(16, 12, 540, 64),
        };
        Controls.Add(info);

        Controls.Add(new Label { Text = "Preset name:", Bounds = new Rectangle(16, 86, 100, 20) });
        _name.Bounds = new Rectangle(120, 84, 430, 24);
        Controls.Add(_name);

        Controls.Add(new Label { Text = "URL:", Bounds = new Rectangle(16, 122, 100, 20) });
        _url.Bounds = new Rectangle(120, 120, 430, 24);
        Controls.Add(_url);

        var ok = new Button
        {
            Text = "Create & apply",
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(384, 180, 110, 30),
        };
        ok.Click += (_, _) =>
        {
            PresetName = _name.Text.Trim();
            Url        = _url.Text.Trim();
        };
        Controls.Add(ok);

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(500, 180, 60, 30),
        };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }
}
