using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>
/// Laboratory equipment gateway (INT-LAB). Every minute it reconciles the TCP listeners with
/// <c>integrations.lab</c>/<c>integrations.lab.instruments</c>: new analysers start listening, removed or changed ones
/// are restarted, and a port that could not be bound is retried. It runs only on nodes that run jobs
/// (<see cref="PlatformOptions.RunsJobs"/>), so a scaled-out API node never competes for analyser ports.
/// </summary>
internal sealed class LabGatewayHostedService(
    IServiceScopeFactory scopes,
    IOptions<PlatformOptions> platform,
    ILabMessageProcessor processor,
    LabGatewayState state,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions SignatureJson = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, (LabInstrumentListener Listener, string Signature)> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggers.CreateLogger<LabGatewayHostedService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!platform.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(RefreshInterval, time);
        try
        {
            do
            {
                await ReconcileAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
        finally
        {
            foreach (var (_, entry) in _listeners)
            {
                await entry.Listener.DisposeAsync();
            }

            _listeners.Clear();
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        LabGatewaySettings gateway;
        IReadOnlyList<LabInstrumentSettings> instruments;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<IntegrationSettingsReader>();
            gateway = await reader.ReadValueAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, cancellationToken) ?? new LabGatewaySettings();
            instruments = await reader.ReadValueAsync<List<LabInstrumentSettings>>(LabSettingKeys.Instruments, null, cancellationToken) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Database not ready yet (service start after a power cut): keep what is running and try again next tick.
            _logger.LogWarning(ex, "Could not read the laboratory gateway settings; keeping the current listeners");
            return;
        }

        var desired = new Dictionary<string, LabInstrumentSettings>(StringComparer.OrdinalIgnoreCase);
        if (gateway.Enabled)
        {
            foreach (var instrument in instruments.Where(i => i.Enabled))
            {
                if (instrument.Validate() is { } problem)
                {
                    _logger.LogWarning("Laboratory instrument {Instrument} ignored: {Problem}", instrument.Code, problem);
                }
                else
                {
                    desired.TryAdd(instrument.Code, instrument);
                }
            }
        }

        foreach (var (code, entry) in _listeners.ToList())
        {
            if (!desired.TryGetValue(code, out var instrument) || Signature(gateway, instrument) != entry.Signature)
            {
                await entry.Listener.DisposeAsync();
                _listeners.Remove(code);
                _logger.LogInformation("Stopped listening for instrument {Instrument}", code);
            }
        }

        var bindAddress = IPAddress.TryParse(gateway.BindAddress, out var address) ? address : IPAddress.Any;
        foreach (var (code, instrument) in desired.Where(d => !_listeners.ContainsKey(d.Key)))
        {
            var listener = new LabInstrumentListener(instrument, bindAddress, LabRuntimeOptions.From(gateway, instrument),
                gateway.MaxConnectionsPerInstrument, processor, state, time, loggers.CreateLogger<LabInstrumentListener>());
            try
            {
                listener.Start();
                _listeners[code] = (listener, Signature(gateway, instrument));
                _logger.LogInformation("Listening for instrument {Instrument} ({Protocol}) on {Address}:{Port}", code, instrument.Protocol, bindAddress, instrument.Port);
            }
            catch (SocketException ex)
            {
                await listener.DisposeAsync();
                state.Failed(code, instrument.Protocol, instrument.Port, $"Port {instrument.Port} could not be opened: {ex.SocketErrorCode}");
                _logger.LogError(ex, "Could not listen for instrument {Instrument} on port {Port}", code, instrument.Port);
            }
        }
    }

    private static string Signature(LabGatewaySettings gateway, LabInstrumentSettings instrument) =>
        JsonSerializer.Serialize(new { gateway, instrument }, SignatureJson);
}
