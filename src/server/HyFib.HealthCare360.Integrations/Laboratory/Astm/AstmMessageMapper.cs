using System.Globalization;

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
    public static IReadOnlyList<string> BuildOrderResponse(IReadOnlyList<InstrumentOrder> orders, AstmDelimiters delimiters, DateTimeOffset localNow)
    {
        var d = delimiters;
        var records = new List<string>
        {
            AstmRecords.Build(d, "H", d.Definition, null, null, $"HC360{d.Component}1.0", null, null, null, null, null, null, "P", "1",
                localNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)),
        };

        var sequence = 0;
        foreach (var order in orders.Where(o => o.ChannelCodes.Count > 0))
        {
            sequence++;
            var number = sequence.ToString(CultureInfo.InvariantCulture);
            records.Add(AstmRecords.Build(d, "P", number, null, null, null, d.EscapeText(order.PatientName), null,
                order.DateOfBirth?.ToString("yyyyMMdd", CultureInfo.InvariantCulture), Sex(order.Gender)));

            var tests = string.Join(d.Repeat, order.ChannelCodes.Select(code => $"{d.Component}{d.Component}{d.Component}{d.EscapeText(code)}"));
            var fields = new string?[26];
            fields[0] = "O";
            fields[1] = "1";
            fields[2] = d.EscapeText(order.SampleBarcode);
            fields[4] = tests;
            fields[5] = "R";
            fields[11] = "N";
            fields[25] = "Q";
            records.Add(AstmRecords.Build(d, fields));
        }

        records.Add(AstmRecords.Build(d, "L", "1", sequence == 0 ? "I" : "F"));
        return records;
    }

    private static string Sex(string? gender) => gender?.Trim().ToUpperInvariant() switch
    {
        "M" or "MALE" => "M",
        "F" or "FEMALE" => "F",
        _ => "U",
    };
}
