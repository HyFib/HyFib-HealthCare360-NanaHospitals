using System.Text.Json.Serialization;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using HyFib.HealthCare360.Platform.Identity;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Integrations.Features.LabConnections;

/// <summary>
/// Reads and edits the analyser connections (<c>integrations.lab.instruments</c>) for Lab setup › Analysers.
/// <list type="bullet">
/// <item>Each change is checked against Lab setup (the analyser must be there; its protocol is taken from there), the
/// enrolled lab agents and the other connections (one analyser per network port, one per COM port of an agent).</item>
/// <item>Changes are made against the list version the person saw, so two edits at once give 409 instead of one
/// overwriting the other. Each is audited.</item>
/// <item>Saving drops the retired <c>hostQuery</c> flag: Bidirectional in Lab setup decides from then on.</item>
/// <item>Staff see and edit only their facility's analysers and lab agents, as in Lab setup (an account with no
/// facility sees every facility's), and an analyser can be relayed only by an agent enrolled at its own facility.
/// Connections whose analyser is no longer in Lab setup belong to no facility: everyone sees them, to remove
/// them.</item>
/// </list>
/// The gateway applies a change within a minute.
/// </summary>
internal sealed class LabConnectionService(
    HospitalDbContext db,
    ISettingsService settings,
    IAuditLogger audit,
    LabGatewayState state,
    LabRelayRegistry relays,
    IOptions<PlatformOptions> platform,
    IServiceProvider services,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<LabConnectionService> logger)
{
    /// <summary>An agent calls in every minute; three missed calls and it shows as offline.</summary>
    internal static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(3);

    private static readonly JsonSerializerOptions StoredJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<LabConnectionsDto> ListAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: false, cancellationToken);
        var stored = Parse(row?.Value);
        var gateway = await GatewayAsync(cancellationToken);
        var sink = services.GetService<IInstrumentResultSink>();
        var statuses = state.Snapshot().ToDictionary(s => s.InstrumentCode, StringComparer.OrdinalIgnoreCase);
        var connected = relays.ConnectedAgents();
        var now = time.GetUtcNow();
        var facility = currentUser.FacilityId;
        var agents = (await db.Set<Device>().AsNoTracking()
                .Where(d => d.Kind == DeviceKind.LabAgent && !d.IsRevoked)
                .Where(d => facility == null || d.FacilityId == facility)
                .OrderBy(d => d.Name)
                .ToListAsync(cancellationToken))
            .Select(d => new LabAgentDto(d.Id, d.Name, d.MachineName, d.Location, d.LastSeenAt,
                connected.Contains(d.Id) || (d.LastSeenAt is { } seen && now - seen <= OnlineWindow), d.AppVersion, d.LastIpAddress))
            .ToList();
        var pending = await db.Set<LabOrderDownload>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(d => d.Status == LabOrderDownloadStatus.Pending)
            .GroupBy(d => d.InstrumentCode)
            .Select(g => new { Code = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Code, g => g.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var connections = new List<LabConnectionDto>();
        foreach (var instrument in stored.Instruments.OrderBy(i => i.Code, StringComparer.OrdinalIgnoreCase))
        {
            var profile = sink is null ? null : await sink.GetInstrumentAsync(instrument.Code, cancellationToken);
            if (profile is not null && !InScope(profile.FacilityId))
            {
                continue;
            }

            var agent = instrument.AgentDeviceId is { } agentId ? agents.FirstOrDefault(a => a.Id == agentId) : null;
            var tcp = instrument.Transport == LabTransport.Tcp;
            connections.Add(new LabConnectionDto(
                instrument.Code.ToUpperInvariant(),
                profile?.Name ?? instrument.Name,
                instrument.Protocol.ToString(),
                instrument.Transport.ToString(),
                instrument.Enabled,
                tcp ? instrument.Port : null,
                instrument.AllowedIps ?? [],
                instrument.Encoding,
                instrument.SampleIdFields ?? [],
                instrument.OrderMessage,
                instrument.AgentDeviceId,
                agent?.Name ?? (instrument.AgentDeviceId is null ? null : "A revoked or removed agent"),
                instrument.Serial is { } serial ? ToDto(serial) : null,
                instrument.HostQuery == false,
                profile is not null,
                profile?.IsBidirectional,
                instrument.Validate(),
                statuses.TryGetValue(instrument.Code, out var status)
                    ? new LabConnectionStatusDto(status.State, status.ActiveConnections, status.ConnectedSince, status.LastMessageAt, status.LastErrorAt,
                        status.LastError, status.MessagesAccepted, status.MessagesRefused)
                    : null,
                pending.GetValueOrDefault(instrument.Code.ToUpperInvariant())));
        }

        return new LabConnectionsDto(gateway.Enabled, row?.Version ?? 0, connections, agents);
    }

    public async Task<Result> SaveAsync(string code, SaveLabConnectionRequest request, CancellationToken cancellationToken)
    {
        var sink = services.GetService<IInstrumentResultSink>();
        var profile = sink is null ? null : await sink.GetInstrumentAsync(code, cancellationToken);
        if (profile is null || !InScope(profile.FacilityId))
        {
            return Error.NotFound("Analyser", code);
        }

        var row = await RowAsync(tracked: true, cancellationToken);
        if (CheckVersion(row, request.ListVersion) is { } conflict)
        {
            return conflict;
        }

        var stored = Parse(row?.Value);
        if (stored.Invalid)
        {
            return InvalidList();
        }

        var others = stored.Instruments.Where(i => !string.Equals(i.Code, profile.Code, StringComparison.OrdinalIgnoreCase)).ToList();
        var transport = request.Transport switch
        {
            "Serial" => LabTransport.Serial,
            "Simulator" => LabTransport.Simulator,
            _ => LabTransport.Tcp,
        };
        var hl7 = profile.Protocol == "Hl7";
        string? agentName = null;
        if (transport == LabTransport.Tcp)
        {
            var port = request.Port ?? 0;

            // The API reference's port even while the reference is off, so that turning it on stops no analyser.
            var use = Listeners.ServerPortUse(port, platform.Value, apiReferenceOn: true)
                      ?? (port == 5353 ? "finding the server on the network (mDNS)" : null);
            if (use is not null)
            {
                return Error.Field("port", $"Port {port} is used by the HC360 server itself, for {use}. Choose another, for example 5100.");
            }

            if (others.FirstOrDefault(i => i.Transport == LabTransport.Tcp && i.Port == port) is { } taken)
            {
                return Error.Field("port", $"Port {port} is already used by analyser {taken.Code}.");
            }
        }
        else if (transport == LabTransport.Serial)
        {
            var agent = await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == request.AgentDeviceId, cancellationToken);
            if (agent is null || agent.IsRevoked || agent.Kind != DeviceKind.LabAgent || !InScope(agent.FacilityId))
            {
                return Error.Field("agentDeviceId", "Choose an enrolled lab agent. Enrol one in Administration › Devices.");
            }

            if (agent.FacilityId != profile.FacilityId)
            {
                return Error.Field("agentDeviceId",
                    $"{agent.Name} is enrolled at another facility. Choose a lab agent at the analyser's facility.");
            }

            agentName = agent.Name;
            var portName = request.Serial!.PortName.Trim().ToUpperInvariant();
            if (others.FirstOrDefault(i => i.Transport == LabTransport.Serial && i.AgentDeviceId == agent.Id
                                           && string.Equals(i.Serial?.PortName, portName, StringComparison.OrdinalIgnoreCase)) is { } taken)
            {
                return Error.Field("serial.portName", $"{portName} on {agent.Name} is already used by analyser {taken.Code}.");
            }
        }

        var entry = new LabInstrumentSettings
        {
            Code = profile.Code,
            Name = profile.Name,
            Protocol = hl7 ? LabProtocol.Hl7v2 : LabProtocol.Astm,
            Enabled = request.Enabled,
            Transport = transport,
            Port = transport == LabTransport.Tcp ? request.Port ?? 0 : 0,
            AllowedIps = transport == LabTransport.Tcp ? Clean(request.AllowedIps) : null,
            AgentDeviceId = transport == LabTransport.Serial ? request.AgentDeviceId : null,
            Serial = transport == LabTransport.Serial ? FromDto(request.Serial!) : null,
            Encoding = request.Encoding,
            SampleIdFields = hl7 ? Clean(request.SampleIdFields) : null,
            OrderMessage = hl7 ? request.OrderMessage : null,
        };
        if (entry.Validate() is { } problem)
        {
            return Error.Rule("lab.connection_invalid", $"The connection is not valid: {problem}.");
        }

        audit.Add(new AuditEntry(AuditCategories.Admin, "lab.connection.saved", transport switch
        {
            LabTransport.Tcp => $"Set analyser {profile.Code} to connect over the network on port {entry.Port}",
            LabTransport.Serial => $"Set analyser {profile.Code} to connect through lab agent {agentName} on {entry.Serial!.PortName}",
            _ => $"Set analyser {profile.Code} to be the training analyser (results entered on screen)",
        })
        {
            EntityType = "LabConnection",
            EntityId = profile.Code,
            Details = new { transport = transport.ToString(), entry.Enabled, entry.Port, entry.AllowedIps, entry.AgentDeviceId, comPort = entry.Serial?.PortName },
            Severity = AuditSeverity.Warning,
        });
        await StoreAsync([.. others, entry], cancellationToken);
        logger.LogInformation("Analyser {Instrument} connection saved ({Transport})", profile.Code, transport);
        return Result.Success();
    }

    public async Task<Result> RemoveAsync(string code, uint listVersion, CancellationToken cancellationToken)
    {
        var row = await RowAsync(tracked: true, cancellationToken);
        if (CheckVersion(row, listVersion) is { } conflict)
        {
            return conflict;
        }

        var stored = Parse(row?.Value);
        if (stored.Invalid)
        {
            return InvalidList();
        }

        var remaining = stored.Instruments.Where(i => !string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
        var sink = services.GetService<IInstrumentResultSink>();
        var profile = sink is null ? null : await sink.GetInstrumentAsync(code, cancellationToken);
        if (remaining.Count == stored.Instruments.Count || (profile is not null && !InScope(profile.FacilityId)))
        {
            return Error.NotFound("Analyser connection", code);
        }

        audit.Add(new AuditEntry(AuditCategories.Admin, "lab.connection.removed", $"Removed the connection of analyser {code.ToUpperInvariant()}")
        {
            EntityType = "LabConnection",
            EntityId = code.ToUpperInvariant(),
            Severity = AuditSeverity.Warning,
        });
        await StoreAsync(remaining, cancellationToken);
        return Result.Success();
    }

    internal static LabSerialPortDto ToDto(LabSerialSettings serial) => new(
        serial.PortName.ToUpperInvariant(), serial.BaudRate, serial.DataBits, serial.Parity.ToString(), serial.StopBits.ToString(), serial.Handshake.ToString(),
        serial.DtrEnable, serial.RtsEnable);

    internal static LabSerialSettings FromDto(LabSerialPortDto dto) => new()
    {
        PortName = dto.PortName.Trim().ToUpperInvariant(),
        BaudRate = dto.BaudRate,
        DataBits = dto.DataBits,
        Parity = Enum.Parse<LabParity>(dto.Parity),
        StopBits = Enum.Parse<LabStopBits>(dto.StopBits),
        Handshake = Enum.Parse<LabHandshake>(dto.Handshake),
        DtrEnable = dto.DtrEnable,
        RtsEnable = dto.RtsEnable,
    };

    /// <summary>The stored list as the gateway reads it; <c>Invalid</c> when the JSON cannot be read at all.</summary>
    internal static (IReadOnlyList<LabInstrumentSettings> Instruments, bool Invalid) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null")
        {
            return ([], false);
        }

        try
        {
            return (JsonSerializer.Deserialize<List<LabInstrumentSettings>>(json, StoredJson) ?? [], false);
        }
        catch (JsonException)
        {
            return ([], true);
        }
    }

    private static List<string>? Clean(IReadOnlyList<string>? values)
    {
        var cleaned = (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return cleaned.Count == 0 ? null : cleaned;
    }

    /// <summary>Whether <paramref name="facilityId"/> is the caller's facility (all are, if they have none).</summary>
    private bool InScope(Guid? facilityId) => currentUser.FacilityId is not { } mine || facilityId == mine;

    private static Error InvalidList() => Error.Rule("lab.connections_invalid",
        "The stored analyser connections cannot be read. Correct integrations.lab.instruments in Administration › Settings first.");

    /// <summary>Null when the list is the version the person saw; the row (when there is one) then checks it again on save.</summary>
    private Error? CheckVersion(SystemSetting? row, uint expected)
    {
        if (row is null)
        {
            return expected == 0 ? null : Error.ConcurrencyConflict;
        }

        if (row.Version != expected)
        {
            return Error.ConcurrencyConflict;
        }

        db.SetExpectedVersion(row, expected);
        return null;
    }

    private Task<SystemSetting?> RowAsync(bool tracked, CancellationToken cancellationToken)
    {
        var rows = tracked ? db.Set<SystemSetting>() : db.Set<SystemSetting>().AsNoTracking();
        return rows.FirstOrDefaultAsync(s => s.Key == LabSettingKeys.Instruments && s.FacilityId == null, cancellationToken);
    }

    private async Task<LabGatewaySettings> GatewayAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetAsync<LabGatewaySettings>(LabSettingKeys.Gateway, null, cancellationToken) ?? new LabGatewaySettings();
        }
        catch (JsonException)
        {
            return new LabGatewaySettings();
        }
    }

    /// <summary>Saved through the settings service (the cache is cleared at once) with the audit entry, in one save.</summary>
    private Task StoreAsync(IEnumerable<LabInstrumentSettings> instruments, CancellationToken cancellationToken) =>
        settings.SetAsync(LabSettingKeys.Instruments, JsonSerializer.SerializeToNode(instruments.OrderBy(i => i.Code, StringComparer.OrdinalIgnoreCase).ToList(), StoredJson),
            null, secret: false, cancellationToken);
}
