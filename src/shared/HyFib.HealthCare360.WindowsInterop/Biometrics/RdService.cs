using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace HyFib.HealthCare360.WindowsInterop.Biometrics;

/// <summary>Whether an RD service can capture now, as it answers <see cref="RdService.Discover"/>.</summary>
public enum RdServiceStatus
{
    /// <summary>The service answered with a status this version of HC360 does not know.</summary>
    Unknown,

    /// <summary><c>READY</c>: a device is connected and can capture.</summary>
    Ready,

    /// <summary><c>NOTREADY</c>: the service runs, but no device is connected or it is not initialised.</summary>
    NotReady,

    /// <summary><c>USED</c>: another application is using the device.</summary>
    Used,
}

/// <summary>What the fingerprint is captured as (<c>fType</c>), and the biometric type UIDAI is told (<c>bt</c>).</summary>
public enum FingerDataType
{
    /// <summary><c>fType="0"</c>, <c>bt="FMR"</c>: the finger's minutiae (ISO 19794-2).</summary>
    Minutiae = 0,

    /// <summary><c>fType="1"</c>, <c>bt="FIR"</c>: the finger's image (ISO 19794-4).</summary>
    Image = 1,

    /// <summary><c>fType="2"</c>, <c>bt="FMR,FIR"</c>: both, which UIDAI asks of L1 registered devices.</summary>
    MinutiaeAndImage = 2,
}

/// <summary>The UIDAI environment a capture is encrypted for (<c>env</c>): it must match the AUA's.</summary>
public enum RdEnvironment
{
    /// <summary><c>P</c>: production.</summary>
    Production,

    /// <summary><c>PP</c>: pre-production, for testing with an AUA's test licence.</summary>
    PreProduction,

    /// <summary><c>S</c>: staging.</summary>
    Staging,
}

/// <summary>An RD service found on this computer (<see cref="RdService.Discover"/>).</summary>
/// <param name="Port">The local port it listens on, between <see cref="RdService.FirstPort"/> and <see cref="RdService.LastPort"/>.</param>
/// <param name="Info">The service's own description (<c>info</c>), such as the vendor's name.</param>
/// <param name="InfoPath">The path its DEVICEINFO interface answers on, or null when it names none that can be used.</param>
/// <param name="CapturePath">The path its CAPTURE interface answers on, or null when it names none that can be used.</param>
public sealed record RdServiceEndpoint(int Port, RdServiceStatus Status, string Info, string? InfoPath, string? CapturePath)
{
    /// <summary>The service can be asked for a capture now.</summary>
    public bool CanCapture => Status == RdServiceStatus.Ready && CapturePath is not null;
}

/// <summary>
/// A registered device as its RD service describes it (<c>DeviceInfo</c>): the attributes UIDAI's Auth API takes in its
/// <c>Meta</c> element, and the serial number for the person looking after the device.
/// </summary>
/// <param name="ProviderId"><c>dpId</c>: the device provider's id.</param>
/// <param name="ServiceId"><c>rdsId</c>: the RD service's id.</param>
/// <param name="ServiceVersion"><c>rdsVer</c>: the RD service's version.</param>
/// <param name="ModelId"><c>mi</c>: the device model.</param>
/// <param name="DeviceCode"><c>dc</c>: the device's unique code.</param>
/// <param name="Certificate"><c>mc</c>: the device's public key certificate (base64), signed by its provider.</param>
/// <param name="SerialNumber">The serial number the provider reports in <c>additional_info</c> (<c>srno</c>), if any.</param>
public sealed record RdDevice(
    string ProviderId,
    string ServiceId,
    string ServiceVersion,
    string ModelId,
    string DeviceCode,
    string Certificate,
    string? SerialNumber);

/// <summary>
/// The capture asked of the RD service (<c>PidOptions</c>, RD specification 2.0). Fingerprints only: no iris, face or
/// photo, and no <c>wadh</c>, since HC360 authenticates and never asks UIDAI for e-KYC data.
/// </summary>
public sealed record FingerprintCaptureOptions
{
    public const int MaxFingers = 2;

    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How many fingers to capture (<c>fCount</c>), 1 or 2.</summary>
    public int FingerCount { get; init; } = 1;

    public FingerDataType DataType { get; init; } = FingerDataType.MinutiaeAndImage;

    /// <summary>How long the device waits for a finger (<c>timeout</c>).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public RdEnvironment Environment { get; init; } = RdEnvironment.Production;

    /// <summary>Checks the options are ones HC360 asks for.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value outside what HC360 allows.</exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(FingerCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FingerCount, MaxFingers);
        if (!Enum.IsDefined(DataType))
        {
            throw new ArgumentOutOfRangeException(nameof(DataType), DataType, "Not a fingerprint data type.");
        }

        if (Timeout < MinTimeout || Timeout > MaxTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, $"Between {MinTimeout.TotalSeconds} and {MaxTimeout.TotalSeconds} seconds.");
        }

        if (!Enum.IsDefined(Environment))
        {
            throw new ArgumentOutOfRangeException(nameof(Environment), Environment, "Not a UIDAI environment.");
        }
    }

    /// <summary>The options as the RD service takes them.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value outside what HC360 allows.</exception>
    public string ToXml()
    {
        Validate();
        var options = new XElement(
            "PidOptions",
            new XAttribute("ver", "1.0"),
            new XElement(
                "Opts",
                new XAttribute("fCount", FingerCount.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("fType", ((int)DataType).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("iCount", "0"),
                new XAttribute("pCount", "0"),
                new XAttribute("format", "0"),
                new XAttribute("pidVer", "2.0"),
                new XAttribute("timeout", ((int)Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)),
                new XAttribute("posh", "UNKNOWN"),
                new XAttribute("env", RdService.EnvironmentCode(Environment))));
        return "<?xml version=\"1.0\"?>" + options.ToString(SaveOptions.DisableFormatting);
    }
}

/// <summary>
/// What an RD service answered to a capture (<c>PidData</c>). On success it carries the fingerprint as the device
/// encrypted it for UIDAI alone (<c>Skey</c>, <c>Hmac</c>, <c>Data</c>): nobody else can read it, HC360 included, so it is
/// only ever passed on, never kept or logged.
/// </summary>
public sealed class PidCapture
{
    private PidCapture(string xml)
    {
        Xml = xml;
    }

    /// <summary>The <c>PidData</c> document as the device returned it.</summary>
    public string Xml { get; }

    /// <summary><c>errCode</c>: 0 when the capture succeeded.</summary>
    public int ErrorCode { get; private init; }

    /// <summary><c>errInfo</c>: the device's own words for the outcome.</summary>
    public string ErrorInfo { get; private init; } = string.Empty;

    public int FingerCount { get; private init; }

    public FingerDataType? DataType { get; private init; }

    /// <summary><c>qScore</c>: the capture's quality, 0 to 100 (the lowest finger's when there are several).</summary>
    public int? Quality { get; private init; }

    public RdDevice? Device { get; private init; }

    /// <summary><c>Skey/@ci</c>: which UIDAI public key encrypted the session key (its expiry, <c>yyyyMMdd</c>).</summary>
    public string? CertificateIdentifier { get; private init; }

    /// <summary><c>Skey</c>: the session key, encrypted with UIDAI's public key (base64).</summary>
    public string? SessionKey { get; private init; }

    /// <summary><c>Hmac</c>: the PID block's SHA-256 hash, encrypted with the session key (base64).</summary>
    public string? Hmac { get; private init; }

    /// <summary><c>Data</c>: the PID block, encrypted with the session key (base64).</summary>
    public string? Data { get; private init; }

    /// <summary><c>Data/@type</c>: <c>X</c> (XML) or <c>P</c> (protobuf).</summary>
    public string? DataFormat { get; private init; }

    /// <summary>The device captured, and the capture holds everything UIDAI needs.</summary>
    public bool Succeeded =>
        ErrorCode == 0
        && Device is not null
        && !string.IsNullOrEmpty(CertificateIdentifier)
        && !string.IsNullOrEmpty(SessionKey)
        && !string.IsNullOrEmpty(Hmac)
        && !string.IsNullOrEmpty(Data)
        && DataFormat is "X" or "P";

    /// <summary>A training capture (<see cref="RdService.TrainingCapture"/>): no finger and no device, never sent to UIDAI.</summary>
    public bool IsTraining => Device?.ProviderId == RdService.TrainingProviderId;

    /// <summary>A training capture made to stand for a finger that matches (rather than one that does not).</summary>
    public bool IsMatchingTrainingCapture => IsTraining && SessionKey == RdService.TrainingMatch;

    /// <summary>Reads a <c>PidData</c> document.</summary>
    /// <exception cref="FormatException">Not a <c>PidData</c> document.</exception>
    public static PidCapture Parse(string? xml)
    {
        var root = RdService.Load(xml, "PidData");
        var response = RdService.Child(root, "Resp") ?? throw new FormatException("The fingerprint reader's answer has no result (Resp).");
        var errorCode = RdService.Attribute(response, "errCode");
        if (!int.TryParse(errorCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            throw new FormatException("The fingerprint reader's answer has no result code (errCode).");
        }

        var key = RdService.Child(root, "Skey");
        var data = RdService.Child(root, "Data");
        var deviceInfo = RdService.Child(root, "DeviceInfo");
        return new PidCapture(xml!)
        {
            ErrorCode = code,
            ErrorInfo = RdService.Attribute(response, "errInfo") ?? string.Empty,
            FingerCount = int.TryParse(RdService.Attribute(response, "fCount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0,
            DataType = int.TryParse(RdService.Attribute(response, "fType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type) && Enum.IsDefined((FingerDataType)type)
                ? (FingerDataType)type
                : null,
            Quality = RdService.LowestScore(RdService.Attribute(response, "qScore")),
            Device = deviceInfo is null ? null : RdService.DeviceOf(deviceInfo),
            CertificateIdentifier = key is null ? null : RdService.Attribute(key, "ci"),
            SessionKey = RdService.Text(key),
            Hmac = RdService.Text(RdService.Child(root, "Hmac")),
            Data = RdService.Text(data),
            DataFormat = data is null ? null : RdService.Attribute(data, "type"),
        };
    }
}

/// <summary>
/// The UIDAI Registered Devices (RD) service protocol, version 2.0 (SRS INT-BIO). On Windows a certified fingerprint
/// reader's RD service is a local HTTP server on <c>127.0.0.1</c>, on a port between <see cref="FirstPort"/> and
/// <see cref="LastPort"/>, that answers three HTTP methods of its own: <see cref="Discover"/> (on <c>/</c>), and
/// <see cref="Info"/> and <see cref="Capture"/> on the paths its discovery answer names (vendors differ: <c>/rd/info</c>,
/// <c>/getDeviceInfo</c>…). The device encrypts each capture for UIDAI before it leaves the reader, so a fingerprint
/// can only be checked by Aadhaar authentication, through an AUA that UIDAI licensed.
/// </summary>
public static class RdService
{
    public const int FirstPort = 11100;

    public const int LastPort = 11120;

    /// <summary>A capture of two fingers as minutiae and images stays well under this.</summary>
    public const int MaxDocumentChars = 2 * 1024 * 1024;

    public static readonly HttpMethod Discover = new("RDSERVICE");

    public static readonly HttpMethod Info = new("DEVICEINFO");

    public static readonly HttpMethod Capture = new("CAPTURE");

    /// <summary>The device provider id (<c>dpId</c>) of a training capture. No registered device has it.</summary>
    public const string TrainingProviderId = "HC360.TRAINING";

    internal const string TrainingMatch = "TRAINING-MATCH";

    /// <summary>
    /// A capture for training with the simulator where no fingerprint reader is connected, standing for a finger that
    /// matches or one that does not. It holds no fingerprint, and only the simulator accepts it. The staff app makes the
    /// same document (<c>trainingCapture</c> in packages/core/src/bridge/fingerprint.ts).
    /// </summary>
    public static string TrainingCapture(bool matches) =>
        "<PidData><Resp errCode=\"0\" errInfo=\"Training capture\" fCount=\"1\" fType=\"2\" nmPoints=\"0\" qScore=\"80\" />"
        + $"<DeviceInfo dpId=\"{TrainingProviderId}\" rdsId=\"HC360.TRAINING.RD\" rdsVer=\"1.0\" mi=\"TRAINING\" mc=\"TRAINING\" dc=\"TRAINING\" />"
        + $"<Skey ci=\"20991231\">{(matches ? TrainingMatch : "TRAINING-NO-MATCH")}</Skey><Hmac>TRAINING</Hmac><Data type=\"X\">TRAINING</Data></PidData>";

    /// <summary>The UIDAI biometric type (<c>bt</c>) of what a capture holds.</summary>
    public static string BiometricType(FingerDataType type) => type switch
    {
        FingerDataType.Minutiae => "FMR",
        FingerDataType.Image => "FIR",
        FingerDataType.MinutiaeAndImage => "FMR,FIR",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a fingerprint data type."),
    };

    public static string EnvironmentCode(RdEnvironment environment) => environment switch
    {
        RdEnvironment.Production => "P",
        RdEnvironment.PreProduction => "PP",
        RdEnvironment.Staging => "S",
        _ => throw new ArgumentOutOfRangeException(nameof(environment), environment, "Not a UIDAI environment."),
    };

    /// <summary>The UIDAI environment a code names (<c>P</c>, <c>PP</c> or <c>S</c>, as <c>integrations.aadhaar</c> sets it).</summary>
    /// <exception cref="FormatException">Not a UIDAI environment code.</exception>
    public static RdEnvironment ParseEnvironment(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "P" => RdEnvironment.Production,
        "PP" => RdEnvironment.PreProduction,
        "S" => RdEnvironment.Staging,
        _ => throw new FormatException($"'{code}' is not a UIDAI environment: use P, PP or S."),
    };

    /// <summary>Reads an RD service's answer to <see cref="Discover"/>.</summary>
    /// <exception cref="FormatException">Not an <c>RDService</c> document.</exception>
    public static RdServiceEndpoint ParseEndpoint(int port, string? xml)
    {
        var root = Load(xml, "RDService");
        var status = Attribute(root, "status")?.Trim().ToUpperInvariant() switch
        {
            "READY" => RdServiceStatus.Ready,
            "NOTREADY" => RdServiceStatus.NotReady,
            "USED" => RdServiceStatus.Used,
            _ => RdServiceStatus.Unknown,
        };
        string? PathOf(string id) => root.Elements()
            .Where(e => e.Name.LocalName == "Interface" && string.Equals(Attribute(e, "id"), id, StringComparison.OrdinalIgnoreCase))
            .Select(e => Attribute(e, "path"))
            .FirstOrDefault(IsLocalPath);
        return new RdServiceEndpoint(port, status, Attribute(root, "info")?.Trim() ?? string.Empty, PathOf("DEVICEINFO"), PathOf("CAPTURE"));
    }

    /// <summary>Reads an RD service's answer to <see cref="Info"/>.</summary>
    /// <exception cref="FormatException">Not a <c>DeviceInfo</c> document.</exception>
    public static RdDevice ParseDevice(string? xml) => DeviceOf(Load(xml, "DeviceInfo"));

    /// <summary>
    /// A path an RD service may name for an interface: absolute on the same local service, without a host, a query, a
    /// parent folder or anything but plain path characters, so an answer can never send HC360 anywhere else.
    /// </summary>
    public static bool IsLocalPath(string? path) =>
        path is { Length: > 0 and <= 128 }
        && path[0] == '/'
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains("..", StringComparison.Ordinal)
        && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.');

    internal static RdDevice DeviceOf(XElement element)
    {
        var serial = element.Elements().FirstOrDefault(e => e.Name.LocalName == "additional_info")?
            .Elements()
            .Where(e => e.Name.LocalName == "Param" && string.Equals(Attribute(e, "name"), "srno", StringComparison.OrdinalIgnoreCase))
            .Select(e => Attribute(e, "value"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return new RdDevice(
            Attribute(element, "dpId") ?? string.Empty,
            Attribute(element, "rdsId") ?? string.Empty,
            Attribute(element, "rdsVer") ?? string.Empty,
            Attribute(element, "mi") ?? string.Empty,
            Attribute(element, "dc") ?? string.Empty,
            Attribute(element, "mc") ?? string.Empty,
            serial?.Trim());
    }

    /// <summary>
    /// Parses an RD service document without DTDs or external entities, and checks its root. A byte order mark or
    /// blank space before the document, which some services send, is allowed.
    /// </summary>
    internal static XElement Load(string? xml, string root)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            throw new FormatException("The fingerprint reader sent an empty answer.");
        }

        if (xml.Length > MaxDocumentChars)
        {
            throw new FormatException("The fingerprint reader's answer is too large.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxDocumentChars,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        XElement element;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿', ' ', '\t', '\r', '\n')), settings);
            element = XElement.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("The fingerprint reader's answer is not XML.", ex);
        }

        if (element.Name.LocalName != root)
        {
            throw new FormatException($"The fingerprint reader answered with {element.Name.LocalName} instead of {root}.");
        }

        return element;
    }

    internal static XElement? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>An attribute by name, whatever its case: vendors are not all exact.</summary>
    internal static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    internal static string? Text(XElement? element) =>
        element is null ? null : string.Concat(element.Value.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>The lowest of a score list such as <c>"74"</c> or <c>"74,61"</c> (one per finger), or null.</summary>
    internal static int? LowestScore(string? scores)
    {
        if (string.IsNullOrWhiteSpace(scores))
        {
            return null;
        }

        int? lowest = null;
        foreach (var part in scores.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score) && score is >= 0 and <= 100)
            {
                lowest = lowest is null ? score : Math.Min(lowest.Value, score);
            }
        }

        return lowest;
    }

    /// <summary>
    /// Staff-facing words for a capture that failed, from its <c>errCode</c> (UIDAI's RD error codes) and the device's
    /// own <c>errInfo</c>, which is added whenever it says something.
    /// </summary>
    public static string Problem(int errorCode, string? errorInfo)
    {
        var said = string.IsNullOrWhiteSpace(errorInfo) ? null : errorInfo.Trim();
        var text = errorCode switch
        {
            700 => "The fingerprint reader waited for a finger and timed out. Place the finger on the reader and try again.",
            710 => "Another application is using the fingerprint reader. Close it and try again.",
            720 => "The fingerprint reader is not ready. Plug it in again, or restart its RD service.",
            730 => "The fingerprint reader could not capture the finger. Clean the reader and the finger, and try again.",
            >= 100 and < 200 => "HC360 asked the fingerprint reader for a capture it cannot make. Tell your administrator.",
            _ => "The fingerprint reader could not capture the finger.",
        };
        var details = new StringBuilder(text);
        if (said is not null)
        {
            details.Append(CultureInfo.InvariantCulture, $" The reader says: {(said.Length <= 200 ? said : said[..200])} (code {errorCode}).");
        }
        else
        {
            details.Append(CultureInfo.InvariantCulture, $" (Code {errorCode}.)");
        }

        return details.ToString();
    }
}
