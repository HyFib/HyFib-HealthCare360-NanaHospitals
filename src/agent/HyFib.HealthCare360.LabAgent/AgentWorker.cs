using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.WindowsInterop.Trust;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.LabAgent;

/// <summary>Where the agent keeps its configuration (the service default, or <c>--config</c>).</summary>
internal sealed record AgentPaths(string ConfigurationFile);

/// <summary>Which relays to start and stop so the running ones match the server's assignments.</summary>
internal static class RelayPlan
{
    private static readonly JsonSerializerOptions SignatureJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Relays whose analyser is no longer assigned, or whose port settings changed, are stopped; new and changed ones are
    /// started. Unchanged relays keep running untouched, so a heartbeat never interrupts a working line.
    /// </summary>
    public static (IReadOnlyList<Assignment> Start, IReadOnlyList<string> Stop) Diff(IReadOnlyDictionary<string, Assignment> running, IReadOnlyList<Assignment> assigned)
    {
        var wanted = assigned.GroupBy(a => a.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var stop = running.Where(r => !wanted.TryGetValue(r.Key, out var want) || Signature(want) != Signature(r.Value)).Select(r => r.Key).ToList();
        var start = wanted.Values.Where(w => !running.ContainsKey(w.Code) || stop.Contains(w.Code, StringComparer.OrdinalIgnoreCase)).ToList();
        return (start, stop);
    }

    private static string Signature(Assignment assignment) => JsonSerializer.Serialize(assignment, SignatureJson);
}

/// <summary>
/// The service's main loop. Every minute (as the server says) it sends a heartbeat with the state of each COM port and
/// gets back its analysers, then starts, restarts or stops relays to match. While the server cannot be reached the
/// relays keep trying on their own. When the server no longer knows the agent (revoked, or enrolled again elsewhere), every
/// relay stops until the agent is enrolled again. A changed configuration file (a new enrolment) is picked up without a
/// restart.
/// </summary>
internal sealed class AgentWorker(AgentPaths paths, ISerialLineFactory lines, TimeProvider time, ILoggerFactory loggers) : BackgroundService
{
    private static readonly TimeSpan NotEnrolledWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RevokedWait = TimeSpan.FromMinutes(5);

    /// <summary>How often the agent looks for a new hospital CA to follow after a rollover.</summary>
    private static readonly TimeSpan FollowCaEvery = TimeSpan.FromHours(6);

    private readonly ILogger _logger = loggers.CreateLogger<AgentWorker>();
    private readonly Dictionary<string, InstrumentRelay> _relays = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var stamp = Stamp();
                var configuration = AgentConfiguration.Load(paths.ConfigurationFile);
                var secret = configuration.DeviceSecret;
                if (!configuration.IsEnrolled || secret is null)
                {
                    _logger.LogError(
                        "This lab agent is not enrolled (or its configuration cannot be read on this PC). Run Install-HC360LabAgent.ps1 with an enrolment code from Administration › Devices.");
                    await StopAllAsync();
                    await Task.Delay(NotEnrolledWait, time, stoppingToken);
                    continue;
                }

                using var trust = new ServerTrust(configuration.CaCertificatePem!, configuration.PreviousCaCertificatePem);
                using var api = new ServerApi(configuration.ServerUri!, trust);
                var connector = new RelayConnector(configuration.ServerUri!, trust, configuration.DeviceId!.Value, secret);
                await RunEnrolledAsync(configuration, secret, trust, api, connector, stamp, stoppingToken);
                await StopAllAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The service is stopping.
        }
        finally
        {
            await StopAllAsync();
        }
    }

    /// <summary>
    /// Heartbeats until the configuration file changes or the server no longer knows the agent. Every six hours, after a
    /// heartbeat that reached the server, it also looks for a new hospital CA to follow (<see cref="FollowCaAsync"/>).
    /// </summary>
    private async Task RunEnrolledAsync(
        AgentConfiguration configuration, string secret, ServerTrust trust, ServerApi api, IRelayConnector connector, DateTime stamp,
        CancellationToken stoppingToken)
    {
        var deviceId = configuration.DeviceId!.Value;
        var unreachable = new Backoff(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));
        var reportedOutage = false;
        var nextCaCheck = time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested && Stamp() == stamp)
        {
            var report = new HeartbeatRequest(ServerApi.AppVersion, _relays.Values.Select(r => r.Report).ToList(), trust.Pins.Fingerprints(time.GetUtcNow()));
            HeartbeatOutcome outcome;
            try
            {
                outcome = await api.HeartbeatAsync(deviceId, secret, report, stoppingToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !stoppingToken.IsCancellationRequested)
            {
                if (!reportedOutage)
                {
                    _logger.LogWarning("The HC360 server cannot be reached ({Reason}); the analysers keep reconnecting on their own", ex.Message);
                    reportedOutage = true;
                }

                await Task.Delay(unreachable.Next(), time, stoppingToken);
                continue;
            }

            unreachable.Reset();
            if (reportedOutage)
            {
                _logger.LogInformation("The HC360 server can be reached again");
                reportedOutage = false;
            }

            if (outcome.NotEnrolled || outcome.Assignments is null)
            {
                _logger.LogError("The HC360 server no longer knows this lab agent (revoked, or enrolled again). Enrol it again with a new code from Administration › Devices.");
                await StopAllAsync();
                await Task.Delay(RevokedWait, time, stoppingToken);
                return;
            }

            if (time.GetUtcNow() >= nextCaCheck)
            {
                nextCaCheck = time.GetUtcNow() + FollowCaEvery;
                stamp = await FollowCaAsync(configuration, trust, api, stamp, stoppingToken);
            }

            var assignments = outcome.Assignments;
            await ApplyAsync(assignments.GatewayEnabled ? assignments.Instruments : [], connector);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(assignments.HeartbeatSeconds, 15, 300)), time, stoppingToken);
        }
    }

    /// <summary>
    /// After the hospital CA was rolled over ('certificates roll-ca'), takes the new CA on the word of the CA the agent pins and
    /// saves it, keeping the one it replaced as a second pin until that expires (CA-ROLLOVER.md). The relays keep running:
    /// the check they use follows in place, and the configuration file's new time is the agent's own write, not a new
    /// enrolment. Returns the file time to watch from now on.
    /// </summary>
    private async Task<DateTime> FollowCaAsync(AgentConfiguration configuration, ServerTrust trust, ServerApi api, DateTime stamp, CancellationToken stoppingToken)
    {
        HospitalCaSuccessor? successor;
        try
        {
            successor = await api.FindCaSuccessorAsync(time.GetUtcNow(), stoppingToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or CryptographicException
            || (ex is TaskCanceledException && !stoppingToken.IsCancellationRequested))
        {
            _logger.LogDebug(ex, "Could not ask the HC360 server for its CA; trying again later");
            return stamp;
        }

        // A new enrolment written meanwhile is not overwritten: the loop picks it up.
        if (successor is null || Stamp() != stamp)
        {
            return stamp;
        }

        var before = (configuration.CaCertificatePem, configuration.CaFingerprint, configuration.PreviousCaCertificatePem);
        try
        {
            configuration.Follow(successor);
            configuration.Save(paths.ConfigurationFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (configuration.CaCertificatePem, configuration.CaFingerprint, configuration.PreviousCaCertificatePem) = before;
            _logger.LogError(ex, "The hospital's new CA could not be saved; this lab agent keeps the CA it pins and tries again later");
            return stamp;
        }

        trust.Pins.Follow(successor);
        _logger.LogInformation(
            "The hospital CA was rolled over: this lab agent now pins the new CA {Fingerprint}, and the one it replaced until that expires",
            successor.Fingerprint);
        return Stamp();
    }

    private async Task ApplyAsync(IReadOnlyList<Assignment> assigned, IRelayConnector connector)
    {
        var (start, stop) = RelayPlan.Diff(_relays.ToDictionary(r => r.Key, r => r.Value.Assignment, StringComparer.OrdinalIgnoreCase), assigned);
        foreach (var code in stop)
        {
            _logger.LogInformation("Stopping the relay of analyser {Instrument}", code);
            await _relays[code].DisposeAsync();
            _relays.Remove(code);
        }

        foreach (var assignment in start)
        {
            _logger.LogInformation("Starting the relay of analyser {Instrument} on {Port}", assignment.Code, assignment.Port.PortName);
            var relay = new InstrumentRelay(assignment, lines, connector, time, loggers.CreateLogger<InstrumentRelay>());
            _relays[assignment.Code] = relay;
            relay.Start();
        }
    }

    private async Task StopAllAsync()
    {
        foreach (var relay in _relays.Values)
        {
            await relay.DisposeAsync();
        }

        _relays.Clear();
    }

    /// <summary>When the configuration file was last written, so a new enrolment is noticed.</summary>
    private DateTime Stamp() => File.Exists(paths.ConfigurationFile) ? File.GetLastWriteTimeUtc(paths.ConfigurationFile) : DateTime.MinValue;
}
