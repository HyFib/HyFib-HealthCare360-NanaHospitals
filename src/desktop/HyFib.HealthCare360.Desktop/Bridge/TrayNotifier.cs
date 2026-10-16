using System.Windows;
using System.Windows.Forms;

namespace HyFib.HealthCare360.Desktop.Bridge;

/// <summary>
/// Notification-area icon. Alerts (critical lab values, new tokens, approvals) arrive as Windows notifications even
/// when HC360 is minimised; clicking one brings the window back. Its menu opens HC360, and offers "Restart to update"
/// once a new version of HC360 Desktop is ready to install (SRS DED-18).
/// </summary>
internal sealed class TrayNotifier : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _restart;
    private readonly Action _activate;
    private Action? _balloonClicked;
    private Action? _restartClicked;

    public TrayNotifier(Action activate)
    {
        _activate = activate;
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hc360.ico"));
        var open = new ToolStripMenuItem("Open HealthCare360");
        open.Click += (_, _) => activate();
        _restart = new ToolStripMenuItem("Restart to update") { Visible = false };
        _restart.Click += (_, _) => _restartClicked?.Invoke();
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([open, _restart]);
        _icon = new NotifyIcon
        {
            Icon = resource is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(resource.Stream),
            Text = "HealthCare360",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.BalloonTipClicked += (_, _) => (_balloonClicked ?? _activate)();
        _icon.DoubleClick += (_, _) => activate();
    }

    /// <summary>A Windows notification; clicking it runs <paramref name="clicked"/>, or brings HC360 back.</summary>
    public void Show(string title, string body, Action? clicked = null)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _balloonClicked = clicked;
            _icon.ShowBalloonTip(8000, Truncate(title, 63), Truncate(body, 255), ToolTipIcon.Info);
        });
    }

    /// <summary>Offers "Restart to update to <paramref name="version"/>" in the icon's menu.</summary>
    public void OfferRestart(string version, Action restart)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _restartClicked = restart;
            _restart.Text = $"Restart to update to {version}";
            _restart.Visible = true;
        });
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
