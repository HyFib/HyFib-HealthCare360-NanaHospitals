using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>
/// Laboratory orders for an analyser in HL7 v2: <c>OML^O21</c> (2.5.1, the default) or <c>ORM^O01</c> (2.3.1, for older
/// analysers). The patient (PID) is followed by one order per test: ORC (NW new, CA cancel; ORC-2 the sample barcode),
/// for 2.5.1 a TQ1 with the priority, then OBR (OBR-2 the barcode, OBR-4 the analyser's channel code, OBR-7 when the
/// sample was collected) and for 2.5.1 the specimen (SPM-2 the barcode). Every value is escaped.
/// </summary>
internal static class Hl7OrderMessages
{
    public const string Oml = "OML^O21";
    public const string Orm = "ORM^O01";

    private const string CodingSystem = "99HC360";

    public static bool IsKnown(string? messageType) => messageType is null or Oml or Orm;

    /// <summary>The order message for an analyser: its configured one, else ORM^O01 for HL7 2.3 analysers, else OML^O21.</summary>
    public static string MessageFor(LabInstrumentSettings instrument, string? analyserVersion = null) =>
        instrument.OrderMessage is Oml or Orm ? instrument.OrderMessage
        : analyserVersion?.StartsWith("2.3", StringComparison.Ordinal) == true ? Orm
        : Oml;

    public static string Build(
        InstrumentOrder order, InstrumentOrderAction action, string messageType, string instrumentCode, string controlId, DateTimeOffset localNow, TimeZoneInfo zone)
    {
        var e = Hl7Encoding.Default;
        var v251 = messageType != Orm;
        var msh = string.Join(e.Field,
            "MSH",
            e.EncodingCharacters,
            "HC360",
            "HC360",
            e.Encode(instrumentCode),
            string.Empty,
            localNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            string.Empty,
            v251 ? "OML^O21^OML_O21" : "ORM^O01",
            e.Encode(controlId),
            "P",
            v251 ? "2.5.1" : "2.3.1");
        return string.Join('\r', [msh, .. Segments(order, action, v251, zone, e)]) + "\r";
    }

    /// <summary>PID and one order group per channel code (also the body of an RSP^K11 query answer).</summary>
    public static IEnumerable<string> Segments(InstrumentOrder order, InstrumentOrderAction action, bool v251, TimeZoneInfo zone, Hl7Encoding e)
    {
        var uhid = order.PatientId is { Length: > 0 } id ? e.Encode(id) + (v251 ? $"{e.Component}{e.Component}{e.Component}HC360{e.Component}MR" : string.Empty) : string.Empty;
        yield return Join(e, "PID", "1", string.Empty, uhid, string.Empty, e.Encode(order.PatientName),
            string.Empty, order.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty, Sex(order.Gender));

        var control = action == InstrumentOrderAction.Cancel ? "CA" : "NW";
        var barcode = e.Encode(order.SampleBarcode);
        var priority = Priority(order.Priority);
        var collected = order.CollectedAt is { } at ? TimeZoneInfo.ConvertTime(at, zone).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : string.Empty;
        var specimen = order.SpecimenType is { Length: > 0 } type ? e.Encode(type) : string.Empty;
        var sequence = 0;
        foreach (var channel in order.ChannelCodes)
        {
            sequence++;
            var test = string.Join(e.Component, e.Encode(channel), e.Encode(channel), CodingSystem);
            yield return Join(e, "ORC", control, barcode);
            if (v251)
            {
                yield return Join(e, "TQ1", "1", string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, priority);
                yield return Join(e, "OBR", sequence.ToString(CultureInfo.InvariantCulture), barcode, string.Empty, test, string.Empty, string.Empty, collected);
                yield return Join(e, "SPM", "1", barcode, string.Empty, specimen);
            }
            else
            {
                // 2.3.1: OBR-5 priority, OBR-7 collected at, OBR-15 specimen source.
                yield return Join(e, "OBR", sequence.ToString(CultureInfo.InvariantCulture), barcode, string.Empty, test, priority, string.Empty, collected,
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, specimen);
            }
        }
    }

    internal static string Priority(OrderPriority priority) => priority switch
    {
        OrderPriority.Stat => "S",
        OrderPriority.Urgent => "A",
        _ => "R",
    };

    private static string Sex(string? gender) => gender?.Trim().ToUpperInvariant() switch
    {
        "M" or "MALE" => "M",
        "F" or "FEMALE" => "F",
        _ => "U",
    };

    private static string Join(Hl7Encoding e, params string[] fields) => string.Join(e.Field, fields).TrimEnd(e.Field);
}
