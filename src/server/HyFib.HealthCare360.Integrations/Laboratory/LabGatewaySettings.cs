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

internal static class LabSettingKeys
{
    /// <summary>Gateway switch and limits (<see cref="LabGatewaySettings"/>).</summary>
    public const string Gateway = "integrations.lab";

    /// <summary>The analyser list (array of <see cref="LabInstrumentSettings"/>).</summary>
    public const string Instruments = "integrations.lab.instruments";
}

/// <summary>Laboratory equipment gateway (<c>integrations.lab</c>, global): TCP listeners for analysers (INT-LAB).</summary>
internal sealed record LabGatewaySettings
{
    public bool Enabled { get; init; }

    /// <summary>Interface to listen on; use the lab VLAN address to keep analyser ports off other networks.</summary>
    public string BindAddress { get; init; } = "0.0.0.0";

    /// <summary>Upper bound for one message; larger ones are refused (NAK / connection closed).</summary>
    public int MaxMessageKilobytes { get; init; } = 1024;

    /// <summary>Idle connections are closed after this long so dead analyser sockets cannot exhaust the connection slots.</summary>
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

/// <summary>One analyser connection point (an element of <c>integrations.lab.instruments</c>).</summary>
internal sealed partial record LabInstrumentSettings
{
    /// <summary>Instrument code the LIS maps channels for (passed to <see cref="IInstrumentResultSink"/>).</summary>
    public string Code { get; init; } = string.Empty;

    public string? Name { get; init; }

    public LabProtocol Protocol { get; init; }

    public int Port { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Analyser IP addresses allowed to connect (recommended: the analyser's fixed IP).</summary>
    public IReadOnlyList<string>? AllowedIps { get; init; }

    /// <summary><c>latin1</c> (default) or <c>utf-8</c>.</summary>
    public string? Encoding { get; init; }

    /// <summary>HL7 only: where the sample barcode is, in order of preference (default SPM-2, SAC-3, OBR-3, OBR-2).</summary>
    public IReadOnlyList<string>? SampleIdFields { get; init; }

    /// <summary>ASTM only: answer host queries (Q records) with the LIS orders (bi-directional mode).</summary>
    public bool HostQuery { get; init; } = true;

    public string? Validate() =>
        !CodePattern().IsMatch(Code) ? "instrument code must be 1–32 letters, digits, '-' or '_'"
        : Port is < 1 or > 65535 ? "port must be between 1 and 65535"
        : !Enum.IsDefined(Protocol) ? "protocol must be Hl7v2 or Astm"
        : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex CodePattern();
}

/// <summary>Resolved per-listener options (encoding, limits, time zone and ASTM timers).</summary>
internal sealed record LabRuntimeOptions(Encoding Encoding, int MaxMessageBytes, TimeSpan IdleTimeout, TimeZoneInfo Zone, AstmTimings Timings)
{
    public static LabRuntimeOptions From(LabGatewaySettings gateway, LabInstrumentSettings instrument) => new(
        LabGatewaySettings.ResolveEncoding(instrument.Encoding),
        Math.Clamp(gateway.MaxMessageKilobytes, 16, 16 * 1024) * 1024,
        TimeSpan.FromSeconds(Math.Clamp(gateway.IdleTimeoutSeconds, 30, 86_400)),
        LabTime.ResolveZone(gateway.TimeZone),
        AstmTimings.Standard);
}
