using Microsoft.Toolkit.Uwp.Notifications;

namespace WallhavenFetcher.Notifications;

/// <summary>
/// Native Windows 10/11 toast notifications. Falls back to tray-balloon
/// if the toast API isn't available.
/// </summary>
public sealed class Notifier
{
    private readonly Action<string, string>? _fallbackBalloon;

    public Notifier(Action<string, string>? fallbackBalloon = null)
    {
        _fallbackBalloon = fallbackBalloon;
    }

    public void Show(string title, string message)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .Show();
        }
        catch (Exception)
        {
            _fallbackBalloon?.Invoke(title, message);
        }
    }
}
