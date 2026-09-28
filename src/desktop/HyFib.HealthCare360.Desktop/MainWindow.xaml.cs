using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HyFib.HealthCare360.Desktop.Bridge;
using HyFib.HealthCare360.Desktop.Services;
using Microsoft.Web.WebView2.Core;
using Serilog;

namespace HyFib.HealthCare360.Desktop;

/// <summary>
/// Hosts the HC360 staff app. Security: only the pinned server origin may load in the view (anything else opens in
/// the default browser), the server certificate must chain to the hospital CA, password autosave/autofill are off
/// (shared workstations), and DevTools are disabled unless IT enables them.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly HashSet<CoreWebView2WebErrorStatus> ConnectivityErrors =
    [
        CoreWebView2WebErrorStatus.CannotConnect,
        CoreWebView2WebErrorStatus.Timeout,
        CoreWebView2WebErrorStatus.Disconnected,
        CoreWebView2WebErrorStatus.ConnectionAborted,
        CoreWebView2WebErrorStatus.ConnectionReset,
        CoreWebView2WebErrorStatus.HostNameNotResolved,
        CoreWebView2WebErrorStatus.ServerUnreachable,
    ];

    private readonly DesktopSettings _settings;
    private readonly string[] _args;
    private readonly Uri _server;
    private readonly ServerTrust _trust;
    private readonly ServerClient _client;
    private readonly NetworkStatusService _network;
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _lifetime = new();
    private TrayNotifier? _tray;
    private bool _pageLoaded;

    internal MainWindow(DesktopSettings settings, string[] args)
    {
        InitializeComponent();
        _settings = settings;
        _args = args;
        _server = settings.ServerUri ?? throw new InvalidOperationException("No server configured.");
        _trust = new ServerTrust(settings.CaCertificatePem!);
        _client = new ServerClient(_server, _trust);
        _network = new NetworkStatusService(_client);
        _retry.Tick += async (_, _) => await RetryAsync();

        if (settings.Kiosk)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            Topmost = true;
            ChangeServerButton.Visibility = Visibility.Collapsed;
        }

        Loaded += async (_, _) => await InitializeAsync();
    }

    internal bool RestartRequested { get; set; }

    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(DesktopSettings.AppDataDirectory, "WebView2"),
                options: new CoreWebView2EnvironmentOptions { Language = "en-IN" });
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Log.Fatal(ex, "WebView2 runtime missing");
            ShowOffline("Microsoft Edge WebView2 is not installed", "HC360 Desktop needs the WebView2 Runtime. Ask IT to install it (it is included in the HC360 installer).", null);
            RetryButton.Visibility = Visibility.Collapsed;
            return;
        }

        var core = Web.CoreWebView2;
        var kiosk = _settings.Kiosk;
        core.Settings.AreDevToolsEnabled = _settings.AllowDevTools;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = !kiosk;
        core.Settings.AreBrowserAcceleratorKeysEnabled = !kiosk;
        core.Settings.IsZoomControlEnabled = !kiosk;

        core.ServerCertificateErrorDetected += OnServerCertificateError;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        core.PermissionRequested += OnPermissionRequested;
        core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "HealthCare360" : core.DocumentTitle;

        _tray = new TrayNotifier(() => Activate([]));
        var printing = new PrintService(core.Environment, new WindowInteropHelper(this).Handle);
        new BridgeHost(core, _server, _settings, _network, printing, _tray).Attach();
        _network.Start();
        _ = HeartbeatLoopAsync(_lifetime.Token);

        Navigate(DeepLinks.Resolve(_args) ?? _settings.StartPath);
    }

    private void Navigate(string path)
    {
        var target = DeepLinks.IsSafePath(path) ? new Uri(_server, path) : _server;
        Web.CoreWebView2?.Navigate(target.AbsoluteUri);
    }

    /// <summary>Second launch or <c>hc360://</c> link: bring the window forward and open the requested screen.</summary>
    internal void Activate(string[] args)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        base.Activate();
        if (DeepLinks.Resolve(args) is { } path)
        {
            Navigate(path);
        }
    }

    private bool IsServerOrigin(Uri uri) =>
        Uri.Compare(uri, _server, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    private void OnServerCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.RequestUri);
            using var certificate = e.ServerCertificate.ToX509Certificate2();
            var trusted = IsServerOrigin(uri) && _trust.IsTrusted(certificate, uri.Host);
            e.Action = trusted ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
            if (!trusted)
            {
                Log.Warning("Rejected certificate for {Uri} ({Status}); subject {Subject}", e.RequestUri, e.ErrorStatus, certificate.Subject);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            e.Cancel = true;
            return;
        }

        if (IsServerOrigin(target) || target.Scheme is "about" or "data")
        {
            return;
        }

        e.Cancel = true;
        TryOpenExternally(target);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target))
        {
            if (IsServerOrigin(target))
            {
                Web.CoreWebView2.Navigate(target.AbsoluteUri);
            }
            else
            {
                TryOpenExternally(target);
            }
        }
    }

    /// <summary>
    /// The hospital's own app may use the camera/microphone (video consultations), the clipboard and notifications;
    /// everything else (location, USB, other sites) is denied without a prompt.
    /// </summary>
    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        var fromServer = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && IsServerOrigin(uri);
        var allowed = fromServer && e.PermissionKind is CoreWebView2PermissionKind.Camera
            or CoreWebView2PermissionKind.Microphone
            or CoreWebView2PermissionKind.ClipboardRead
            or CoreWebView2PermissionKind.Notifications;
        e.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        e.Handled = true;
        if (!allowed)
        {
            Log.Information("Denied {Permission} for {Uri}", e.PermissionKind, e.Uri);
        }
    }

    private static void TryOpenExternally(Uri target)
    {
        try
        {
            BridgeHost.OpenExternal(target.AbsoluteUri);
        }
        catch (BridgeException)
        {
            Log.Warning("Blocked navigation to {Uri}", target);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            _pageLoaded = true;
            HideOffline();
            return;
        }

        if (ConnectivityErrors.Contains(e.WebErrorStatus))
        {
            ShowOffline(
                "Cannot reach the HealthCare360 server",
                "Check that this computer is connected to the hospital WiFi. HC360 keeps trying and continues where you left off.",
                _server.Host);
            _retry.Start();
        }
        else if (e.WebErrorStatus is CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
                 or CoreWebView2WebErrorStatus.CertificateExpired
                 or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
                 or CoreWebView2WebErrorStatus.CertificateRevoked
                 or CoreWebView2WebErrorStatus.CertificateIsInvalid)
        {
            ShowOffline(
                "The server could not be verified",
                "For your security HC360 did not connect. The server's identity does not match the hospital certificate. Contact IT before continuing.",
                _server.Host);
        }
        else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            Log.Warning("Navigation failed: {Status}", e.WebErrorStatus);
        }
    }

    private async void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Error("WebView2 process failed: {Kind} {Reason}", e.ProcessFailedKind, e.Reason);
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            await Task.Delay(1000);
            Web.CoreWebView2?.Reload();
        }
    }

    private async Task RetryAsync()
    {
        if (await _client.PingAsync(_lifetime.Token) is null)
        {
            OfflineDetails.Text = $"{_server.Host} · last try {DateTime.Now:HH:mm:ss}";
            return;
        }

        _retry.Stop();
        if (_pageLoaded)
        {
            Web.CoreWebView2?.Reload();
        }
        else
        {
            Navigate(DeepLinks.Resolve(_args) ?? _settings.StartPath);
        }
    }

    private void ShowOffline(string title, string message, string? details)
    {
        OfflineTitle.Text = title;
        OfflineMessage.Text = message;
        OfflineDetails.Text = details ?? string.Empty;
        OfflinePanel.Visibility = Visibility.Visible;
    }

    private void HideOffline()
    {
        _retry.Stop();
        OfflinePanel.Visibility = Visibility.Collapsed;
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e) => await RetryAsync();

    private void OnChangeServerClick(object sender, RoutedEventArgs e) => ((App)Application.Current).Reconfigure(this);

    /// <summary>Reports this workstation's WiFi to the server each minute so IT can spot weak coverage (DED-06).</summary>
    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            if (_settings.DeviceId is { } deviceId && _settings.DeviceSecret is { } secret)
            {
                var status = _network.Current;
                var outcome = await _client.HeartbeatAsync(deviceId, secret, status.Ssid, status.SignalQuality, cancellationToken);
                if (outcome == HeartbeatOutcome.Revoked)
                {
                    Log.Warning("This device's enrolment was revoked by an administrator");
                    _tray?.Show("HC360 Desktop", "This computer's enrolment was revoked. Ask IT to enrol it again.");
                    return;
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Kiosks and TV boards are not closed by passers-by; IT signs the Windows session out instead.
        if (_settings.Kiosk && !RestartRequested)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _retry.Stop();
        _network.Dispose();
        _client.Dispose();
        _trust.Dispose();
        _tray?.Dispose();
        _lifetime.Dispose();
        base.OnClosed(e);
    }
}
