namespace HyFib.HealthCare360.LabAgent;

/// <summary>
/// The server endpoints the agent calls and how it identifies itself. They mirror the server's
/// <c>LabAgentProtocol</c> (Integrations › Laboratory › Relay); a test checks the two agree. Paths are relative to the
/// server address the agent was enrolled with.
/// </summary>
internal static class AgentProtocol
{
    public const string CaCertificatePath = "api/v1/system/ca-certificate";
    public const string SystemInfoPath = "api/v1/system/info";
    public const string EnrolPath = "api/v1/devices/enroll";
    public const string HeartbeatPath = "api/v1/integrations/lab/agent/heartbeat";
    public const string RelayPath = "api/v1/integrations/lab/agent/relay/{code}";

    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceKeyHeader = "X-Device-Key";

    /// <summary>The relay's WebSocket sub-protocol: binary messages carry the serial line's bytes, nothing else.</summary>
    public const string Subprotocol = "hc360.lab-relay.v1";

    public static string RelayPathFor(string code) => RelayPath.Replace("{code}", Uri.EscapeDataString(code), StringComparison.Ordinal);
}

/// <summary>What the server says about itself (the agent compares the CA fingerprint it announces).</summary>
internal sealed record SystemInfo(string Product, string Version, string ServerName, string? FacilityName, string? FacilityCode, string? CaFingerprint);

internal sealed record EnrolRequest(string Code, string Name, string? MachineName, string? OsVersion, string? AppVersion);

internal sealed record EnrolResult(Guid DeviceId, string DeviceSecret, string ServerName, string? CaFingerprint);

/// <summary>
/// Every minute: the agent's version, for each COM port it was asked to open whether it is open, and the hospital CAs it
/// trusts the server through, the one it pins first.
/// </summary>
internal sealed record HeartbeatRequest(string? AppVersion, IReadOnlyList<PortReport> Ports, IReadOnlyList<string> CaFingerprints);

internal sealed record PortReport(string Code, bool Open, string? Error);

/// <summary>The server's answer: the analysers this agent relays, or none while the gateway is off.</summary>
internal sealed record Assignments(Guid DeviceId, string DeviceName, bool GatewayEnabled, int HeartbeatSeconds, IReadOnlyList<Assignment> Instruments);

internal sealed record Assignment(string Code, string? Name, string Protocol, SerialSettings Port);

/// <summary>A COM port's settings, named as System.IO.Ports names them.</summary>
internal sealed record SerialSettings(string PortName, int BaudRate, int DataBits, string Parity, string StopBits, string Handshake, bool DtrEnable, bool RtsEnable);
