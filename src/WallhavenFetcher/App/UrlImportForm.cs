namespace WallhavenFetcher.App;

/// <summary>
/// Modal dialog: paste one or more direct image URLs (one per line) for
/// import. Wallhaven / konachan CDN URLs are recognized by the engine and
/// saved under their canonical source ID; anything else falls back to
/// generic content-hashed local-… naming.
/// </summary>
public sealed class UrlImportForm : Form
{
    private readonly TextBox _list = new();

    /// <summary>Trimmed list of http(s) URLs the user entered. Empty until OK is pressed.</summary>
    public List<string> Urls { get; private set; } = new();

    public UrlImportForm()
    {
        Text = "Wallhaven Fetcher — Import from URL(s)";
        Width = 640;
        Height = 380;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;

        var info = new Label
        {
            Text =
                "Paste one or more direct image URLs — one per line.\n" +
                "Wallhaven CDN URLs (e.g. https://w.wallhaven.cc/full/g7/wallhaven-g7xrgq.jpg) " +
                "are recognized and saved under their canonical ID, so they sync alongside " +
                "results from the matching search. Other URLs are content-hashed and saved as " +
                "generic local-… entries.",
            Bounds = new Rectangle(16, 12, 600, 70),
        };
        Controls.Add(info);

        Controls.Add(new Label
        {
            Text = "URLs (one per line):",
            Bounds = new Rectangle(16, 90, 200, 18),
        });

        _list.Multiline = true;
        _list.ScrollBars = ScrollBars.Vertical;
        _list.AcceptsReturn = true;
        _list.WordWrap = false;
        _list.Font = new Font(FontFamily.GenericMonospace, Font.Size);
        _list.Bounds = new Rectangle(16, 112, 600, 180);
        Controls.Add(_list);

        var ok = new Button
        {
            Text = "Import",
            DialogResult = DialogResult.OK,
            Bounds = new Rectangle(450, 304, 80, 30),
        };
        ok.Click += (_, _) => Urls = ExtractUrls(_list.Text);
        Controls.Add(ok);

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Bounds = new Rectangle(536, 304, 80, 30),
        };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    /// <summary>
    /// Split the textbox on whitespace and keep only entries that look like
    /// real http(s) URLs. Lets the user paste a comma-separated, newline-
    /// separated, or mixed list without fussing about the exact delimiter.
    /// </summary>
    private static List<string> ExtractUrls(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
                              s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                  .ToList();
    }
}
