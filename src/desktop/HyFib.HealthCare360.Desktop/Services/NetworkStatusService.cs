using System.Net.NetworkInformation;
using HyFib.HealthCare360.WindowsInterop;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>
/// Shape sent to the web app over the bridge (see packages/core/src/bridge/bridge.ts NetworkStatus). <see cref="Wired"/>:
/// a network cable is up, which is then the workstation's link, not its WiFi (SRS DED-07 on a cabled LAN, as at Nana
/// Hospitals); <see cref="CableSpeedMbps"/> is the fastest cable's speed, when Windows gives it.
/// </summary>
internal sealed record NetworkStatus(
    bool WifiConnected,
    string? Ssid,
    int? SignalQuality,
    double? ReceiveRateMbps,
    string? Security,
    bool DetailsAvailable,
    string? Note,
    bool ServerReachable,
    int? ServerLatencyMs,
    bool Wired,
    long? CableSpeedMbps);

/// <summary>
/// Watches this workstation's link, its network cable or its WiFi, and the round-trip to the server every 10 seconds
/// (sooner when Windows reports a network change) and raises <see cref="Changed"/> when something the user would notice
/// changes.
/// </summary>
internal sealed class NetworkStatusService(ServerClient client) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private NetworkStatus? _last;

    public event Action<NetworkStatus>? Changed;

    public NetworkStatus Current => _last ?? Sample(null);

    public void Start()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        _ = Task.Run(() => LoopAsync(_stop.Token));
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _wake.Release();

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var latency = await client.PingAsync(cancellationToken);
                var status = Sample(latency);
                if (_last is null || Differs(_last, status))
                {
                    if (_last?.ServerReachable != status.ServerReachable)
                    {
                        Log.Information("Server {State} (cable {Wired}, WiFi {Ssid}, signal {Signal})", status.ServerReachable ? "reachable" : "unreachable",
                            status.Wired ? "up" : "not up", status.Ssid, status.SignalQuality);
                    }

                    _last = status;
                    Changed?.Invoke(status);
                }
                else
                {
                    _last = status;
                }

                await _wake.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Network status sampling failed");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static NetworkStatus Sample(TimeSpan? latency)
    {
        WifiConnectionInfo? wifi = null;
        try
        {
            wifi = WlanClient.GetPrimaryConnection();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Debug(ex, "WLAN query failed");
        }

        IReadOnlyList<NetworkLink> cables = [];
        try
        {
            cables = NetworkLinks.Cables(NetworkLinks.Read());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Debug(ex, "Network link query failed");
        }

        return Describe(wifi, cables, NetworkInterface.GetIsNetworkAvailable(), latency);
    }

    /// <summary>
    /// What the web app is told, from this workstation's WiFi (null without a WiFi adapter), its network cables that are
    /// up, and the server's round-trip (null when it did not answer). Without a WiFi adapter, <c>WifiConnected</c> says
    /// whether Windows has a network at all, as before, for a staff app that does not know <c>Wired</c>.
    /// </summary>
    internal static NetworkStatus Describe(WifiConnectionInfo? wifi, IReadOnlyList<NetworkLink> cables, bool networkAvailable, TimeSpan? latency) =>
        new(
            wifi?.IsConnected ?? networkAvailable,
            wifi?.Ssid,
            wifi is { DetailsAvailable: true } ? wifi.SignalQuality : null,
            wifi?.ReceiveRateMbps,
            wifi?.Security.ToString(),
            wifi?.DetailsAvailable ?? false,
            wifi?.Note ?? (wifi is null ? (cables.Count > 0 ? "On a network cable" : "Wired or no WiFi adapter") : null),
            latency is not null,
            latency is { } l ? (int)Math.Round(l.TotalMilliseconds) : null,
            cables.Count > 0,
            cables.Max(c => c.SpeedMbps));

    private static bool Differs(NetworkStatus a, NetworkStatus b) =>
        a.WifiConnected != b.WifiConnected
        || a.Wired != b.Wired
        || a.ServerReachable != b.ServerReachable
        || !string.Equals(a.Ssid, b.Ssid, StringComparison.Ordinal)
        || Math.Abs((a.SignalQuality ?? 0) - (b.SignalQuality ?? 0)) >= 10;

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        _stop.Cancel();
        _stop.Dispose();
        _wake.Dispose();
    }
}
