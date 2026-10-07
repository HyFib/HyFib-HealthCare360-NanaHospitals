using System.ComponentModel;
using System.IO;
using System.Security.Cryptography.X509Certificates;
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
/// <remarks>
/// WebView2 shows the app a server certificate only when Windows rejects it, which the hospital CA's certificates
/// always are. A certificate Windows trusts (from a company's TLS-inspection proxy, say) would reach the view without
/// the pin check, so the pinned <see cref="ServerClient"/> checks the path before the staff app loads and every 10
/// seconds after (<see cref="NetworkStatusService"/>). When it is offered a certificate that is not from the hospital
/// CA, the window unloads the staff app and stays locked until a check passes again (<see cref="Lock"/>).
/// </remarks>
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
    private readonly List<StaffWindow> _windows = [];
    private CoreWebView2Environment? _environment;
    private TrayNotifier? _tray;
    private bool _pageLoaded;

    // Set on the connection's thread when a pinned check is offered a certificate that is not from the hospital CA;
    // cleared when a retry reaches the server and is offered the hospital's certificate. While set, the staff app is
    // not loaded.
    private RejectedCertificate? _rejected;

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
        _trust.CertificateRejected += OnCertificateRejected;

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
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: Path.Combine(DesktopSettings.AppDataDirectory, "WebView2"),
                options: new CoreWebView2EnvironmentOptions { Language = "en-IN" });
            await Web.EnsureCoreWebView2Async(_environment);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            Log.Fatal(ex, "WebView2 runtime missing");
            ShowOffline("Microsoft Edge WebView2 is not installed", "HC360 Desktop needs the WebView2 Runtime. Ask IT to install it (it is included in the HC360 installer).", null);
            RetryButton.Visibility = Visibility.Collapsed;
            return;
        }

        var core = Web.CoreWebView2;
        ApplyViewRules(core);
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;
        core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "HealthCare360" : core.DocumentTitle;

        _tray = new TrayNotifier(() => Activate([]));
        CreateBridge(core, new WindowInteropHelper(this).Handle).Attach();
        _network.Start();
        _ = HeartbeatLoopAsync(_lifetime.Token);

        // The pinned check runs before the staff app (and the sign-in cookies it sends) goes to the server.
        await _client.PingAsync(_lifetime.Token);
        Navigate(DeepLinks.Resolve(_args) ?? _settings.StartPath);
    }

    /// <summary>
    /// The rules every view of the staff app follows, in this window and in the second windows it opens: only the
    /// server's pages, its certificate checked against the hospital CA, no saved passwords, and the permission and
    /// download policies below.
    /// </summary>
    internal void ApplyViewRules(CoreWebView2 core)
    {
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
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
    }

    /// <summary>
    /// The native bridge for a view of the staff app; it prints over the window <paramref name="owner"/>.
    /// </summary>
    internal BridgeHost CreateBridge(CoreWebView2 core, IntPtr owner)
    {
        var tray = _tray ?? throw new InvalidOperationException("The tray icon is created before any view.");
        var printing = new PrintService(core.Environment, owner);
        return new BridgeHost(core, _server, _settings, _network, printing, PrinterSetupStore.ForThisComputer(), tray);
    }

    private bool Locked => Volatile.Read(ref _rejected) is not null;

    private void Navigate(string path)
    {
        if (Locked)
        {
            return;
        }

        var target = DeepLinks.IsSafePath(path) ? new Uri(_server, path) : _server;
        Web.CoreWebView2?.Navigate(target.AbsoluteUri);
    }

    private void OnCertificateRejected(RejectedCertificate rejected)
    {
        if (Interlocked.Exchange(ref _rejected, rejected) is null)
        {
            Log.Warning(
                "Locked: {Host} presented a certificate that is not from the hospital CA "
                + "(subject {Subject}, issuer {Issuer}, SHA-256 {Fingerprint})",
                rejected.Host, rejected.Subject, rejected.Issuer, rejected.Fingerprint);
        }

        Dispatcher.InvokeAsync(() => Lock(rejected));
    }

    /// <summary>
    /// Unloads the staff app, so it sends nothing more through a connection that may be intercepted, and explains why.
    /// "Try again now" loads it again once the server presents the hospital's certificate; "Change server…" sets up a
    /// server whose certificate authority really changed.
    /// </summary>
    private void Lock(RejectedCertificate rejected)
    {
        if (!Locked)
        {
            return;
        }

        _retry.Stop();
        _pageLoaded = false;
        CloseWindows();
        if (Web.CoreWebView2 is { } core && !string.Equals(core.Source, "about:blank", StringComparison.Ordinal))
        {
            core.Navigate("about:blank");
        }

        var fingerprint = ServerTrust.Format(rejected.Fingerprint[..Math.Min(16, rejected.Fingerprint.Length)]);
        ShowOffline(
            "HC360 stopped: the server could not be verified",
            "The server answered with a certificate this computer does not accept. Something on the network may be "
            + "intercepting the connection, or the server's certificate was replaced or has expired. Do not sign in on "
            + "this computer until IT has checked it.",
            $"{rejected.Host} · certificate from {rejected.Issuer} · fingerprint {fingerprint}…");
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

    private bool IsServerOrigin(Uri uri) => NewWindowPolicy.IsSameOrigin(uri, _server);

    private void OnServerCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var uri = new Uri(e.RequestUri);
            using var certificate = e.ServerCertificate.ToX509Certificate2();

            // The issuers the server sent, so a certificate from an intermediate below the pinned CA validates.
            var intermediates = new X509Certificate2Collection();
            foreach (var pem in e.ServerCertificate.PemEncodedIssuerCertificateChain)
            {
                intermediates.Add(X509Certificate2.CreateFromPem(pem));
            }

            bool trusted;
            try
            {
                trusted = IsServerOrigin(uri) && _trust.IsTrusted(certificate, uri.Host, intermediates);
            }
            finally
            {
                foreach (var issuer in intermediates)
                {
                    issuer.Dispose();
                }
            }

            e.Action = trusted ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow : CoreWebView2ServerCertificateErrorAction.Cancel;
            if (!trusted)
            {
                Log.Warning("Rejected certificate for {Uri} ({Status}); subject {Subject}", e.RequestUri, e.ErrorStatus, certificate.Subject);
                if (IsServerOrigin(uri))
                {
                    OnCertificateRejected(RejectedCertificate.From(certificate, uri.Host));
                }
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

        if (target.Scheme is "about" or "data")
        {
            return;
        }

        if (IsServerOrigin(target))
        {
            e.Cancel = Locked;
            return;
        }

        e.Cancel = true;
        TryOpenExternally(target);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        switch (NewWindowPolicy.Decide(e.Uri, _server, _settings.Kiosk, Locked))
        {
            case NewWindowAction.OpenExternally:
                TryOpenExternally(new Uri(e.Uri));
                break;
            case NewWindowAction.OpenHere:
                (sender as CoreWebView2 ?? Web.CoreWebView2)?.Navigate(e.Uri);
                break;
            case NewWindowAction.OpenWindow:
                OpenWindow(new Uri(e.Uri));
                break;
        }
    }

    /// <summary>
    /// A second HC360 window for <paramref name="target"/>, so the screen that opened it stays as it is.
    /// </summary>
    private void OpenWindow(Uri target)
    {
        if (_environment is null)
        {
            return;
        }

        var window = new StaffWindow(this, _environment, target);
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        window.Show();
    }

    private void CloseWindows()
    {
        foreach (var window in _windows.ToList())
        {
            window.Close();
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

    /// <summary>Only files from the hospital server, and only document types HC360 produces (see <see cref="DownloadPolicy"/>).</summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (DownloadPolicy.IsAllowed(e.DownloadOperation.Uri, e.ResultFilePath, _server))
        {
            return;
        }

        e.Cancel = true;
        Log.Warning("Refused a download of {File} from {Uri}", Path.GetFileName(e.ResultFilePath), e.DownloadOperation.Uri);
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
        if (Locked)
        {
            return;
        }

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
        var rejected = Volatile.Read(ref _rejected);
        var latency = await _client.PingAsync(_lifetime.Token);
        if (latency is not null && rejected is not null)
        {
            // The pinned check reached the server and was offered the hospital's certificate, so the lock lifts, unless
            // another check was refused in the meantime. A check that cannot reach the server leaves the lock as it is.
            Interlocked.CompareExchange(ref _rejected, null, rejected);
        }

        if (Volatile.Read(ref _rejected) is { } still)
        {
            Lock(still);
            return;
        }

        if (latency is null)
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
        CloseWindows();
        _trust.CertificateRejected -= OnCertificateRejected;
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
