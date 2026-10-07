using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using HyFib.HealthCare360.Desktop.Bridge;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace HyFib.HealthCare360.Desktop;

/// <summary>
/// Another HC360 window, for a page the staff app opens in a new window (SRS TEL-04): the chart or the consultation
/// notes beside a video call, say, which then keeps running in the window that opened them. It shares the main
/// window's browser profile (so it is signed in), shows only the server's pages under the same rules
/// (<see cref="MainWindow.ApplyViewRules"/>), and closes with the main window, or when it locks because the server
/// could not be verified.
/// </summary>
public partial class StaffWindow : Window
{
    private readonly MainWindow _main;
    private readonly CoreWebView2Environment _environment;
    private readonly Uri _target;
    private BridgeHost? _bridge;

    internal StaffWindow(MainWindow main, CoreWebView2Environment environment, Uri target)
    {
        InitializeComponent();
        _main = main;
        _environment = environment;
        _target = target;
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await Web.EnsureCoreWebView2Async(_environment);
        var core = Web.CoreWebView2;
        _main.ApplyViewRules(core);
        core.DocumentTitleChanged += (_, _) =>
            Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "HealthCare360" : core.DocumentTitle;
        core.ProcessFailed += OnProcessFailed;
        _bridge = _main.CreateBridge(core, new WindowInteropHelper(this).Handle);
        _bridge.Attach();
        core.Navigate(_target.AbsoluteUri);
    }

    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error("WebView2 process failed in a second window: {Kind} {Reason}", e.ProcessFailedKind, e.Reason);
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
            or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            await Task.Delay(1000);
            Web.CoreWebView2?.Reload();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _bridge?.Detach();
        base.OnClosing(e);
    }
}
