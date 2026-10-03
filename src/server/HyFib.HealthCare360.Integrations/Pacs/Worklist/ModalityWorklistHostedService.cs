using System.Net.Sockets;
using FellowOakDicom.Network;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// The DICOM modality worklist server (IMG-01, IMG-02): modalities ask it (C-FIND) for the studies they are to do, and
/// get the patient's details, the accession number and the study UID HC360 assigned, so nobody types them at the
/// console. Every minute it applies <c>integrations.mwl</c>: an allow-list change takes effect at once for new
/// associations; a change of address, port, AE title or association limit restarts the listener; turning it off stops
/// it. A port that could not be opened is tried again at the next minute. It runs only on nodes that run jobs
/// (<see cref="PlatformOptions.RunsJobs"/>), so a scaled-out API node never competes for the port.
/// </summary>
internal sealed class ModalityWorklistHostedService(
    IServiceScopeFactory scopes,
    IOptions<PlatformOptions> platform,
    IDicomServerFactory servers,
    ModalityWorklistGateway gateway,
    TimeProvider time,
    ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly ILogger _logger = loggers.CreateLogger<ModalityWorklistHostedService>();
    private IDicomServer? _server;
    private WorklistListener? _listener;

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
            await StopServerAsync();
            gateway.State.Stopped();
        }
    }

    /// <summary>Reads the settings and brings the listener in line with them.</summary>
    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        WorklistConfig config;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<IntegrationSettingsReader>();
            var settings = await reader.ReadValueAsync<WorklistSettings>(WorklistSettings.Key, null, cancellationToken);
            var facilities = settings?.Enabled == true
                ? await scope.ServiceProvider.GetRequiredService<IWorklistFacilities>().ActiveAsync(cancellationToken)
                : [];
            config = WorklistConfig.From(settings, facilities);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Database not ready yet (a start after a power cut): keep what is running and try again at the next minute.
            _logger.LogWarning(ex, "Could not read the modality worklist settings; keeping the current listener");
            return;
        }

        gateway.Apply(config);
        var wanted = config.Enabled ? config.Listener : null;
        if (_server is not null && (wanted != _listener || !_server.IsListening))
        {
            await StopServerAsync();
            _logger.LogInformation("Stopped serving the modality worklist");
        }

        if (wanted is null)
        {
            if (config.Enabled && config.Problems.Count > 0)
            {
                gateway.State.Failed(config.AeTitle.Length > 0 ? config.AeTitle : "MWL", 0, config.Problems[0]);
            }
            else
            {
                gateway.State.Stopped();
            }

            return;
        }

        if (_server is not null)
        {
            return;
        }

        try
        {
            _server = servers.Create<ModalityWorklistService>(
                wanted.BindAddress,
                wanted.Port,
                logger: new DemotedLogger(loggers.CreateLogger("HyFib.HealthCare360.Integrations.Pacs.Worklist.Dicom")),
                configure: options => options.MaxClientsAllowed = wanted.MaxAssociations + 2);
            _listener = wanted;
            gateway.State.Listening(wanted.AeTitle, wanted.Port);
            _logger.LogInformation("Serving the modality worklist as {AeTitle} on {Address}:{Port}", wanted.AeTitle, wanted.BindAddress, wanted.Port);
        }
        catch (Exception ex) when (ex is DicomNetworkException or SocketException)
        {
            var reason = (ex.InnerException as SocketException ?? ex as SocketException)?.SocketErrorCode.ToString() ?? "the port is in use or not allowed";
            gateway.State.Failed(wanted.AeTitle, wanted.Port, $"Port {wanted.Port} could not be opened: {reason}.");
            _logger.LogError(ex, "Could not serve the modality worklist on port {Port}", wanted.Port);
        }
    }

    private async Task StopServerAsync()
    {
        if (_server is null)
        {
            return;
        }

        var running = _server.Registration?.Task;
        _server.Dispose();
        _server = null;
        _listener = null;
        if (running is null)
        {
            return;
        }

        try
        {
            // Wait for the port to be released before it is opened again.
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "The modality worklist listener stopped with an error");
        }
    }
}
