using System.Text.RegularExpressions;

namespace WallhavenFetcher.App;

/// <summary>
/// Prompt the user for preset name + URL + optional rollout %. The URL gets
/// an &amp;rollout=N param appended (replacing any existing one) if the
/// dropdown picks a value other than "(don't override)".
/// </summary>
public sealed class PresetUrlForm : Form
{
    public string PresetName { get; private set; } = "";
    public string Url        { get; private set; } = "";

    private readonly TextBox  _name    = new();
    private readonly TextBox  _url     = new();
    private readonly ComboBox _rollout = new();

    private sealed record RolloutChoice(string Label, int? Value)
    {
        public override string ToString() => Label;
    }

    public PresetUrlForm()
    {
        Text = "Wallhaven Fetcher — Create preset from URL";
        Width = 600;
        Height = 320;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;

        var info = new Label
        {
            Text =
                "Paste a search URL from wallhaven.cc or konachan.com.\n" +
                "The source is auto-detected and the new preset is applied + synced immediately.",
            Bounds = new Rectangle(16, 12, 560, 48),
        };
        Controls.Add(info);

        Controls.Add(new Label { Text = "Preset name:", Bounds = new Rectangle(16, 70, 100, 20) });
        _name.Bounds = new Rectangle(120, 68, 450, 24);
        Controls.Add(_name);

        Controls.Add(new Label { Text = "URL:", Bounds = new Rectangle(16, 106, 100, 20) });
        _url.Bounds = new Rectangle(120, 104, 450, 24);
        Controls.Add(_url);

        Controls.Add(new Label { Text = "Rollout %:", Bounds = new Rectangle(16, 142, 100, 20) });
        _rollout.DropDownStyle = ComboBoxStyle.DropDownList;
        _rollout.Bounds = new Rectangle(120, 140, 450, 24);
        _rollout.Items.AddRange(new object[]
        {
            new RolloutChoice("(don't override — keep current setting)", null),
            new RolloutChoice("10% — gentle (~10 syncs to fully roll over)", 10),
            new RolloutChoice("25% — moderate (~4 syncs)",                  25),
            new RolloutChoice("50% — fast (~2 syncs)",                       50),
            new RolloutChoice("100% — instant (swap everything in one sync)", 100),
        });
        _rollout.SelectedIndex = 0;
        Controls.Add(_rollout);

        var ok = new Button
        {
            Text = "Create & apply",
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(400, 220, 110, 30),
        };
        ok.Click += (_, _) =>
        {
            PresetName = _name.Text.Trim();
            Url        = AppendRollout(_url.Text.Trim(),
                                       (_rollout.SelectedItem as RolloutChoice)?.Value);
        };
        Controls.Add(ok);

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(516, 220, 60, 30),
        };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>
    /// Apply the user's rollout-dropdown choice to the URL. If a value was
    /// chosen, strip any existing &amp;rollout=N then append the new one.
    /// </summary>
    private static string AppendRollout(string url, int? rollout)
    {
        if (rollout is null) return url;
        // Strip any pre-existing rollout= (so the dropdown wins over a value
        // the user already pasted into the URL).
        url = Regex.Replace(url, @"[?&]rollout=\d+", "");
        url += (url.Contains('?') ? "&" : "?") + "rollout=" + rollout.Value;
        return url;
    }
}
