using System.Globalization;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;

namespace HyFib.HealthCare360.Integrations.Laboratory.Astm;

/// <summary>
/// E1394 message content: result records (O → R) to <see cref="InstrumentResultMessage"/>s, host queries (Q) to
/// sample ids, and LIS orders back to H/P/O/L records for the analyser.
/// </summary>
internal static class AstmMessageMapper
{
    public static IReadOnlyList<InstrumentResultMessage> MapResults(
        IReadOnlyList<AstmRecord> records, string instrumentCode, string rawMessage, DateTimeOffset receivedAt, TimeZoneInfo zone)
    {
        var header = records.FirstOrDefault(r => r.Type == 'H');
        var messageTime = LabTime.Parse(header?.Field(14), zone);
        var samples = new List<(string SampleId, List<InstrumentResult> Results)>();
        List<InstrumentResult>? current = null;
        foreach (var record in records)
        {
            switch (record.Type)
            {
                case 'P':
                    // A new patient: results must be preceded by their own order record.
                    current = null;
                    break;
                case 'O':
                    var sampleId = SampleId(record);
                    current = string.IsNullOrWhiteSpace(sampleId) ? null : [];
                    if (current is not null)
                    {
                        samples.Add((sampleId!, current));
                    }

                    break;
                case 'R' when current is not null:
                    if (Result(record, zone, messageTime) is { } result)
                    {
                        current.Add(result);
                    }

                    break;
            }
        }

        return samples
            .Where(s => s.Results.Count > 0)
            .GroupBy(s => s.SampleId, StringComparer.Ordinal)
            .Select(g => new InstrumentResultMessage(instrumentCode, g.Key, g.SelectMany(s => s.Results).ToList(), rawMessage, receivedAt))
            .ToList();
    }

    /// <summary>O.3 specimen id (first component: analysers append rack/position), else O.4 instrument specimen id.</summary>
    internal static string? SampleId(AstmRecord order)
    {
        var specimen = order.Component(3, 1).Trim();
        return specimen.Length > 0 ? specimen : order.Component(4, 1).Trim() is { Length: > 0 } instrumentId ? instrumentId : null;
    }

    /// <summary>
    /// R.3 universal test id (<c>^^^CODE</c>: the manufacturer's code is the 4th component), R.4 value, R.5 units,
    /// R.7 abnormal flags, R.9 status (X = cannot be done and I = pending are skipped), R.13 completion time.
    /// </summary>
    internal static InstrumentResult? Result(AstmRecord record, TimeZoneInfo zone, DateTimeOffset? messageTime)
    {
        var status = record.Value(9).Trim().ToUpperInvariant();
        if (status is "X" or "I")
        {
            return null;
        }

        var testId = record.Components(3);
        var code = testId.Count >= 4 && testId[3].Trim().Length > 0 ? testId[3] : testId.FirstOrDefault(c => c.Trim().Length > 0);
        var value = string.Join(" ", record.Components(4).Select(c => c.Trim()).Where(c => c.Length > 0));
        if (string.IsNullOrWhiteSpace(code) || value.Length == 0)
        {
            return null;
        }

        var units = record.Component(5, 1).Trim();
        var flags = string.Join(",", record.Repeats(7).Select(f => record.Delimiters.Unescape(f).Trim()).Where(f => f.Length > 0));
        return new InstrumentResult(code.Trim(), value, units.Length == 0 ? null : units, flags.Length == 0 ? null : flags,
            LabTime.Parse(record.Field(13), zone) ?? messageTime);
    }

    /// <summary>
    /// Samples requested by Q records. Q.3 is <c>patient id^specimen id^…</c> with repeats; the specimen id is preferred,
    /// and the "ALL" wildcard is ignored (HC360 answers per sample).
    /// </summary>
    public static IReadOnlyList<string> QueriedSampleIds(IReadOnlyList<AstmRecord> records) =>
        records.Where(r => r.Type == 'Q')
            .SelectMany(q => Enumerable.Range(0, q.Repeats(3).Count).Select(i => q.Components(3, i)))
            .Select(components => components.Count >= 2 && components[1].Trim().Length > 0
                ? components[1].Trim()
                : components.Select(c => c.Trim()).FirstOrDefault(c => c.Length > 0))
            .Where(id => !string.IsNullOrEmpty(id) && !string.Equals(id, "ALL", StringComparison.OrdinalIgnoreCase))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Host-query answer: header, one patient + order record per sample (O.5 lists <c>^^^code</c> tests, O.26 = Q
    /// "response to query"), and a terminator with code F (query processed) or I (no information available).
    /// </summary>
    public static IReadOnlyList<string> BuildOrderResponse(IReadOnlyList<InstrumentOrder> orders, AstmDelimiters delimiters, DateTimeOffset localNow, TimeZoneInfo zone)
    {
        var records = new List<string> { Header(delimiters, localNow) };
        var sequence = 0;
        foreach (var order in orders.Where(o => o.ChannelCodes.Count > 0))
        {
            sequence++;
            records.Add(PatientRecord(delimiters, sequence, order));
            records.Add(OrderRecord(delimiters, order, actionCode: "N", reportType: "Q", zone));
        }

        records.Add(AstmRecords.Build(delimiters, "L", "1", sequence == 0 ? "I" : "F"));
        return records;
    }

    /// <summary>
    /// Orders sent unasked (order download): header, then a patient + order record per sample. O.12 is the action:
    /// N for a sample that reached the lab, A to add (rerun) tests, C to cancel them; O.26 = O (order).
    /// </summary>
    public static IReadOnlyList<string> BuildOrderDownload(IReadOnlyList<LabDownload> downloads, AstmDelimiters delimiters, DateTimeOffset localNow, TimeZoneInfo zone)
    {
        var records = new List<string> { Header(delimiters, localNow) };
        var sequence = 0;
        foreach (var download in downloads.Where(d => d.Order.ChannelCodes.Count > 0))
        {
            sequence++;
            records.Add(PatientRecord(delimiters, sequence, download.Order));
            records.Add(OrderRecord(delimiters, download.Order, ActionCode(download.Action), reportType: "O", zone));
        }

        records.Add(AstmRecords.Build(delimiters, "L", "1", "N"));
        return records;
    }

    internal static string ActionCode(InstrumentOrderAction action) => action switch
    {
        InstrumentOrderAction.Cancel => "C",
        InstrumentOrderAction.Rerun => "A",
        _ => "N",
    };

    /// <summary>O.6 priority: S (stat), A (as soon as possible) or R (routine).</summary>
    internal static string Priority(OrderPriority priority) => priority switch
    {
        OrderPriority.Stat => "S",
        OrderPriority.Urgent => "A",
        _ => "R",
    };

    private static string Header(AstmDelimiters d, DateTimeOffset localNow) =>
        AstmRecords.Build(d, "H", d.Definition, null, null, $"HC360{d.Component}1.0", null, null, null, null, null, null, "P", "1",
            localNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

    /// <summary>P.3 hospital patient id (UHID), P.6 name, P.8 date of birth, P.9 sex.</summary>
    private static string PatientRecord(AstmDelimiters d, int sequence, InstrumentOrder order) =>
        AstmRecords.Build(d, "P", sequence.ToString(CultureInfo.InvariantCulture), order.PatientId is { Length: > 0 } uhid ? d.EscapeText(uhid) : null,
            null, null, d.EscapeText(order.PatientName), null, order.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture), Sex(order.Gender));

    /// <summary>O.3 sample id, O.5 tests, O.6 priority, O.8 collected at, O.12 action, O.16 specimen, O.26 report type.</summary>
    private static string OrderRecord(AstmDelimiters d, InstrumentOrder order, string actionCode, string reportType, TimeZoneInfo zone)
    {
        var tests = string.Join(d.Repeat, order.ChannelCodes.Select(code => $"{d.Component}{d.Component}{d.Component}{d.EscapeText(code)}"));
        var fields = new string?[26];
        fields[0] = "O";
        fields[1] = "1";
        fields[2] = d.EscapeText(order.SampleBarcode);
        fields[4] = tests;
        fields[5] = Priority(order.Priority);
        fields[7] = order.CollectedAt is { } collected ? TimeZoneInfo.ConvertTime(collected, zone).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : null;
        fields[11] = actionCode;
        fields[15] = order.SpecimenType is { Length: > 0 } specimen ? d.EscapeText(specimen) : null;
        fields[25] = reportType;
        return AstmRecords.Build(d, fields);
    }

    private static string Sex(string? gender) => gender?.Trim().ToUpperInvariant() switch
    {
        "M" or "MALE" => "M",
        "F" or "FEMALE" => "F",
        _ => "U",
    };
}
