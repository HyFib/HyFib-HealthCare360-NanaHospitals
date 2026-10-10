using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>
/// Laboratory equipment gateway (INT-LAB). Every minute it reconciles the TCP listeners with
/// <c>integrations.lab</c>/<c>integrations.lab.instruments</c>: new network analysers start listening, removed or changed
/// ones are restarted, and a port that could not be bound is retried. Serial analysers have no listener: their lab agent
/// connects to the relay endpoint, and until it does they show as waiting. Every hour it expires and prunes the order
/// queue. It runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>), so a scaled-out API node never
/// competes for analyser ports.
/// </summary>
internal sealed class LabGatewayHostedService(
    IServiceScopeFactory scopes,
    IOptions<PlatformOptions> platform,
    IHostEnvironment environment,
    ILabMessageProcessor processor,
    LabGatewayState state,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HousekeepingInterval = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions SignatureJson = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, (LabInstrumentListener Listener, string Signature)> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger = loggers.CreateLogger<LabGatewayHostedService>();
    private DateTimeOffset _nextHousekeeping;

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
                await HousekeepAsync(stoppingToken);
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

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
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
        var serial = new List<LabInstrumentSettings>();
        var training = new List<LabInstrumentSettings>();
        if (gateway.Enabled)
        {
            foreach (var instrument in instruments.Where(i => i.Enabled))
            {
                if (instrument.Validate() is { } problem)
                {
                    _logger.LogWarning("Laboratory instrument {Instrument} ignored: {Problem}", instrument.Code, problem);
                }
                else if (instrument.Transport == LabTransport.Serial)
                {
                    serial.Add(instrument);
                }
                else if (instrument.Transport == LabTransport.Simulator)
                {
                    training.Add(instrument);
                }
                else
                {
                    desired.TryAdd(instrument.Code, instrument);
                }
            }
        }

        // Stop obsolete listeners first: stopping one forgets its analyser's status, which for an analyser moved from the
        // network to serial or training is the status set just below.
        foreach (var (code, entry) in _listeners.ToList())
        {
            if (!desired.TryGetValue(code, out var instrument) || Signature(gateway, instrument) != entry.Signature)
            {
                await entry.Listener.DisposeAsync();
                _listeners.Remove(code);
                _logger.LogInformation("Stopped listening for instrument {Instrument}", code);
            }
        }

        foreach (var instrument in serial)
        {
            state.SerialExpected(instrument.Code, instrument.Protocol, instrument.Serial!.PortName.ToUpperInvariant());
        }

        state.RetainSerial(serial.Select(i => i.Code).ToList());

        foreach (var instrument in training)
        {
            state.TrainingReady(instrument.Code, instrument.Protocol);
        }

        state.RetainTraining(training.Select(i => i.Code).ToList());

        var bindAddress = IPAddress.TryParse(gateway.BindAddress, out var address) ? address : IPAddress.Any;
        foreach (var (code, instrument) in desired.Where(d => !_listeners.ContainsKey(d.Key)))
        {
            // A port the server itself listens on, such as the patient portal's since the portal has an address of its own
            // (an analyser may have been given it before): binding it first would stop the server starting. Say why instead.
            if (Listeners.ServerPortUse(instrument.Port, platform.Value, Listeners.ApiReferenceOn(platform.Value, environment)) is { } use)
            {
                state.Failed(code, instrument.Protocol, instrument.Port,
                    $"Port {instrument.Port} is used by the HC360 server itself, for {use}. Choose another port for this analyser, in its connection here and on the analyser.");
                _logger.LogError("Laboratory instrument {Instrument} is set to port {Port}, which the HC360 server uses for {Use}: it is not listened for", code, instrument.Port, use);
                continue;
            }

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

    /// <summary>Orders not sent within their lifetime expire; finished ones are deleted after 30 days.</summary>
    private async Task HousekeepAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (now < _nextHousekeeping)
        {
            return;
        }

        _nextHousekeeping = now + HousekeepingInterval;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var (expired, deleted) = await scope.ServiceProvider.GetRequiredService<LabOrderQueue>().HousekeepAsync(cancellationToken);
            if (expired > 0)
            {
                _logger.LogWarning("{Count} analyser order(s) expired: their analysers were not connected within {Hours} hours", expired, LabOrderQueue.Lifetime.TotalHours);
            }

            if (deleted > 0)
            {
                _logger.LogInformation("Deleted {Count} finished analyser order(s)", deleted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Analyser order housekeeping failed; it runs again in an hour");
        }
    }

    private static string Signature(LabGatewaySettings gateway, LabInstrumentSettings instrument) =>
        JsonSerializer.Serialize(new { gateway, instrument }, SignatureJson);
}
