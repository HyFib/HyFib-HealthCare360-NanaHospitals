using System.Windows;
using System.Windows.Forms;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// Notification-area icon. Alerts (critical lab values, new tokens, approvals) arrive as Windows notifications even
/// when HC360 is minimised; clicking one brings the window back.
/// </summary>
internal sealed class TrayNotifier : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayNotifier(Action activate)
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hc360.ico"));
        _icon = new NotifyIcon
        {
            Icon = resource is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(resource.Stream),
            Text = "HealthCare360",
            Visible = true,
        };
        _icon.BalloonTipClicked += (_, _) => activate();
        _icon.DoubleClick += (_, _) => activate();
    }

    public void Show(string title, string body)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            _icon.ShowBalloonTip(8000, Truncate(title, 63), Truncate(body, 255), ToolTipIcon.Info));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
