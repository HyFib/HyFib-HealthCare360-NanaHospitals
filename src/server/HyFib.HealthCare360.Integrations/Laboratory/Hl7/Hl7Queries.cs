namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>
/// HL7 host queries from analysers.
/// <list type="bullet">
/// <item><c>QBP^Q11</c> (HL7 2.5, IHE LAW "work order step" query): the sample id is QPD-3 (else QPD-4). The answer is
/// <c>RSP^K11</c>: MSA, QAK (the query tag; OK with orders, NF without, AR when host queries are off, AE when the LIS
/// cannot answer), the QPD echoed, then the patient and one order group per test.</item>
/// <item><c>QRY^Q02</c> (HL7 2.3.1 deferred query): the sample id is QRD-8 (who subject filter). The answer is
/// <c>QCK^Q02</c> (MSA and QAK with QRD-4 as the query tag); the orders found then follow on the connection as the
/// analyser's order message.</item>
/// </list>
/// </summary>
internal static class Hl7Queries
{
    public static bool IsQbp(Hl7Message message) => message.MessageType == "QBP" && message.TriggerEvent == "Q11";

    public static bool IsQry(Hl7Message message) => message.MessageType == "QRY" && message.TriggerEvent == "Q02";

    /// <summary>Acknowledgements and application responses: never acknowledged in turn.</summary>
    public static bool IsAcknowledgement(Hl7Message message) => message.MessageType is "ACK" or "ORL" or "ORR" or "OSR" or "QCK";

    public static Hl7Segment? Segment(Hl7Message message, string id) => message.Segments.FirstOrDefault(s => s.Id == id);

    public static string? QbpSampleId(Hl7Message message) =>
        Segment(message, "QPD") is { } qpd ? FirstNonEmpty(qpd.Component(3, 1), qpd.Component(4, 1)) : null;

    public static string QbpTag(Hl7Message message) => Segment(message, "QPD")?.Value(2) ?? string.Empty;

    public static string? QrySampleId(Hl7Message message) =>
        Segment(message, "QRD") is { } qrd ? FirstNonEmpty(qrd.Component(8, 1)) : null;

    public static string QryTag(Hl7Message message) => Segment(message, "QRD")?.Value(4) ?? string.Empty;

    /// <summary>RSP^K11 for a QBP^Q11. <paramref name="orders"/> are listed only with QAK status OK.</summary>
    public static string BuildRsp(
        Hl7Message query, string msaCode, string qakStatus, string? text, IReadOnlyList<InstrumentOrder> orders, string controlId, DateTimeOffset localNow, TimeZoneInfo zone)
    {
        var e = query.Encoding;
        var qpd = Segment(query, "QPD");
        var segments = new List<string>
        {
            Hl7Ack.Header(query, string.Join(e.Component, "RSP", "K11", "RSP_K11"), controlId, localNow),
            Hl7Ack.Msa(query, msaCode, text),
            string.Join(e.Field, "QAK", e.Encode(QbpTag(query)), qakStatus, qpd?.Field(1) ?? string.Empty).TrimEnd(e.Field),
        };

        if (qpd is not null)
        {
            // Echoed exactly as received (already in the query's own encoding).
            segments.Add(qpd.Text);
        }

        if (qakStatus == "OK")
        {
            foreach (var order in orders.Where(o => o.ChannelCodes.Count > 0))
            {
                segments.AddRange(Hl7OrderMessages.Segments(order, InstrumentOrderAction.New, v251: true, zone, e));
            }
        }

        return string.Join('\r', segments) + "\r";
    }

    /// <summary>QCK^Q02 for a QRY^Q02: the query acknowledged, QAK OK when orders follow, NF when there are none.</summary>
    public static string BuildQck(Hl7Message query, string msaCode, string qakStatus, string? text, string controlId, DateTimeOffset localNow)
    {
        var e = query.Encoding;
        return string.Join('\r',
            Hl7Ack.Header(query, string.Join(e.Component, "QCK", "Q02"), controlId, localNow),
            Hl7Ack.Msa(query, msaCode, text),
            string.Join(e.Field, "QAK", e.Encode(QryTag(query)), qakStatus).TrimEnd(e.Field)) + "\r";
    }

    private static string? FirstNonEmpty(params string[] values) =>
        values.Select(v => v.Trim()).FirstOrDefault(v => v.Length > 0);
}
