using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>
/// Original-mode acknowledgements: <c>MSA|AA|&lt;control id&gt;</c> when the LIS stored the results, <c>AE</c> when they
/// could not be processed (the analyser keeps and resends them) and <c>AR</c> for messages HC360 does not accept.
/// Sending/receiving application and facility are swapped from the original message, and its version is echoed.
/// </summary>
internal static class Hl7Ack
{
    public const string Accept = "AA";
    public const string Error = "AE";
    public const string Reject = "AR";

    public static string Build(Hl7Message? original, string code, string? text, string controlId, DateTimeOffset now)
    {
        var encoding = original?.Encoding ?? Hl7Encoding.Default;
        var header = original?.Header;
        var trigger = original?.TriggerEvent;
        var messageType = string.IsNullOrEmpty(trigger) ? "ACK" : string.Join(encoding.Component, "ACK", trigger, "ACK");

        var msh = string.Join(encoding.Field,
            "MSH",
            encoding.EncodingCharacters,
            header?.Field(5) is { Length: > 0 } receivingApp ? receivingApp : "HC360",
            header?.Field(6) ?? string.Empty,
            header?.Field(3) ?? string.Empty,
            header?.Field(4) ?? string.Empty,
            now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            string.Empty,
            messageType,
            encoding.Encode(controlId),
            header?.Field(11) is { Length: > 0 } processing ? processing : "P",
            header?.Field(12) is { Length: > 0 } version ? version : "2.5.1");

        var msa = string.Join(encoding.Field,
            "MSA",
            code,
            original is null ? string.Empty : encoding.Encode(original.ControlId),
            string.IsNullOrWhiteSpace(text) ? string.Empty : encoding.Encode(IntegrationTrace.Truncate(text, 80)!));

        return msh + "\r" + msa.TrimEnd(encoding.Field) + "\r";
    }

    /// <summary>A new message control id (MSH-10 allows 20 characters in v2.3.1).</summary>
    public static string NewControlId() => "HC" + Guid.CreateVersion7().ToString("N")[^18..].ToUpperInvariant();
}
