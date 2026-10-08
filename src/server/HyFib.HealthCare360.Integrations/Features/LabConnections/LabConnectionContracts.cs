using System.Net;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Hl7;

namespace HyFib.HealthCare360.Integrations.Features.LabConnections;

/// <summary>
/// Every analyser connection with its live state, and the enrolled lab agents. <c>ListVersion</c> is sent back with a
/// change, so two people editing connections at once never overwrite each other (409).
/// </summary>
public sealed record LabConnectionsDto(bool GatewayEnabled, uint ListVersion, IReadOnlyList<LabConnectionDto> Connections, IReadOnlyList<LabAgentDto> Agents);

/// <summary>
/// One analyser's connection. <c>Transport</c> is <c>Tcp</c>, <c>Serial</c> or <c>Simulator</c> (the training analyser);
/// <c>Protocol</c> follows Lab setup
/// (<c>Hl7v2</c> or <c>Astm</c>). <c>HostQueryRetiredOff</c> means the connection still carries the retired
/// <c>"hostQuery": false</c>, which keeps host queries and order download off until it is saved again. <c>Problem</c>
/// says why the gateway ignores it.
/// </summary>
public sealed record LabConnectionDto(
    string Code,
    string? Name,
    string Protocol,
    string Transport,
    bool Enabled,
    int? Port,
    IReadOnlyList<string> AllowedIps,
    string? Encoding,
    IReadOnlyList<string> SampleIdFields,
    string? OrderMessage,
    Guid? AgentDeviceId,
    string? AgentName,
    LabSerialPortDto? Serial,
    bool HostQueryRetiredOff,
    bool InLabSetup,
    bool? Bidirectional,
    string? Problem,
    LabConnectionStatusDto? Status,
    int PendingOrders);

/// <summary>A COM port's settings (names as Windows uses them: Parity None/Odd/Even/Mark/Space, StopBits One/OnePointFive/Two, Handshake None/XOnXOff/RequestToSend/RequestToSendXOnXOff).</summary>
public sealed record LabSerialPortDto(string PortName, int BaudRate, int DataBits, string Parity, string StopBits, string Handshake, bool DtrEnable, bool RtsEnable);

/// <summary>Live state on this server (see <see cref="LabListenerStatus"/> for the states).</summary>
public sealed record LabConnectionStatusDto(
    string State, int ActiveConnections, DateTimeOffset? ConnectedSince, DateTimeOffset? LastMessageAt, DateTimeOffset? LastErrorAt, string? LastError,
    long MessagesAccepted, long MessagesRefused);

/// <summary>An enrolled lab agent. Online: it called in the last three minutes, or is relaying an analyser now.</summary>
public sealed record LabAgentDto(Guid Id, string Name, string? MachineName, string? Location, DateTimeOffset? LastSeenAt, bool Online, string? AppVersion, string? LastIpAddress);

/// <summary>
/// Saves an analyser's connection. Network: <c>Port</c> (and the analyser's fixed IP in <c>AllowedIps</c>). Serial:
/// <c>AgentDeviceId</c> and <c>Serial</c>. HL7 analysers may set <c>SampleIdFields</c> and <c>OrderMessage</c>.
/// </summary>
public sealed record SaveLabConnectionRequest(
    string Transport,
    bool Enabled,
    int? Port,
    IReadOnlyList<string>? AllowedIps,
    string? Encoding,
    IReadOnlyList<string>? SampleIdFields,
    string? OrderMessage,
    Guid? AgentDeviceId,
    LabSerialPortDto? Serial,
    uint ListVersion);

public sealed class SaveLabConnectionRequestValidator : AbstractValidator<SaveLabConnectionRequest>
{
    public SaveLabConnectionRequestValidator()
    {
        RuleFor(r => r.Transport).Must(t => t is "Tcp" or "Serial" or "Simulator")
            .WithMessage("Choose network (Tcp), serial (Serial) or the training analyser (Simulator).");

        When(r => r.Transport == "Tcp", () =>
        {
            RuleFor(r => r.Port).NotNull().WithMessage("Enter the port the analyser connects to.")
                .InclusiveBetween(1, 65535).WithMessage("A port is a number from 1 to 65535.");
            RuleFor(r => r.AllowedIps).Must(ips => ips is null || ips.Count <= 16).WithMessage("List at most 16 addresses.");
            RuleForEach(r => r.AllowedIps).Must(ip => IPAddress.TryParse(ip?.Trim(), out _)).WithMessage("'{PropertyValue}' is not an IP address.");
        });

        When(r => r.Transport == "Serial", () =>
        {
            RuleFor(r => r.AgentDeviceId).Must(id => id is { } agent && agent != Guid.Empty).WithMessage("Choose the lab agent on the PC the analyser is cabled to.");
            RuleFor(r => r.Serial).NotNull().WithMessage("Enter the COM port settings.");
            RuleFor(r => r.Serial!.PortName).Must(p => p is not null && LabSerialSettings.PortNamePattern().IsMatch(p.Trim()))
                .WithMessage("Enter a COM port, for example COM3.").When(r => r.Serial is not null);
            RuleFor(r => r.Serial!.BaudRate).Must(b => LabSerialSettings.BaudRates.Contains(b))
                .WithMessage($"Choose a baud rate: {string.Join(", ", LabSerialSettings.BaudRates)}.").When(r => r.Serial is not null);
            RuleFor(r => r.Serial!.DataBits).InclusiveBetween(7, 8).WithMessage("Data bits are 7 or 8.").When(r => r.Serial is not null);
            RuleFor(r => r.Serial!.Parity).IsEnumName(typeof(LabParity)).WithMessage("Parity is None, Odd, Even, Mark or Space.").When(r => r.Serial is not null);
            RuleFor(r => r.Serial!.StopBits).IsEnumName(typeof(LabStopBits)).WithMessage("Stop bits are One, OnePointFive or Two.").When(r => r.Serial is not null);
            RuleFor(r => r.Serial!.Handshake).IsEnumName(typeof(LabHandshake))
                .WithMessage("Handshake is None, XOnXOff, RequestToSend or RequestToSendXOnXOff.").When(r => r.Serial is not null);
        });

        RuleFor(r => r.Encoding).Must(e => e is null or "latin1" or "utf-8").WithMessage("Encoding is latin1 or utf-8.");
        RuleFor(r => r.SampleIdFields).Must(f => f is null || f.Count <= 6).WithMessage("List at most 6 fields.");
        RuleForEach(r => r.SampleIdFields).Matches("^(SPM|SAC|OBR|PID)-[0-9]{1,2}$").WithMessage("A field is written like SPM-2, SAC-3, OBR-3 or OBR-2.");
        RuleFor(r => r.OrderMessage).Must(Hl7OrderMessages.IsKnown).WithMessage("The order message is OML^O21 or ORM^O01.");
    }
}
