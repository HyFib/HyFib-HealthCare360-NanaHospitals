using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Integrations.Laboratory.Relay;

/// <summary>
/// The wire contract between the server and the HC360 lab agent (src/agent): where it calls, how it identifies itself
/// and the WebSocket sub-protocol of the relay. The agent keeps its own copy; a test checks the two agree.
/// </summary>
public static class LabAgentProtocol
{
    public const string HeartbeatPath = "/api/v1/integrations/lab/agent/heartbeat";

    public const string RelayPath = "/api/v1/integrations/lab/agent/relay/{code}";

    /// <summary>The device id the agent was given at enrolment.</summary>
    public const string DeviceIdHeader = "X-Device-Id";

    /// <summary>The device secret the agent was given at enrolment (also what the device rate limit counts by).</summary>
    public const string DeviceKeyHeader = "X-Device-Key";

    /// <summary>The relay's WebSocket sub-protocol: binary messages carry the serial line's bytes, nothing else.</summary>
    public const string Subprotocol = "hc360.lab-relay.v1";

    /// <summary>How often the agent calls in.</summary>
    public const int HeartbeatSeconds = 60;

    public static string RelayPathFor(string code) => RelayPath.Replace("{code}", Uri.EscapeDataString(code), StringComparison.Ordinal);
}

/// <summary>
/// What a lab agent reports every minute: its version, for each COM port it was asked to open whether it is open (or why
/// not), and the hospital CAs it trusts the server through, the one it pins first. Port errors are short texts from
/// Windows ("Access to the port is denied"), never analyser data.
/// </summary>
public sealed record LabAgentHeartbeatRequest(string? AppVersion, IReadOnlyList<LabAgentPortReport>? Ports, IReadOnlyList<string>? CaFingerprints = null);

public sealed record LabAgentPortReport(string Code, bool Open, string? Error);

/// <summary>The serial analysers this agent relays, with their port settings (names as System.IO.Ports uses them).</summary>
public sealed record LabAgentAssignments(Guid DeviceId, string DeviceName, bool GatewayEnabled, int HeartbeatSeconds, IReadOnlyList<LabAgentAssignment> Instruments);

public sealed record LabAgentAssignment(string Code, string? Name, string Protocol, LabAgentSerialPort Port);

public sealed record LabAgentSerialPort(string PortName, int BaudRate, int DataBits, string Parity, string StopBits, string Handshake, bool DtrEnable, bool RtsEnable);

public sealed class LabAgentHeartbeatRequestValidator : AbstractValidator<LabAgentHeartbeatRequest>
{
    public LabAgentHeartbeatRequestValidator()
    {
        RuleFor(r => r.AppVersion).SafeText(32);
        RuleFor(r => r.Ports).Must(ports => ports is null || ports.Count <= 64).WithMessage("Report at most 64 ports.");
        RuleFor(r => r.CaFingerprints).Must(DeviceCaFingerprints.AreValid).WithMessage(DeviceCaFingerprints.Rule);
        RuleForEach(r => r.Ports).ChildRules(port =>
        {
            port.RuleFor(p => p.Code).NotEmpty().Matches("^[A-Za-z0-9_-]{1,32}$").WithMessage("An analyser code is 1–32 letters, digits, - or _.");
            port.RuleFor(p => p.Error).SafeText(300);
        });
    }
}
