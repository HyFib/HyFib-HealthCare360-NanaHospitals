namespace HyFib.HealthCare360.Integrations.Laboratory;

/// <summary>
/// Removes patient identity from raw analyser messages before they are written to the integration log (SEC-10).
/// Names, birth dates, addresses and phone numbers are replaced; patient identifiers keep their last four
/// characters (enough to troubleshoot a mismatch); sample barcodes and results are kept because the log exists to
/// diagnose exactly those. The unmasked message goes only to the LIS, which stores clinical data anyway.
/// </summary>
internal static class LabPhiMasking
{
    private const string Removed = "***";

    // HL7 field numbers per segment: removed entirely / identifiers masked to the last four characters.
    private static readonly Dictionary<string, (int[] Removed, int[] Identifiers)> Hl7Fields = new(StringComparer.Ordinal)
    {
        ["PID"] = ([5, 6, 7, 9, 11, 13, 14, 19, 20], [2, 3, 4]),
        ["NK1"] = ([2, 4, 5, 6], []),
        ["GT1"] = ([3, 5, 6, 7], []),
        ["IN1"] = ([16, 18, 19], [36]),
    };

    // ASTM P record field numbers (1-based, field 1 = "P").
    private static readonly int[] AstmRemoved = [6, 7, 8, 11, 13];
    private static readonly int[] AstmIdentifiers = [3, 4, 5];

    public static string Mask(string message, LabProtocol protocol) => protocol == LabProtocol.Hl7v2 ? MaskHl7(message) : MaskAstm(message);

    public static string MaskHl7(string message)
    {
        var fieldSeparator = message.StartsWith("MSH", StringComparison.Ordinal) && message.Length > 4 ? message[3] : '|';
        var componentSeparator = message.StartsWith("MSH", StringComparison.Ordinal) && message.Length > 5 ? message[4] : '^';
        var segments = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length < 3 || !Hl7Fields.TryGetValue(segment[..3], out var rules))
            {
                continue;
            }

            var fields = segment.Split(fieldSeparator);
            foreach (var number in rules.Removed.Where(n => n < fields.Length && fields[n].Length > 0))
            {
                fields[number] = Removed;
            }

            foreach (var number in rules.Identifiers.Where(n => n < fields.Length && fields[n].Length > 0))
            {
                fields[number] = MaskIdentifier(fields[number], componentSeparator);
            }

            segments[i] = string.Join(fieldSeparator, fields);
        }

        return string.Join('\r', segments);
    }

    public static string MaskAstm(string message)
    {
        var records = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var header = records.FirstOrDefault(r => r.StartsWith('H'));
        var fieldSeparator = header is { Length: > 1 } ? header[1] : '|';
        var componentSeparator = header is { Length: > 3 } ? header[3] : '^';
        for (var i = 0; i < records.Length; i++)
        {
            if (!records[i].StartsWith('P'))
            {
                continue;
            }

            var fields = records[i].Split(fieldSeparator);
            foreach (var index in AstmRemoved.Select(n => n - 1).Where(n => n < fields.Length && fields[n].Length > 0))
            {
                fields[index] = Removed;
            }

            foreach (var index in AstmIdentifiers.Select(n => n - 1).Where(n => n < fields.Length && fields[n].Length > 0))
            {
                fields[index] = MaskIdentifier(fields[index], componentSeparator);
            }

            records[i] = string.Join(fieldSeparator, fields);
        }

        return string.Join('\r', records);
    }

    /// <summary>Keeps the last four characters of the identifier (first component); drops assigning-authority noise.</summary>
    private static string MaskIdentifier(string field, char componentSeparator)
    {
        var id = field.Split(componentSeparator)[0].Trim();
        return id.Length <= 4 ? new string('*', id.Length) : new string('*', id.Length - 4) + id[^4..];
    }
}
