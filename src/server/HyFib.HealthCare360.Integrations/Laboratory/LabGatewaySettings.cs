using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;

namespace HyFib.HealthCare360.Integrations.Laboratory;

[JsonConverter(typeof(JsonStringEnumConverter<LabProtocol>))]
internal enum LabProtocol
{
    Hl7v2 = 0,
    Astm,
}

/// <summary>How an analyser reaches the gateway.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LabTransport>))]
internal enum LabTransport
{
    /// <summary>The analyser connects to a listening port on the server over the lab network.</summary>
    Tcp = 0,

    /// <summary>The analyser is cabled to a lab PC whose HC360 lab agent relays the serial line to the server.</summary>
    Serial,
}

internal static class LabSettingKeys
{
    /// <summary>Gateway switch and limits (<see cref="LabGatewaySettings"/>).</summary>
    public const string Gateway = "integrations.lab";

    /// <summary>The analyser connections (array of <see cref="LabInstrumentSettings"/>).</summary>
    public const string Instruments = "integrations.lab.instruments";
}

/// <summary>Laboratory equipment gateway (<c>integrations.lab</c>, global): analyser connections (INT-LAB).</summary>
internal sealed record LabGatewaySettings
{
    public bool Enabled { get; init; }

    /// <summary>Interface to listen on; use the lab VLAN address to keep analyser ports off other networks.</summary>
    public string BindAddress { get; init; } = "0.0.0.0";

    /// <summary>Upper bound for one message; larger ones are refused (NAK / connection closed).</summary>
    public int MaxMessageKilobytes { get; init; } = 1024;

    /// <summary>Idle network connections are closed after this long so dead analyser sockets cannot exhaust the slots.</summary>
    public int IdleTimeoutSeconds { get; init; } = 300;

    public int MaxConnectionsPerInstrument { get; init; } = 4;

    /// <summary>Zone of analyser timestamps without an offset.</summary>
    public string TimeZone { get; init; } = "Asia/Kolkata";

    public static Encoding ResolveEncoding(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "utf-8" or "utf8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        // Latin-1 maps every byte to one character, so framing and checksums never break on unexpected bytes.
        _ => Encoding.Latin1,
    };
}

/// <summary>
/// One analyser connection (an element of <c>integrations.lab.instruments</c>). Lab setup › Analysers edits it through
/// <c>/api/v1/integrations/lab/connections</c>; the protocol always follows the analyser's entry in Lab setup.
/// </summary>
internal sealed partial record LabInstrumentSettings
{
    /// <summary>Instrument code the LIS maps channels for (passed to <see cref="IInstrumentResultSink"/>).</summary>
    public string Code { get; init; } = string.Empty;

    public string? Name { get; init; }

    public LabProtocol Protocol { get; init; }

    public bool Enabled { get; init; } = true;

    public LabTransport Transport { get; init; }

    /// <summary>Network: the port the analyser connects to.</summary>
    public int Port { get; init; }

    /// <summary>Network: analyser IP addresses allowed to connect (recommended: the analyser's fixed IP).</summary>
    public IReadOnlyList<string>? AllowedIps { get; init; }

    /// <summary>Serial: the enrolled lab agent (device id) on the PC the analyser is cabled to.</summary>
    public Guid? AgentDeviceId { get; init; }

    /// <summary>Serial: the COM port and line settings the agent opens.</summary>
    public LabSerialSettings? Serial { get; init; }

    /// <summary><c>latin1</c> (default) or <c>utf-8</c>.</summary>
    public string? Encoding { get; init; }

    /// <summary>HL7 only: where the sample barcode is, in order of preference (default SPM-2, SAC-3, OBR-3, OBR-2).</summary>
    public IReadOnlyList<string>? SampleIdFields { get; init; }

    /// <summary>HL7 only: the message orders are sent in, <c>OML^O21</c> (HL7 2.5.1, default) or <c>ORM^O01</c> (2.3.1).</summary>
    public string? OrderMessage { get; init; }

    /// <summary>
    /// Retired. Host queries and order download follow Bidirectional in Lab setup. A stored <c>false</c> (set before
    /// that) still keeps both off for this analyser, and saving the connection in Lab setup clears it.
    /// </summary>
    public bool? HostQuery { get; init; }

    public string? Validate()
    {
        if (!CodePattern().IsMatch(Code))
        {
            return "instrument code must be 1–32 letters, digits, '-' or '_'";
        }

        if (!Enum.IsDefined(Protocol))
        {
            return "protocol must be Hl7v2 or Astm";
        }

        return Transport switch
        {
            LabTransport.Tcp => Port is < 1 or > 65535 ? "port must be between 1 and 65535" : null,
            LabTransport.Serial when AgentDeviceId is null || AgentDeviceId == Guid.Empty => "a serial analyser needs its lab agent",
            LabTransport.Serial => Serial is null ? "a serial analyser needs its port settings" : Serial.Validate(),
            _ => "transport must be Tcp or Serial",
        };
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex CodePattern();
}

/// <summary>
/// The serial line the lab agent opens (names match <c>System.IO.Ports</c>). Most analysers use 9600 baud, 8 data bits,
/// no parity, one stop bit and no handshake; the analyser's interface manual gives the values.
/// </summary>
internal sealed partial record LabSerialSettings
{
    public static IReadOnlyList<int> BaudRates { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    public string PortName { get; init; } = "COM1";

    public int BaudRate { get; init; } = 9600;

    public int DataBits { get; init; } = 8;

    public LabParity Parity { get; init; }

    public LabStopBits StopBits { get; init; } = LabStopBits.One;

    public LabHandshake Handshake { get; init; }

    /// <summary>Data terminal ready: on tells the analyser the host is there (some analysers wait for it).</summary>
    public bool DtrEnable { get; init; } = true;

    /// <summary>Request to send, when the handshake does not drive it.</summary>
    public bool RtsEnable { get; init; } = true;

    public string? Validate() =>
        !PortNamePattern().IsMatch(PortName) ? "the port must be a COM port, for example COM3"
        : !BaudRates.Contains(BaudRate) ? $"the baud rate must be one of {string.Join(", ", BaudRates)}"
        : DataBits is < 7 or > 8 ? "data bits must be 7 or 8"
        : !Enum.IsDefined(Parity) || !Enum.IsDefined(StopBits) || !Enum.IsDefined(Handshake) ? "parity, stop bits or handshake is not valid"
        : null;

    /// <summary>COM1 to COM256, as Windows names them.</summary>
    [GeneratedRegex("^COM([1-9]|[1-9][0-9]|1[0-9][0-9]|2[0-4][0-9]|25[0-6])$", RegexOptions.IgnoreCase)]
    internal static partial Regex PortNamePattern();
}

[JsonConverter(typeof(JsonStringEnumConverter<LabParity>))]
internal enum LabParity
{
    None = 0,
    Odd,
    Even,
    Mark,
    Space,
}

[JsonConverter(typeof(JsonStringEnumConverter<LabStopBits>))]
internal enum LabStopBits
{
    One = 1,
    Two,
    OnePointFive,
}

[JsonConverter(typeof(JsonStringEnumConverter<LabHandshake>))]
internal enum LabHandshake
{
    None = 0,
    XOnXOff,
    RequestToSend,
    RequestToSendXOnXOff,
}

/// <summary>Resolved per-session options (encoding, limits, time zone, ASTM timers and how often orders are looked for).</summary>
internal sealed record LabRuntimeOptions(Encoding Encoding, int MaxMessageBytes, TimeSpan IdleTimeout, TimeZoneInfo Zone, AstmTimings Timings)
{
    /// <summary>While the line is idle, how often the session looks for orders to send unasked.</summary>
    public TimeSpan OrderPollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>HL7: how long to wait for the analyser's acknowledgement of an order.</summary>
    public TimeSpan Hl7ReplyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static LabRuntimeOptions From(LabGatewaySettings gateway, LabInstrumentSettings instrument) => new(
        LabGatewaySettings.ResolveEncoding(instrument.Encoding),
        Math.Clamp(gateway.MaxMessageKilobytes, 16, 16 * 1024) * 1024,
        // A relayed serial line is always there: the relay's own keep-alives find a dead agent, so it never idles out.
        instrument.Transport == LabTransport.Serial ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(Math.Clamp(gateway.IdleTimeoutSeconds, 30, 86_400)),
        LabTime.ResolveZone(gateway.TimeZone),
        AstmTimings.Standard);
}
