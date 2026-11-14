namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>Decoded results, or the reason the message cannot be used (answered with AE).</summary>
internal sealed record Hl7MappingResult(IReadOnlyList<InstrumentResultMessage> Messages, string? Error)
{
    public static Hl7MappingResult Failed(string error) => new([], error);
}

/// <summary>
/// Maps result messages (ORU^R01, and OUL^R22 from Lab Automation profiles) to <see cref="InstrumentResultMessage"/>s,
/// one per sample. Each OBR starts an order block; its OBX segments are the results, and the NTE segments after an
/// OBX its comments. The sample barcode is taken from the configured fields in order (default SPM-2, SAC-3, OBR-3,
/// OBR-2) because analysers disagree about where they put it. Results of a block with no sample id in any of them come
/// under an empty sample id, which the LIS keeps for a person to assign. An OBX the analyser withdraws (OBX-11 D or W)
/// or could not obtain (X) is passed on as such, with or without a value, so the LIS acts on it instead of keeping the
/// value it had. The patient the PID before a block names goes with its results (<see cref="Patient"/>), for the LIS
/// to check against the tube's; a sample sent under two patients makes two messages.
/// </summary>
internal static class Hl7ResultMapper
{
    public static readonly IReadOnlyList<string> DefaultSampleIdFields = ["SPM-2", "SAC-3", "OBR-3", "OBR-2"];

    // Encapsulated data (histograms, images) and reference pointers are not results the LIS can store as values.
    private static readonly HashSet<string> SkippedValueTypes = new(StringComparer.OrdinalIgnoreCase) { "ED", "RP", "AD" };

    public static bool IsResultMessage(Hl7Message message) =>
        (message.MessageType == "ORU" && message.TriggerEvent is "R01" or "R30" or "R32")
        || (message.MessageType == "OUL" && message.TriggerEvent is "R21" or "R22" or "R23" or "R24");

    public static Hl7MappingResult Map(
        Hl7Message message, string instrumentCode, IReadOnlyList<string>? sampleIdFields, string rawMessage, DateTimeOffset receivedAt, TimeZoneInfo zone)
    {
        var blocks = Blocks(message);
        if (blocks.Count == 0 || blocks.All(b => b.Observations.Count == 0))
        {
            return Hl7MappingResult.Failed("The message contains no OBX results.");
        }

        var fields = sampleIdFields is { Count: > 0 } ? sampleIdFields : DefaultSampleIdFields;
        var messageTime = LabTime.Parse(message.Header.Value(7), zone);
        var bySample = new Dictionary<(string SampleId, InstrumentPatient? Patient), List<InstrumentResult>>();
        foreach (var block in blocks.Where(b => b.Observations.Count > 0))
        {
            // No sample id in any of the fields: the results are still the analyser's, kept for a person to assign.
            var sample = (SampleId(block, fields) ?? string.Empty, Patient(block.Patient, fields));
            var blockTime = LabTime.Parse(block.Order?.Value(7), zone) ?? messageTime;
            if (!bySample.TryGetValue(sample, out var results))
            {
                results = [];
                bySample[sample] = results;
            }

            foreach (var obx in block.Observations)
            {
                if (Result(obx, zone, blockTime) is { } result)
                {
                    results.Add(block.Comments.TryGetValue(obx, out var notes) ? result with { Comment = string.Join(" ", notes) } : result);
                }
            }
        }

        var messages = bySample.Where(s => s.Value.Count > 0)
            .Select(s => new InstrumentResultMessage(instrumentCode, s.Key.SampleId, s.Value, rawMessage, receivedAt) { Patient = s.Key.Patient })
            .ToList();
        return messages.Count == 0 ? Hl7MappingResult.Failed("The message contains no usable results.") : new Hl7MappingResult(messages, null);
    }

    internal static InstrumentResult? Result(Hl7Segment obx, TimeZoneInfo zone, DateTimeOffset? fallbackTime)
    {
        var valueType = obx.Component(2, 1);
        if (SkippedValueTypes.Contains(valueType))
        {
            return null;
        }

        var status = obx.Component(11, 1).Trim().ToUpperInvariant();
        var kind = Kind(status);
        var code = FirstNonEmpty(obx.Component(3, 1), obx.Component(3, 2));
        var value = Value(obx, valueType);

        // A withdrawal, or a result that could not be obtained, speaks of the test and needs no value.
        if (string.IsNullOrWhiteSpace(code) || (kind == InstrumentResultKind.Value && string.IsNullOrWhiteSpace(value)))
        {
            return null;
        }

        var units = FirstNonEmpty(obx.Component(6, 1), obx.Component(6, 2));
        var flags = string.Join(",", Enumerable.Range(0, obx.Repetitions(8).Count).Select(i => obx.Component(8, 1, i).Trim()).Where(f => f.Length > 0));
        return new InstrumentResult(code.Trim(), value.Trim(), string.IsNullOrWhiteSpace(units) ? null : units.Trim(),
            flags.Length == 0 ? null : flags, LabTime.Parse(obx.Value(14), zone) ?? fallbackTime)
        {
            Status = status.Length == 0 ? null : status,
            Kind = kind,
        };
    }

    /// <summary>OBX-11 as the LIS acts on it: D (deleted) and W (posted in error) withdraw a result; X could not be obtained.</summary>
    internal static InstrumentResultKind Kind(string status) => status switch
    {
        "D" or "W" => InstrumentResultKind.Withdrawn,
        "X" => InstrumentResultKind.NotObtained,
        _ => InstrumentResultKind.Value,
    };

    /// <summary>NTE-3, its repetitions joined; null for an empty note.</summary>
    private static string? Note(Hl7Segment nte)
    {
        var text = string.Join(" ", nte.Repetitions(3).Select(r => nte.Encoding.Decode(r).Trim()).Where(r => r.Length > 0));
        return text.Length == 0 ? null : text;
    }

    /// <summary>NM/ST/TX as text; coded values as their text (or code); SN as comparator + numbers ("&lt;10", "10-20").</summary>
    private static string Value(Hl7Segment obx, string valueType)
    {
        switch (valueType.ToUpperInvariant())
        {
            case "CE":
            case "CWE":
            case "CNE":
                return FirstNonEmpty(obx.Component(5, 2), obx.Component(5, 1));
            case "SN":
                var comparator = obx.Component(5, 1);
                var first = obx.Component(5, 2);
                var separator = obx.Component(5, 3);
                var second = obx.Component(5, 4);
                return string.Concat(comparator, first, second.Length > 0 ? (separator.Length > 0 ? separator : "-") + second : string.Empty);
            default:
                var repetitions = obx.Repetitions(5).Select(r => obx.Encoding.Decode(r.Replace(obx.Encoding.Component, ' '))).Where(r => r.Trim().Length > 0);
                return string.Join("; ", repetitions);
        }
    }

    private static string? SampleId(OrderBlock block, IReadOnlyList<string> fields)
    {
        foreach (var field in fields)
        {
            if (!TryField(field, out var segmentId, out var number))
            {
                continue;
            }

            var segment = segmentId switch
            {
                "SPM" => block.Specimen,
                "SAC" => block.Container,
                "OBR" => block.Order,
                "PID" => block.Patient,
                _ => null,
            };
            var value = segment?.Component(number, 1).Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>A sample-id field as configured (<c>SPM-2</c>): its segment, in capitals, and field number.</summary>
    private static bool TryField(string field, out string segment, out int number)
    {
        var parts = field.Split('-', 2, StringSplitOptions.TrimEntries);
        segment = parts[0].ToUpperInvariant();
        number = 0;
        return parts.Length == 2 && int.TryParse(parts[1], out number);
    }

    /// <summary>
    /// The patient a PID names: PID-3's patient id and PID-5's name; null without a PID, or when it names no one. A PID
    /// field the connection lists among its sample-id fields holds the analyser's sample id, not the patient's, so it is
    /// not read as the patient's.
    /// </summary>
    internal static InstrumentPatient? Patient(Hl7Segment? pid, IReadOnlyList<string> sampleIdFields)
    {
        if (pid is null)
        {
            return null;
        }

        bool HoldsSampleId(int number) =>
            sampleIdFields.Any(field => TryField(field, out var segment, out var n) && segment == "PID" && n == number);
        return InstrumentPatient.Of(HoldsSampleId(3) ? null : PatientId(pid), HoldsSampleId(5) ? null : PersonName(pid, 5));
    }

    /// <summary>
    /// PID-3, the patient identifier list (CX: id ^ check digit ^ check digit scheme ^ assigning authority ^ identifier
    /// type code, repeated): the id HC360 assigned (assigning authority HC360, as its 2.5.1 orders send the UHID), else
    /// a medical record number (type MR) that no other authority assigned, else the only id there is when it names
    /// neither, as analysers send back the bare UHID of a 2.3.1 order or the one typed in. Another authority's or type's
    /// id (the analyser's own patient number), or one of several bare ids, is not taken for the UHID: null.
    /// </summary>
    private static string? PatientId(Hl7Segment pid)
    {
        var ids = Enumerable.Range(0, pid.Repetitions(3).Count)
            .Select(i => (Id: pid.Component(3, 1, i).Trim(), Authority: pid.Component(3, 4, i).Trim(), Type: pid.Component(3, 5, i).Trim()))
            .Where(x => x.Id.Length > 0)
            .ToList();
        var assigned = ids.Where(x => string.Equals(x.Authority, Hl7OrderMessages.AssigningAuthority, StringComparison.OrdinalIgnoreCase))
            .Concat(ids.Where(x => x.Authority.Length == 0 && string.Equals(x.Type, "MR", StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Id)
            .FirstOrDefault();
        return assigned ?? (ids is [{ Authority.Length: 0, Type.Length: 0 } only] ? only.Id : null);
    }

    /// <summary>
    /// A name field (XPN), its first repetition with a name: family name, given name and further given names as the
    /// analyser wrote them, joined by spaces. The suffix, prefix (Mr, Dr) and degree are not name parts; null for none.
    /// </summary>
    private static string? PersonName(Hl7Segment segment, int field)
    {
        for (var repetition = 0; repetition < segment.Repetitions(field).Count; repetition++)
        {
            var parts = Enumerable.Range(1, 3).Select(c => segment.Component(field, c, repetition).Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count > 0)
            {
                return string.Join(' ', parts);
            }
        }

        return null;
    }

    /// <summary>
    /// Groups segments into order blocks. ORU^R01 carries SPM after its OBR (it belongs to that order); OUL^R22 carries
    /// SPM/SAC before the OBRs they apply to, so there a new block inherits the current specimen and container. NTE
    /// segments straight after an OBX are that result's comments; notes on the order or the patient are not results.
    /// </summary>
    private static List<OrderBlock> Blocks(Hl7Message message)
    {
        var inheritSpecimen = message.MessageType == "OUL";
        var blocks = new List<OrderBlock>();
        Hl7Segment? patient = null, specimen = null, container = null;
        OrderBlock? current = null;
        Hl7Segment? noted = null;
        foreach (var segment in message.Segments)
        {
            if (segment.Id is not ("OBX" or "NTE"))
            {
                noted = null;
            }

            switch (segment.Id)
            {
                case "PID":
                    patient = segment;
                    specimen = container = null;
                    current = null;
                    break;
                case "SPM":
                    specimen = segment;
                    container = null;
                    if (current is not null && !inheritSpecimen && current.Specimen is null)
                    {
                        current.Specimen = segment;
                    }

                    break;
                case "SAC":
                    container = segment;
                    if (current is not null && !inheritSpecimen && current.Container is null)
                    {
                        current.Container = segment;
                    }

                    break;
                case "OBR":
                    current = new OrderBlock
                    {
                        Order = segment,
                        Patient = patient,
                        Specimen = inheritSpecimen ? specimen : null,
                        Container = inheritSpecimen ? container : null,
                    };
                    blocks.Add(current);
                    break;
                case "OBX":
                    if (current is null)
                    {
                        current = new OrderBlock { Patient = patient, Specimen = specimen, Container = container };
                        blocks.Add(current);
                    }

                    current.Observations.Add(segment);
                    noted = segment;
                    break;
                case "NTE" when current is not null && noted is not null && Note(segment) is { } note:
                    if (!current.Comments.TryGetValue(noted, out var notes))
                    {
                        notes = [];
                        current.Comments[noted] = notes;
                    }

                    notes.Add(note);
                    break;
            }
        }

        return blocks;
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    private sealed class OrderBlock
    {
        public Hl7Segment? Order { get; init; }
        public Hl7Segment? Patient { get; init; }
        public Hl7Segment? Specimen { get; set; }
        public Hl7Segment? Container { get; set; }
        public List<Hl7Segment> Observations { get; } = [];

        /// <summary>The analyser's notes on each result, by its OBX segment.</summary>
        public Dictionary<Hl7Segment, List<string>> Comments { get; } = [];
    }
}
