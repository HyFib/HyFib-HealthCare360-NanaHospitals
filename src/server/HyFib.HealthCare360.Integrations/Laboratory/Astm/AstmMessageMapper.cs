using System.Globalization;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;

namespace HyFib.HealthCare360.Integrations.Laboratory.Astm;

/// <summary>
/// E1394 message content: result records (O → R) to <see cref="InstrumentResultMessage"/>s, host queries (Q) to
/// sample ids, and LIS orders back to H/P/O/L records for the analyser.
/// </summary>
internal static class AstmMessageMapper
{
    /// <summary>
    /// The results of each sample, in record order, with the patient of the P record they come under
    /// (<see cref="Patient"/>), for the LIS to check against the tube's. Results under an order record whose sample id
    /// cannot be read, or with no order record of their own, come under an empty sample id: the LIS keeps them for a
    /// person to assign, where acknowledging the message and dropping them would lose them without anyone knowing. A
    /// sample sent under two patients makes two messages. An analyser whose dialect keys samples by P.3
    /// (<see cref="AstmDialect.SampleIdInPatientRecord"/>) sends results with no order record under the P.3 sample id.
    /// </summary>
    public static IReadOnlyList<InstrumentResultMessage> MapResults(
        IReadOnlyList<AstmRecord> records, string instrumentCode, string rawMessage, DateTimeOffset receivedAt, TimeZoneInfo zone, AstmDialect? dialect = null)
    {
        dialect ??= AstmDialect.Standard;
        var header = records.FirstOrDefault(r => r.Type == 'H');
        if (!dialect.CarriesPatientResults(header?.Value(12)))
        {
            // A QC or technician run: not a patient's results, so nothing for the LIS to store on a tube.
            return [];
        }

        var messageTime = MessageTime(header, zone);
        var samples = new List<(string SampleId, InstrumentPatient? Patient, List<InstrumentResult> Results)>();
        InstrumentPatient? patient = null;
        string? patientSampleId = null;
        List<InstrumentResult>? current = null;

        // A comment record belongs to the result record it follows (its position in the sample's results), if any.
        var commented = -1;

        // Under a control sample's order (O.16 CONTROL), whose results are QC, not a patient's.
        var control = false;
        foreach (var record in records)
        {
            switch (record.Type)
            {
                case 'P':
                    // A new patient: results must be preceded by their own order record.
                    patient = Patient(record, dialect);
                    control = false;
                    patientSampleId = dialect.SampleIdInPatientRecord && record.Component(3, 1).Trim() is { Length: > 0 } keyed ? keyed : null;
                    current = null;
                    commented = -1;
                    break;
                case 'O' when dialect.PatientResultsOnly && record.Component(16, 1).Trim().Equals("CONTROL", StringComparison.OrdinalIgnoreCase):
                    control = true;
                    current = null;
                    commented = -1;
                    break;
                case 'O':
                    control = false;
                    current = [];
                    samples.Add((SampleId(record) ?? patientSampleId ?? string.Empty, patient, current));
                    commented = -1;
                    break;
                case 'R' when control:
                    commented = -1;
                    break;
                case 'R':
                    if (current is null)
                    {
                        current = [];
                        samples.Add((patientSampleId ?? string.Empty, patient, current));
                    }

                    commented = -1;
                    if (Result(record, zone, messageTime, dialect) is { } result)
                    {
                        current.Add(result);
                        commented = current.Count - 1;
                    }

                    break;
                case 'C' when current is not null && commented >= 0 && Comment(record) is { } comment:
                    var annotated = current[commented];
                    current[commented] = annotated with { Comment = annotated.Comment is null ? comment : $"{annotated.Comment} {comment}" };
                    break;
            }
        }

        return samples
            .Where(s => s.Results.Count > 0)
            .GroupBy(s => (s.SampleId, s.Patient))
            .Select(g => new InstrumentResultMessage(instrumentCode, g.Key.SampleId, g.SelectMany(s => s.Results).ToList(), rawMessage, receivedAt)
            {
                Patient = g.Key.Patient,
            })
            .ToList();
    }

    /// <summary>
    /// The patient a P record names: its patient id, and P.6, the name (last ^ first ^ middle as the analyser wrote them,
    /// joined by spaces; the suffix and title are not name parts). The id is the first of P.3 (practice-assigned, where
    /// HC360's orders put the UHID), P.4 (laboratory-assigned) and P.5 (a third id) the analyser filled, as analysers
    /// differ in where they put it. One of them may be the analyser's own number: the LIS takes an id for a patient's only
    /// when a registered patient has it. Null when the record names no one.
    /// </summary>
    /// <remarks>An analyser whose P.3 is the sample id (<see cref="AstmDialect.SampleIdInPatientRecord"/>) names the patient by P.4 or P.5 only.</remarks>
    internal static InstrumentPatient? Patient(AstmRecord record, AstmDialect? dialect = null) =>
        InstrumentPatient.Of(
            (dialect?.SampleIdInPatientRecord == true ? new[] { 4, 5 } : [3, 4, 5])
                .Select(field => record.Component(field, 1).Trim()).FirstOrDefault(id => id.Length > 0),
            string.Join(' ', record.Components(6).Take(3).Select(c => c.Trim()).Where(c => c.Length > 0)));

    /// <summary>
    /// H.14, the message's date and time; else H.13 when it holds a full <c>YYYYMMDDHHMMSS</c>, as analysers that leave
    /// out the processing id write it (the Yumizen C150's <c>…|E-1394-97|20131203141051</c>).
    /// </summary>
    internal static DateTimeOffset? MessageTime(AstmRecord? header, TimeZoneInfo zone) =>
        LabTime.Parse(header?.Field(14), zone)
        ?? (header?.Field(13).Trim() is { Length: 14 } stamp && stamp.All(char.IsAsciiDigit) ? LabTime.Parse(stamp, zone) : null);

    /// <summary>O.3 specimen id (first component: analysers append rack/position), else O.4 instrument specimen id.</summary>
    internal static string? SampleId(AstmRecord order)
    {
        var specimen = order.Component(3, 1).Trim();
        return specimen.Length > 0 ? specimen : order.Component(4, 1).Trim() is { Length: > 0 } instrumentId ? instrumentId : null;
    }

    /// <summary>
    /// R.3 universal test id (<c>^^^CODE</c>: the manufacturer's code is the 4th component), R.4 value, R.5 units,
    /// R.7 abnormal flags, R.9 status (I = pending on the analyser is skipped; X = cannot be done is passed on as a
    /// result not obtained, which needs no value; the rest, F final, P preliminary, C correction…, is passed on),
    /// R.13 completion time. In a dialect whose result fields may sit one to the right
    /// (<see cref="AstmDialect.ResultFieldsShifted"/>), a record with a status in R.10 is read as flags R.8, status R.10
    /// and time R.14.
    /// </summary>
    internal static InstrumentResult? Result(AstmRecord record, TimeZoneInfo zone, DateTimeOffset? messageTime, AstmDialect? dialect = null)
    {
        var shift = dialect?.ResultFieldsShifted == true && IsResultStatus(record.Value(10)) ? 1 : 0;
        var status = record.Value(9 + shift).Trim().ToUpperInvariant();
        if (status == "I")
        {
            return null;
        }

        var kind = status == "X" ? InstrumentResultKind.NotObtained : InstrumentResultKind.Value;
        var testId = record.Components(3);
        var code = testId.Count >= 4 && testId[3].Trim().Length > 0 ? testId[3] : testId.FirstOrDefault(c => c.Trim().Length > 0);
        var value = string.Join(" ", record.Components(4).Select(c => c.Trim()).Where(c => c.Length > 0));
        string? note = null;
        if (dialect?.DashValuesNotObtained == true && value is "---" or "+++")
        {
            // Not a number: the analyser could not give one (---) or it is beyond the range it reports (+++).
            note = value == "+++" ? "Above the analyser's reportable range (+++)" : "No result from the analyser (---)";
            kind = InstrumentResultKind.NotObtained;
        }

        if (string.IsNullOrWhiteSpace(code) || (kind == InstrumentResultKind.Value && value.Length == 0))
        {
            return null;
        }

        var units = record.Component(5, 1).Trim();
        var flags = string.Join(",", record.Repeats(7 + shift).Select(f => record.Delimiters.Unescape(f).Trim()).Where(f => f.Length > 0));
        return new InstrumentResult(code.Trim(), value, units.Length == 0 ? null : units, flags.Length == 0 ? null : flags,
            LabTime.Parse(record.Field(13 + shift), zone) ?? messageTime)
        {
            Status = status.Length == 0 ? null : status,
            Kind = kind,
            Comment = note,
        };
    }

    /// <summary>E1394 result status codes (R.9): C, P, F, X, I, S, M, R, N, Q, V, W.</summary>
    private static bool IsResultStatus(string value) => value.Trim() is { Length: 1 } code && "CPFXISMRNQVW".Contains(char.ToUpperInvariant(code[0]));

    /// <summary>C.4 comment text (its components joined), or null for an empty comment.</summary>
    internal static string? Comment(AstmRecord record)
    {
        var text = string.Join(" ", Enumerable.Range(0, record.Repeats(4).Count)
            .SelectMany(repeat => record.Components(4, repeat))
            .Select(c => c.Trim())
            .Where(c => c.Length > 0));
        return text.Length == 0 ? null : text;
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
    /// "response to query"), and a terminator with code F (query processed) or I (no information available); the dialect
    /// may ask for other codes (<see cref="AstmDialect.QueryAnswerReportType"/>, <see cref="AstmDialect.TerminatorAlwaysNormal"/>).
    /// </summary>
    /// <param name="queriedSampleIds">The samples asked for; one HC360 has nothing for is answered with an empty order where the dialect wants it (<see cref="AstmDialect.AnswersUnknownSamples"/>).</param>
    /// <param name="queryReceiver">The query's H.10 as sent, copied to the answer's H.5 where the dialect wants it.</param>
    public static IReadOnlyList<string> BuildOrderResponse(
        IReadOnlyList<InstrumentOrder> orders, AstmDelimiters delimiters, DateTimeOffset localNow, TimeZoneInfo zone, AstmDialect? dialect = null,
        IReadOnlyList<string>? queriedSampleIds = null, string? queryReceiver = null)
    {
        dialect ??= AstmDialect.Standard;
        var records = new List<string> { Header(delimiters, localNow, dialect, queryReceiver) };
        var sequence = 0;
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var order in orders.Where(o => dialect.OrderedTests(o.ChannelCodes).Count > 0))
        {
            sequence++;
            answered.Add(order.SampleBarcode);
            records.Add(PatientRecord(delimiters, sequence, order, dialect));
            records.Add(OrderRecord(delimiters, order, actionCode: "N", reportType: dialect.QueryAnswerReportType, zone, dialect));
        }

        if (dialect.AnswersUnknownSamples)
        {
            // "No test for this record" (O.26 Y), as the analyser's manual answers a sample the host has nothing for.
            foreach (var id in (queriedSampleIds ?? []).Where(id => !answered.Contains(id)))
            {
                sequence++;
                var empty = new string?[26];
                empty[0] = "O";
                empty[1] = "1";
                empty[2] = delimiters.EscapeText(id);
                empty[4] = $"{delimiters.Component}{delimiters.Component}{delimiters.Component}";
                empty[11] = "N";
                empty[25] = "Y";
                // "P|1|" with its trailing delimiter, as the manual's answer writes the empty patient.
                records.Add(AstmRecords.Build(delimiters, "P", sequence.ToString(CultureInfo.InvariantCulture)) + delimiters.Field);
                records.Add(AstmRecords.Build(delimiters, empty));
            }
        }

        records.Add(AstmRecords.Build(delimiters, "L", "1", dialect.TerminatorAlwaysNormal ? "N" : sequence == 0 ? "I" : "F"));
        return records;
    }

    /// <summary>
    /// Orders sent unasked (order download): header, then a patient + order record per sample. O.12 is the action:
    /// N for a sample that reached the lab, A to add (rerun) tests, C to cancel them; O.26 = O (order).
    /// </summary>
    public static IReadOnlyList<string> BuildOrderDownload(
        IReadOnlyList<LabDownload> downloads, AstmDelimiters delimiters, DateTimeOffset localNow, TimeZoneInfo zone, AstmDialect? dialect = null)
    {
        dialect ??= AstmDialect.Standard;
        var records = new List<string> { Header(delimiters, localNow, dialect) };
        var sequence = 0;
        foreach (var download in downloads.Where(d => d.Order.ChannelCodes.Count > 0))
        {
            sequence++;
            records.Add(PatientRecord(delimiters, sequence, download.Order, dialect));
            records.Add(OrderRecord(delimiters, download.Order, ActionCode(download.Action), reportType: "O", zone, dialect));
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
    internal static string Priority(OrderPriority priority) => AstmDialect.Standard.Priority(priority);

    /// <summary>H.5 is HC360 (or, where the dialect wants it, the query's H.10 as it came), H.12 P, H.13 the version, H.14 the time.</summary>
    private static string Header(AstmDelimiters d, DateTimeOffset localNow, AstmDialect dialect, string? queryReceiver = null) =>
        AstmRecords.Build(d, "H", d.Definition, null, null, dialect.HeaderSenderEchoesQuery ? queryReceiver : $"HC360{d.Component}1.0",
            null, null, null, null, null, null, "P", dialect.HeaderVersion, localNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));

    /// <summary>
    /// P.3 hospital patient id (UHID), P.6 name, P.8 date of birth, P.9 sex. An analyser that keys samples by P.3
    /// (<see cref="AstmDialect.SampleIdInPatientRecord"/>) is given the tube's barcode there instead, which it sends
    /// back with the results.
    /// </summary>
    private static string PatientRecord(AstmDelimiters d, int sequence, InstrumentOrder order, AstmDialect dialect)
    {
        var id = dialect.SampleIdInPatientRecord ? order.SampleBarcode : order.PatientId;
        var name = d.EscapeText(order.PatientName);
        if (dialect.MaxPatientNameLength is { } max && name.Length > max)
        {
            name = name[..max].TrimEnd();
        }

        var birth = order.DateOfBirth?.ToString(dialect.DateOfBirthWithTime ? "yyyyMMdd'000000'" : "yyyyMMdd", CultureInfo.InvariantCulture);
        return AstmRecords.Build(d, "P", sequence.ToString(CultureInfo.InvariantCulture), id is { Length: > 0 } ? d.EscapeText(id) : null,
            null, null, name, null, birth, Sex(order.Gender));
    }

    /// <summary>O.3 sample id, O.5 tests, O.6 priority, O.8 collected at, O.12 action, O.16 specimen, O.26 report type.</summary>
    private static string OrderRecord(AstmDelimiters d, InstrumentOrder order, string actionCode, string reportType, TimeZoneInfo zone, AstmDialect dialect)
    {
        var tests = string.Join(d.Repeat, dialect.OrderedTests(order.ChannelCodes).Select(code => $"{d.Component}{d.Component}{d.Component}{d.EscapeText(code)}"));
        var fields = new string?[26];
        fields[0] = "O";
        fields[1] = "1";
        fields[2] = d.EscapeText(order.SampleBarcode);
        fields[4] = tests;
        fields[5] = dialect.Priority(order.Priority);
        fields[7] = order.CollectedAt is { } collected ? TimeZoneInfo.ConvertTime(collected, zone).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) : null;
        fields[11] = actionCode;
        fields[15] = dialect.Specimen(order.SpecimenType) is { } specimen ? d.EscapeText(specimen) : null;
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
