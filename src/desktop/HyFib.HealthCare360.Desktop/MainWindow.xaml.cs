using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HyFib.HealthCare360.Desktop.Bridge;
using HyFib.HealthCare360.Desktop.Services;
using HyFib.HealthCare360.WindowsInterop.Trust;
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
    private readonly DesktopUpdateService _updates;
    private readonly SerialScannerService _scanner = new();
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<StaffWindow> _windows = [];
    private readonly string? _notice;
    private CoreWebView2Environment? _environment;
    private TrayNotifier? _tray;
    private bool _pageLoaded;

    // The version of HC360 Desktop staged and offered with "Restart to update" (SRS DED-18).
    private string? _updateOffered;

    // While the staff app cannot load: tries in a row that the server did not answer, and when to look for it at its
    // other addresses next (SRS DED-03).
    private int _missedTries;
    private DateTime _nextSearch;
    private bool _searching;

    // Set on the connection's thread when a pinned check is offered a certificate that is not from the hospital CA;
    // cleared when a retry reaches the server and is offered the hospital's certificate. While set, the staff app is
    // not loaded.
    private RejectedCertificate? _rejected;

    /// <param name="notice">Shown from the tray once the window is up, such as that the server moved to another address.</param>
    internal MainWindow(DesktopSettings settings, string[] args, string? notice = null)
    {
        InitializeComponent();
        _settings = settings;
        _args = args;
        _notice = notice;
        _server = settings.ServerUri ?? throw new InvalidOperationException("No server configured.");
        _trust = new ServerTrust(settings.CaCertificatePem!, settings.PreviousCaCertificatePem);
        _client = new ServerClient(_server, _trust);
        _network = new NetworkStatusService(_client);
        _updates = new DesktopUpdateService(_server, _trust, settings);
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
        if (_notice is not null)
        {
            _tray.Show("HC360 Desktop", _notice);
        }

        CreateBridge(core, new WindowInteropHelper(this).Handle).Attach();
        _network.Start();
        _scanner.Use(ScannerSetupStore.ForThisComputer().Load().Setup);
        _ = HeartbeatLoopAsync(_lifetime.Token);
        _ = UpdateLoopAsync(_lifetime.Token);
        _ = FollowCaLoopAsync(_lifetime.Token);

        // The pinned check runs before the staff app (and the sign-in cookies it sends) goes to the server.
        if (await _client.PingAsync(_lifetime.Token) is not null)
        {
            RememberServer();
        }

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
        return new BridgeHost(
            core, _server, _settings, _network, printing, PrinterSetupStore.ForThisComputer(), ScannerSetupStore.ForThisComputer(), _scanner, tray);
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
            _missedTries++;
            await SearchElsewhereAsync();
            return;
        }

        _missedTries = 0;
        RememberServer();
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

    /// <summary>
    /// While the staff app cannot load and the server has not answered three tries in a row, looks for it at its other
    /// addresses, at most once a minute (SRS DED-03), and opens the window again on the first where it answers. Never
    /// when IT fixed the server in the machine policy, and never while the staff app is loaded, so no work is lost.
    /// </summary>
    private async Task SearchElsewhereAsync()
    {
        if (_settings.ServerFixedByPolicy || _searching || _missedTries < 3 || DateTime.UtcNow < _nextSearch)
        {
            return;
        }

        _searching = true;
        _nextSearch = DateTime.UtcNow.AddMinutes(1);
        try
        {
            OfflineDetails.Text = $"{_server.Host} · looking for the server at its other addresses…";
            var other = await ServerFallback.FindAsync(_server, _settings, _trust.Fingerprint, _lifetime.Token);

            // The usual address may have answered meanwhile (the retries go on), or a refused certificate locked the window.
            if (other is null || !_retry.IsEnabled || Locked)
            {
                OfflineDetails.Text = $"{_server.Host} · last try {DateTime.Now:HH:mm:ss}";
                return;
            }

            Log.Warning("The server does not answer at {Server}, but does at {Other}, which HC360 uses from now on", _server, other);
            _settings.ServerUrl = ServerFallback.Normalize(other);
            _settings.RememberServer(other);
            _settings.Save();
            ((App)Application.Current).SwitchServer(
                this, $"HC360 now connects to the server at {other.Authority}, because {_server.Authority} did not answer. Sign in again to go on.");
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "The server's new address could not be saved");
        }
        finally
        {
            _searching = false;
        }
    }

    /// <summary>Puts the address the server just answered on first among those to try (SRS DED-03).</summary>
    private void RememberServer()
    {
        if (!_settings.RememberServer(_server))
        {
            return;
        }

        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "The server's address could not be remembered");
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
        _missedTries = 0;
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

    /// <summary>
    /// After the hospital CA was rolled over ('certificates roll-ca'), takes the new CA on the word of the CA this workstation
    /// pins, a minute after start and every six hours: the server sends, with its own certificate, the one its old CA signed
    /// for the new CA's key. The CA pinned until then stays a second pin until it expires, and the heartbeat reports both, so
    /// System health can name the workstations that have not moved (CA-ROLLOVER.md).
    /// </summary>
    private async Task FollowCaLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            do
            {
                await FollowCaAsync(cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
    }

    private async Task FollowCaAsync(CancellationToken cancellationToken)
    {
        HospitalCaSuccessor? successor;
        try
        {
            successor = await _client.FindCaSuccessorAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or CryptographicException
            || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Log.Debug(ex, "Could not ask the server for its CA; trying again later");
            return;
        }

        if (successor is null)
        {
            return;
        }

        var before = (_settings.CaCertificatePem, _settings.CaFingerprint, _settings.PreviousCaCertificatePem, _settings.CaCrossCertificatePem);
        try
        {
            _settings.Follow(successor);
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saved: the CA pinned so far stays, and the next try takes the new one again.
            (_settings.CaCertificatePem, _settings.CaFingerprint, _settings.PreviousCaCertificatePem, _settings.CaCrossCertificatePem) = before;
            Log.Error(ex, "The hospital's new CA could not be saved");
            return;
        }

        _trust.Pins.Follow(successor);
        Log.Information(
            "The hospital CA was rolled over: HC360 Desktop now pins the new CA {Fingerprint}, and the one it replaced until that expires",
            successor.Fingerprint);
    }

    /// <summary>
    /// HC360 Desktop's self-update from the Server Node (SRS DED-18): two minutes after start and every four hours, asks
    /// the server for a newer version and stages it (<see cref="DesktopUpdateService"/>), then offers "Restart to update".
    /// Kiosks and TV boards are not asked: theirs installs at the next start-up or sign-in, when HC360 is not running.
    /// </summary>
    private async Task UpdateLoopAsync(CancellationToken cancellationToken)
    {
        if (!_updates.Enabled)
        {
            Log.Information(_settings.AutoUpdateAllowed
                ? "HC360 Desktop does not update itself: its updater is not installed (run Install-HC360Desktop.ps1)"
                : "HC360 Desktop does not update itself: IT turned it off in the machine policy");
            return;
        }

        try
        {
            OfferUpdate(await Task.Run(
                () =>
                {
                    _updates.CleanUp();
                    return _updates.Ready();
                },
                cancellationToken));
            await Task.Delay(TimeSpan.FromMinutes(2), cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromHours(4));
            do
            {
                OfferUpdate(await Task.Run(() => _updates.CheckAsync(cancellationToken), cancellationToken));
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
    }

    private void OfferUpdate(WindowsInterop.Updates.ReleaseManifest? ready)
    {
        if (ready is null || _settings.Kiosk || string.Equals(_updateOffered, ready.Version, StringComparison.Ordinal))
        {
            return;
        }

        _updateOffered = ready.Version;
        _tray?.OfferRestart(ready.Version, RestartToUpdate);
        _tray?.Show(
            $"HC360 Desktop {ready.Version} is ready",
            "Click here to restart HC360 and install it now, or it installs the next time HC360 is closed.",
            RestartToUpdate);
        RestartToUpdateButton.Content = $"Restart to update to {ready.Version}";
        RestartToUpdateButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// After asking (staff may have unsaved work): closes HC360 Desktop, which the updater installs and opens again.
    /// </summary>
    private void RestartToUpdate()
    {
        if (_updateOffered is not { } version)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Restart HC360 Desktop now to install version {version}?\n\nSave your work first: HC360 closes, and opens again in a minute or so.",
            "HC360 Desktop",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        if (!_updates.StartRestart())
        {
            MessageBox.Show(
                this,
                "The update could not be started now. It installs the next time HC360 Desktop is closed or this computer starts.",
                "HC360 Desktop",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Log.Information("Closing to install HC360 Desktop {Version}", version);
        RestartRequested = true;
        Application.Current.Shutdown();
    }

    private void OnRestartToUpdateClick(object sender, RoutedEventArgs e) => RestartToUpdate();

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
        _updates.Dispose();
        _scanner.Dispose();
        _client.Dispose();
        _trust.Dispose();
        _tray?.Dispose();
        _lifetime.Dispose();
        base.OnClosed(e);
    }
}
