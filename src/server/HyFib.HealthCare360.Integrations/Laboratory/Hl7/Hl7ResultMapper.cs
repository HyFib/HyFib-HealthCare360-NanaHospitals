namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>Decoded results, or the reason the message cannot be used (answered with AE).</summary>
internal sealed record Hl7MappingResult(IReadOnlyList<InstrumentResultMessage> Messages, string? Error)
{
    public static Hl7MappingResult Failed(string error) => new([], error);
}

/// <summary>
/// Maps result messages (ORU^R01, and OUL^R22 from Lab Automation profiles) to <see cref="InstrumentResultMessage"/>s,
/// one per sample. Each OBR starts an order block; its OBX segments are the results. The sample barcode is taken from
/// the configured fields in order (default SPM-2, SAC-3, OBR-3, OBR-2) because analysers disagree about where they put it.
/// </summary>
internal static class Hl7ResultMapper
{
    public static readonly IReadOnlyList<string> DefaultSampleIdFields = ["SPM-2", "SAC-3", "OBR-3", "OBR-2"];

    // Encapsulated data (histograms, images) and reference pointers are not results the LIS can store as values.
    private static readonly HashSet<string> SkippedValueTypes = new(StringComparer.OrdinalIgnoreCase) { "ED", "RP", "AD" };

    // D = delete, W = posted in error, X = could not be obtained.
    private static readonly HashSet<string> SkippedStatuses = new(StringComparer.OrdinalIgnoreCase) { "D", "W", "X" };

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
        var bySample = new Dictionary<string, List<InstrumentResult>>(StringComparer.Ordinal);
        foreach (var block in blocks.Where(b => b.Observations.Count > 0))
        {
            var sampleId = SampleId(block, fields);
            if (string.IsNullOrWhiteSpace(sampleId))
            {
                return Hl7MappingResult.Failed($"No sample id found in {string.Join(", ", fields)}.");
            }

            var blockTime = LabTime.Parse(block.Order?.Value(7), zone) ?? messageTime;
            if (!bySample.TryGetValue(sampleId, out var results))
            {
                results = [];
                bySample[sampleId] = results;
            }

            results.AddRange(block.Observations.Select(obx => Result(obx, zone, blockTime)).OfType<InstrumentResult>());
        }

        var messages = bySample.Where(s => s.Value.Count > 0)
            .Select(s => new InstrumentResultMessage(instrumentCode, s.Key, s.Value, rawMessage, receivedAt))
            .ToList();
        return messages.Count == 0 ? Hl7MappingResult.Failed("The message contains no usable results.") : new Hl7MappingResult(messages, null);
    }

    internal static InstrumentResult? Result(Hl7Segment obx, TimeZoneInfo zone, DateTimeOffset? fallbackTime)
    {
        var valueType = obx.Component(2, 1);
        var status = obx.Component(11, 1);
        if (SkippedValueTypes.Contains(valueType) || SkippedStatuses.Contains(status))
        {
            return null;
        }

        var code = FirstNonEmpty(obx.Component(3, 1), obx.Component(3, 2));
        var value = Value(obx, valueType);
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var units = FirstNonEmpty(obx.Component(6, 1), obx.Component(6, 2));
        var flags = string.Join(",", Enumerable.Range(0, obx.Repetitions(8).Count).Select(i => obx.Component(8, 1, i).Trim()).Where(f => f.Length > 0));
        return new InstrumentResult(code.Trim(), value.Trim(), string.IsNullOrWhiteSpace(units) ? null : units.Trim(),
            flags.Length == 0 ? null : flags, LabTime.Parse(obx.Value(14), zone) ?? fallbackTime);
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
            var parts = field.Split('-', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[1], out var number))
            {
                continue;
            }

            var segment = parts[0].ToUpperInvariant() switch
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

    /// <summary>
    /// Groups segments into order blocks. ORU^R01 carries SPM after its OBR (it belongs to that order); OUL^R22 carries
    /// SPM/SAC before the OBRs they apply to, so there a new block inherits the current specimen and container.
    /// </summary>
    private static List<OrderBlock> Blocks(Hl7Message message)
    {
        var inheritSpecimen = message.MessageType == "OUL";
        var blocks = new List<OrderBlock>();
        Hl7Segment? patient = null, specimen = null, container = null;
        OrderBlock? current = null;
        foreach (var segment in message.Segments)
        {
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
    }
}
