using System.Globalization;
using System.Net.WebSockets;
using HyFib.HealthCare360.Platform.Identity;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.Integrations.Laboratory.Relay;

/// <summary>
/// The HC360 lab agent's endpoints (INT-LAB serial analysers). The agent is a Windows service on the PC an analyser is
/// cabled to. It presents its device credential on every call (<see cref="LabAgentAuthenticator"/>).
/// <list type="bullet">
/// <item><c>POST …/agent/heartbeat</c> every minute: reports its COM ports and gets its analysers and their port settings.</item>
/// <item><c>GET …/agent/relay/{code}</c>: a WebSocket per analyser that relays the serial line's bytes both ways. The
/// analyser's protocol runs here, exactly as for a network analyser (ACK only after the LIS stored the results, host
/// queries, orders, logging and status); the agent never answers the analyser itself.</item>
/// </list>
/// Only an analyser assigned to the calling agent may be relayed, one relay at a time (the newest wins). The assignment
/// and the device are checked again every minute, so revoking the agent or moving the analyser ends the relay.
/// </summary>
internal static class LabAgentEndpoints
{
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions SignatureJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(30);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost(LabAgentProtocol.HeartbeatPath, Heartbeat)
            .WithTags("Laboratory agents")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Device)
            .WithValidation<LabAgentHeartbeatRequest>();

        // WebSockets are switched on for this endpoint only, as SignalR does for its hubs.
        var relay = app.CreateApplicationBuilder();
        relay.UseWebSockets();
        relay.Run(RelayAsync);
        app.MapGet(LabAgentProtocol.RelayPath, relay.Build())
            .WithTags("Laboratory agents")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Device);
    }

    /// <summary>The enabled, valid serial analysers assigned to an agent.</summary>
    internal static async Task<IReadOnlyList<LabInstrumentSettings>> AssignedAsync(IntegrationSettingsReader settings, Guid deviceId, CancellationToken cancellationToken) =>
        ((await settings.ReadValueAsync<List<LabInstrumentSettings>>(LabSettingKeys.Instruments, null, cancellationToken)) ?? [])
            .Where(i => i.Enabled && i.Transport == LabTransport.Serial && i.AgentDeviceId == deviceId && i.Validate() is null)
            .GroupBy(i => i.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    internal static LabAgentAssignment ToAssignment(LabInstrumentSettings instrument)
    {
        var serial = instrument.Serial!;
        return new LabAgentAssignment(instrument.Code, instrument.Name, instrument.Protocol.ToString(), new LabAgentSerialPort(
            serial.PortName.ToUpperInvariant(), serial.BaudRate, serial.DataBits, serial.Parity.ToString(), serial.StopBits.ToString(),
            serial.Handshake.ToString(), serial.DtrEnable, serial.RtsEnable));
    }

    private static async Task<IResult> Heartbeat(
        LabAgentHeartbeatRequest request, HttpContext http, LabAgentAuthenticator authenticator, HospitalDbContext db, IntegrationSettingsReader settings,
        LabGatewayState state, LabRelayRegistry relays, TimeProvider time, CancellationToken ct)
    {
        if (await authenticator.AuthenticateAsync(http, ct) is not { } device)
        {
            return NotEnrolled();
        }

        // Written straight to the row: a heartbeat is not a change anyone needs in the data audit trail.
        var now = time.GetUtcNow();
        var address = http.Connection.RemoteIpAddress?.ToString();
        var version = request.AppVersion?.Trim() is { Length: > 0 } reported ? reported : device.AppVersion;
        await db.Set<Device>().Where(d => d.Id == device.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now).SetProperty(d => d.LastIpAddress, address).SetProperty(d => d.AppVersion, version), ct);

        var gateway = await settings.ReadValueAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, ct) ?? new LabGatewaySettings();
        var assigned = gateway.Enabled ? await AssignedAsync(settings, device.Id, ct) : [];
        foreach (var port in request.Ports ?? [])
        {
            if (assigned.Any(i => string.Equals(i.Code, port.Code, StringComparison.OrdinalIgnoreCase)))
            {
                state.SerialPortReported(port.Code.ToUpperInvariant(), device.Name, port.Open, port.Error);
            }
        }

        relays.EndForAgent(device.Id, code => assigned.Any(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase)));
        return TypedResults.Ok(new LabAgentAssignments(device.Id, device.Name, gateway.Enabled, LabAgentProtocol.HeartbeatSeconds, assigned.Select(ToAssignment).ToList()));
    }

    private static async Task RelayAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var ct = http.RequestAborted;
        if (!http.WebSockets.IsWebSocketRequest)
        {
            await TypedResults.Problem(title: "Connect to the analyser relay with a WebSocket.", statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(http);
            return;
        }

        if (await services.GetRequiredService<LabAgentAuthenticator>().AuthenticateAsync(http, ct) is not { } device)
        {
            await NotEnrolled().ExecuteAsync(http);
            return;
        }

        var settings = services.GetRequiredService<IntegrationSettingsReader>();
        var gateway = await settings.ReadValueAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, ct) ?? new LabGatewaySettings();
        var code = http.GetRouteValue("code") as string ?? string.Empty;
        var instrument = gateway.Enabled
            ? (await AssignedAsync(settings, device.Id, ct)).FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase))
            : null;
        if (instrument is null)
        {
            var problem = gateway.Enabled
                ? TypedResults.Problem(title: "This analyser is not assigned to this lab agent.", statusCode: StatusCodes.Status404NotFound)
                : TypedResults.Problem(title: "The laboratory gateway is switched off.", statusCode: StatusCodes.Status503ServiceUnavailable);
            await problem.ExecuteAsync(http);
            return;
        }

        if (!http.WebSockets.WebSocketRequestedProtocols.Contains(LabAgentProtocol.Subprotocol))
        {
            await TypedResults.Problem(title: $"Ask for the {LabAgentProtocol.Subprotocol} sub-protocol.", statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(http);
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = LabAgentProtocol.Subprotocol,
            KeepAliveInterval = KeepAliveInterval,
            KeepAliveTimeout = KeepAliveTimeout,
        });
        await RunRelayAsync(socket, instrument, gateway, device, services, ct);
    }

    private static async Task RunRelayAsync(
        WebSocket socket, LabInstrumentSettings instrument, LabGatewaySettings gateway, Device device, IServiceProvider services, CancellationToken requestAborted)
    {
        var registry = services.GetRequiredService<LabRelayRegistry>();
        var processor = services.GetRequiredService<ILabMessageProcessor>();
        var time = services.GetRequiredService<TimeProvider>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HyFib.HealthCare360.Integrations.Laboratory.Relay");
        var stopping = services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        var trace = services.GetRequiredService<IntegrationTrace>();
        var provider = $"{(instrument.Protocol == LabProtocol.Hl7v2 ? "hl7" : "astm")}:{instrument.Code}";
        var serial = instrument.Serial!;
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{device.Name} · {serial.PortName.ToUpperInvariant()} {serial.BaudRate} {serial.DataBits}{serial.Parity.ToString()[0]}{(serial.StopBits == LabStopBits.Two ? 2 : 1)}");

        using var lease = registry.Open(instrument, device.Id, device.Name);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, lease.Ended, stopping);

        // Not tied to the session: ending the session must not abort the socket before the close frame is sent.
        await using var stream = new WebSocketRelayStream(socket);
        await trace.LogAsync(IntegrationNames.Lab, provider, "relay.connected", true, trace.Start(), instrument.Code, line, direction: IntegrationDirection.Inbound);
        logger.LogInformation("Lab agent {Agent} is relaying instrument {Instrument}", device.Name, instrument.Code);

        var started = trace.Start();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var watch = WatchAsync(scopes, device.Id, instrument, lease, session, time, logger);
        string? failed = null;
        try
        {
            await LabSessionRunner.RunAsync(instrument, LabRuntimeOptions.From(gateway, instrument), stream, processor, time, logger, session.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or WebSocketException or ObjectDisposedException)
        {
            // The relay was ended (see below), or the agent went away.
        }
        catch (Exception ex)
        {
            // A bug in the session code must end only this relay, never the server.
            logger.LogError(ex, "Unexpected error relaying instrument {Instrument}", instrument.Code);
            failed = "The server ended the relay after an error.";
        }

        await session.CancelAsync();
        await watch;

        var (status, reason) = CloseFor(
            stream.ProtocolError, failed, lease.EndReason, stopping.IsCancellationRequested);
        await stream.CloseAsync(status, reason);
        var (received, sent) = stream.Traffic;
        var error = stream.ProtocolError ?? failed ?? lease.EndReason;
        await trace.LogAsync(IntegrationNames.Lab, provider, "relay.closed", error is null, started, instrument.Code, line,
            string.Create(CultureInfo.InvariantCulture, $"{received} bytes from the analyser, {sent} bytes to it"), error, IntegrationDirection.Inbound);
        logger.LogInformation(
            "Lab agent {Agent} stopped relaying instrument {Instrument}: {Reason}",
            device.Name,
            instrument.Code,
            reason);
    }

    /// <summary>
    /// The close frame that tells the agent why its relay ended (the agent logs it, then reconnects; a relay it may no
    /// longer run is refused then). Descriptions stay within the 123 bytes a close frame carries.
    /// </summary>
    internal static (WebSocketCloseStatus Status, string Reason) CloseFor(
        string? protocolError, string? failure, string? ended, bool stopping) =>
        protocolError is not null ? (WebSocketCloseStatus.PolicyViolation, protocolError)
        : failure is not null ? (WebSocketCloseStatus.InternalServerError, failure)
        : ended == RelayLease.Revoked ? (WebSocketCloseStatus.PolicyViolation, ended)
        : ended is not null ? (WebSocketCloseStatus.NormalClosure, ended)
        : stopping ? (WebSocketCloseStatus.EndpointUnavailable, "The HC360 server is stopping.")
        : (WebSocketCloseStatus.NormalClosure, "HC360 closed the relay.");

    /// <summary>Every minute: the agent must still be enrolled and the analyser still assigned to it, unchanged.</summary>
    private static async Task WatchAsync(
        IServiceScopeFactory scopes,
        Guid deviceId,
        LabInstrumentSettings instrument,
        RelayLease lease,
        CancellationTokenSource session,
        TimeProvider time,
        ILogger logger)
    {
        var signature = JsonSerializer.Serialize(instrument, SignatureJson);
        while (!session.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RecheckInterval, time, session.Token);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var settings = scope.ServiceProvider.GetRequiredService<IntegrationSettingsReader>();
                var enrolled = await db.Set<Device>().AnyAsync(d => d.Id == deviceId && !d.IsRevoked && d.Kind == DeviceKind.LabAgent, session.Token);
                var gateway = await settings.ReadValueAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, session.Token);
                var current = gateway?.Enabled == true
                    ? (await AssignedAsync(settings, deviceId, session.Token)).FirstOrDefault(i => string.Equals(i.Code, instrument.Code, StringComparison.OrdinalIgnoreCase))
                    : null;
                var ended = !enrolled ? RelayLease.Revoked
                    : gateway?.Enabled != true ? RelayLease.GatewayOff
                    : current is null ? RelayLease.Unassigned
                    : JsonSerializer.Serialize(current, SignatureJson) != signature ? RelayLease.Changed
                    : null;
                if (ended is not null)
                {
                    logger.LogInformation("Relay of instrument {Instrument} ended: {Reason}", instrument.Code, ended);
                    lease.End(ended);
                }
            }
            catch (OperationCanceledException)
            {
                // The relay ended.
            }
            catch (Exception ex)
            {
                // The database is unavailable: keep relaying; results are refused (and resent by the analyser) until it is back.
                logger.LogWarning(ex, "Could not re-check the relay of instrument {Instrument}", instrument.Code);
            }
        }
    }

    private static ProblemHttpResult NotEnrolled() =>
        TypedResults.Problem(
            title: "This lab agent is not enrolled, or it was revoked.",
            detail: "Enrol it again with a new code from Administration › Devices.",
            statusCode: StatusCodes.Status401Unauthorized);
}
