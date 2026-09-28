using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using HyFib.HealthCare360.Desktop.Services;
using Serilog;

namespace HyFib.HealthCare360.Desktop;

/// <summary>
/// First-run setup (SRS DED-05): find the Server Node over mDNS (or type it), verify the hospital CA fingerprint
/// out-of-band, then enrol this computer with a one-time code. Nothing is trusted until the fingerprint is either
/// confirmed by a person or matches the value IT pinned in the machine policy.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly DesktopSettings _settings;
    private int _step = 1;
    private Uri? _serverUri;
    private string? _caPem;
    private string? _fingerprint;
    private SystemInfo? _info;

    internal SetupWindow(DesktopSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        DeviceName.Text = settings.DeviceName ?? Environment.MachineName;
        if (settings.ServerFixedByPolicy && settings.ServerUri is not null)
        {
            ManualAddress.Text = settings.ServerUri.Authority;
            ManualAddress.IsEnabled = false;
        }

        Loaded += async (_, _) => await DiscoverAsync();
    }

    private async Task DiscoverAsync()
    {
        DiscoverStatus.Text = "Searching the network…";
        ServerList.ItemsSource = null;
        var servers = await ServerDiscovery.BrowseAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
        ServerList.ItemsSource = servers;
        DiscoverStatus.Text = servers.Count switch
        {
            0 => "No server found automatically. Type the server name or address below (ask IT if unsure).",
            1 => "Found the hospital server.",
            _ => $"Found {servers.Count} servers. Choose yours.",
        };
        if (servers.Count == 1 && !_settings.ServerFixedByPolicy)
        {
            ServerList.SelectedIndex = 0;
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private void OnServerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ServerList.SelectedItem is DiscoveredServer server && !_settings.ServerFixedByPolicy)
        {
            ManualAddress.Text = $"{server.HostName}:{server.Port}";
        }
    }

    private void OnConfirmChanged(object sender, RoutedEventArgs e) => NextButton.IsEnabled = FingerprintConfirmed.IsChecked == true;

    private async void OnNextClick(object sender, RoutedEventArgs e)
    {
        HideError();
        NextButton.IsEnabled = false;
        try
        {
            switch (_step)
            {
                case 1:
                    await ConnectAsync();
                    break;
                case 2:
                    ShowStep(3);
                    break;
                case 3:
                    await EnrolAsync();
                    break;
            }
        }
        finally
        {
            NextButton.IsEnabled = _step != 2 || FingerprintConfirmed.IsChecked == true;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnSkipClick(object sender, RoutedEventArgs e) => Finish(enrolled: false);

    private async Task ConnectAsync()
    {
        var selected = ServerList.SelectedItem as DiscoveredServer;
        var candidates = new List<Uri>();
        if (ParseAddress(ManualAddress.Text) is { } typed)
        {
            candidates.Add(typed);
        }

        if (selected is not null)
        {
            candidates.AddRange(selected.CandidateUris());
        }

        if (candidates.Count == 0)
        {
            ShowError("Choose a server from the list or type its name or address.");
            return;
        }

        foreach (var candidate in candidates.Distinct())
        {
            try
            {
                var (pem, fingerprint) = await ServerClient.FetchCaCertificateAsync(candidate, CancellationToken.None);
                using var trust = new ServerTrust(pem);
                using var client = new ServerClient(candidate, trust);
                // Proves the server's own TLS certificate chains to the CA it handed out and names this address.
                var info = await client.GetSystemInfoAsync(CancellationToken.None);
                if (info is null || !string.Equals(info.CaFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _serverUri = candidate;
                _caPem = pem;
                _fingerprint = fingerprint;
                _info = info;
                ShowVerification(selected?.CaFingerprint);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ServerProblemException or System.Security.Cryptography.CryptographicException)
            {
                Log.Information(ex, "Could not connect to {Candidate}", candidate);
            }
        }

        ShowError("Could not connect securely to that server. Check the name or address, and that the server is switched on and on the same network.");
    }

    private void ShowVerification(string? advertisedFingerprint)
    {
        VerifyServer.Text = $"{_info!.ServerName} ({_serverUri!.Authority})";
        VerifyFacility.Text = $"{_info.FacilityName ?? "Hospital"} · HealthCare360 {_info.Version}";
        Fingerprint.Text = ServerTrust.Format(_fingerprint!);

        var expected = _settings.PolicyCaFingerprint ?? advertisedFingerprint;
        if (_settings.PolicyCaFingerprint is not null)
        {
            var matches = string.Equals(_settings.PolicyCaFingerprint, _fingerprint, StringComparison.Ordinal);
            FingerprintMatch.Text = matches
                ? "✔ Matches the fingerprint set by your IT department."
                : "✖ Does NOT match the fingerprint set by your IT department. Do not continue; contact IT.";
            FingerprintConfirmed.IsChecked = matches;
            FingerprintConfirmed.IsEnabled = false;
            if (!matches)
            {
                ShowStep(2);
                NextButton.IsEnabled = false;
                return;
            }
        }
        else if (expected is not null)
        {
            FingerprintMatch.Text = string.Equals(expected, _fingerprint, StringComparison.Ordinal)
                ? "✔ Matches the fingerprint the server announced on the network."
                : "⚠ Differs from what was announced on the network. Check carefully before continuing.";
        }

        ShowStep(2);
    }

    private async Task EnrolAsync()
    {
        var code = EnrolmentCode.Text.Trim();
        var name = DeviceName.Text.Trim();
        if (code.Replace("-", string.Empty, StringComparison.Ordinal).Length != 8)
        {
            ShowError("Enter the 8-character enrolment code, for example ABCD-1234.");
            return;
        }

        if (name.Length is 0 or > 100)
        {
            ShowError("Enter a name for this computer (up to 100 characters), for example OPD Reception 2.");
            return;
        }

        try
        {
            using var trust = new ServerTrust(_caPem!);
            using var client = new ServerClient(_serverUri!, trust);
            var result = await client.EnrollAsync(code, name, CancellationToken.None);
            if (result.CaFingerprint is not null && !string.Equals(result.CaFingerprint, _fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                ShowError("The server's certificate changed during enrolment. Start again.");
                return;
            }

            _settings.DeviceId = result.DeviceId;
            _settings.DeviceSecret = result.DeviceSecret;
            _settings.DeviceName = name;
            Finish(enrolled: true);
        }
        catch (ServerProblemException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ShowError("The server could not be reached. Check the WiFi connection and try again.");
        }
    }

    private void Finish(bool enrolled)
    {
        var sameServer = string.Equals(_settings.CaFingerprint, _fingerprint, StringComparison.Ordinal);
        _settings.ServerUrl = _serverUri!.GetLeftPart(UriPartial.Authority) + "/";
        _settings.CaCertificatePem = _caPem;
        _settings.CaFingerprint = _fingerprint;
        if (!enrolled && !sameServer)
        {
            // An enrolment belongs to one server; forget it when switching servers.
            _settings.DeviceId = null;
            _settings.ProtectedDeviceSecret = null;
        }

        _settings.Save();
        Log.Information("Configured server {Server} (CA {Fingerprint}), enrolled: {Enrolled}", _settings.ServerUrl, _fingerprint, enrolled);
        DialogResult = true;
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 1, 3);
        DiscoverPanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        VerifyPanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        EnrolPanel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step > 1 ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step switch { 1 => "Connect", 2 => "Trust this server", _ => "Enrol" };
        NextButton.IsEnabled = _step != 2 || FingerprintConfirmed.IsChecked == true;
        StepTitle.Text = _step switch
        {
            1 => "Step 1 of 3 · Find the hospital server",
            2 => "Step 2 of 3 · Verify the server",
            _ => "Step 3 of 3 · Enrol this computer",
        };
        HideError();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    /// <summary>"HC360-SRV", "HC360-SRV:7360", "192.168.1.20" or a full https URL → https URI (default port 7360).</summary>
    internal static Uri? ParseAddress(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var port = uri.IsDefaultPort && !value.Contains(":443", StringComparison.Ordinal) ? 7360 : uri.Port;
        return new UriBuilder(Uri.UriSchemeHttps, uri.Host, port).Uri;
    }
}
