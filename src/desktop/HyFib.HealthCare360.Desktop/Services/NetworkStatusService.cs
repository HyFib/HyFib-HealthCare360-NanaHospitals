using System.Net.NetworkInformation;
using HyFib.HealthCare360.WindowsInterop;
using Serilog;

namespace HyFib.HealthCare360.Desktop.Services;

/// <summary>Shape sent to the web app over the bridge (see packages/core/src/bridge/bridge.ts NetworkStatus).</summary>
internal sealed record NetworkStatus(
    bool WifiConnected,
    string? Ssid,
    int? SignalQuality,
    double? ReceiveRateMbps,
    string? Security,
    bool DetailsAvailable,
    string? Note,
    bool ServerReachable,
    int? ServerLatencyMs);

/// <summary>
/// Watches this workstation's WiFi link and the round-trip to the server every 10 seconds (sooner when Windows
/// reports a network change) and raises <see cref="Changed"/> when something the user would notice changes.
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
                        Log.Information("Server {State} (WiFi {Ssid}, signal {Signal})", status.ServerReachable ? "reachable" : "unreachable", status.Ssid, status.SignalQuality);
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

        return new NetworkStatus(
            wifi?.IsConnected ?? NetworkInterface.GetIsNetworkAvailable(),
            wifi?.Ssid,
            wifi is { DetailsAvailable: true } ? wifi.SignalQuality : null,
            wifi?.ReceiveRateMbps,
            wifi?.Security.ToString(),
            wifi?.DetailsAvailable ?? false,
            wifi?.Note ?? (wifi is null ? "Wired or no WiFi adapter" : null),
            latency is not null,
            latency is { } l ? (int)Math.Round(l.TotalMilliseconds) : null);
    }

    private static bool Differs(NetworkStatus a, NetworkStatus b) =>
        a.WifiConnected != b.WifiConnected
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
