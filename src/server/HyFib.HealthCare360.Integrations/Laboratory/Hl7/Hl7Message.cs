using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace HyFib.HealthCare360.Integrations.Laboratory.Hl7;

/// <summary>HL7 v2 delimiters (MSH-1 and MSH-2) with the standard escape sequences.</summary>
internal sealed record Hl7Encoding(char Field, char Component, char Repetition, char Escape, char Subcomponent)
{
    public static Hl7Encoding Default { get; } = new('|', '^', '~', '\\', '&');

    public string EncodingCharacters => string.Concat(Component, Repetition, Escape, Subcomponent);

    /// <summary>
    /// Decodes <c>\F\ \S\ \T\ \R\ \E\</c>, <c>\.br\</c> and <c>\Xhh…\</c>; formatting escapes (<c>\H\</c>, <c>\N\</c>…)
    /// carry no result data and are dropped. An unterminated escape is kept literally.
    /// </summary>
    public string Decode(string value)
    {
        if (value.IndexOf(Escape, StringComparison.Ordinal) < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != Escape)
            {
                builder.Append(value[i]);
                continue;
            }

            var end = value.IndexOf(Escape, i + 1);
            if (end < 0)
            {
                builder.Append(value, i, value.Length - i);
                break;
            }

            var code = value[(i + 1)..end];
            switch (code)
            {
                case "F": builder.Append(Field); break;
                case "S": builder.Append(Component); break;
                case "T": builder.Append(Subcomponent); break;
                case "R": builder.Append(Repetition); break;
                case "E": builder.Append(Escape); break;
                case ".br": builder.Append('\n'); break;
                default:
                    if (code.Length > 1 && code[0] == 'X' && TryHex(code[1..], out var bytes))
                    {
                        builder.Append(Encoding.Latin1.GetString(bytes));
                    }

                    break;
            }

            i = end;
        }

        return builder.ToString();
    }

    /// <summary>Escapes delimiter characters so free text (for example an ACK error message) cannot break the segment.</summary>
    public string Encode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == Escape) { builder.Append(Escape).Append('E').Append(Escape); }
            else if (c == Field) { builder.Append(Escape).Append('F').Append(Escape); }
            else if (c == Component) { builder.Append(Escape).Append('S').Append(Escape); }
            else if (c == Subcomponent) { builder.Append(Escape).Append('T').Append(Escape); }
            else if (c == Repetition) { builder.Append(Escape).Append('R').Append(Escape); }
            else if (c is '\r' or '\n') { builder.Append(' '); }
            else { builder.Append(c); }
        }

        return builder.ToString();
    }

    private static bool TryHex(string hex, out byte[] bytes)
    {
        bytes = [];
        if (hex.Length % 2 != 0)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>One segment. Field numbers follow HL7 (1-based; in MSH, field 1 is the field separator itself).</summary>
internal sealed class Hl7Segment
{
    private readonly string[] _fields;

    public Hl7Segment(string text, Hl7Encoding encoding)
    {
        Text = text;
        Encoding = encoding;
        _fields = text.Split(encoding.Field);
    }

    public string Text { get; }

    public Hl7Encoding Encoding { get; }

    public string Id => _fields[0];

    /// <summary>Raw (still escaped) field.</summary>
    public string Field(int number)
    {
        if (Id == "MSH")
        {
            return number switch
            {
                < 1 => string.Empty,
                1 => Encoding.Field.ToString(),
                _ => number - 1 < _fields.Length ? _fields[number - 1] : string.Empty,
            };
        }

        return number >= 1 && number < _fields.Length ? _fields[number] : string.Empty;
    }

    /// <summary>Raw repetitions of a field (empty when the field is empty).</summary>
    public IReadOnlyList<string> Repetitions(int field) => Field(field) is { Length: > 0 } raw ? raw.Split(Encoding.Repetition) : [];

    /// <summary>Decoded component (1-based) of the given repetition; the first subcomponent only.</summary>
    public string Component(int field, int component, int repetition = 0)
    {
        var repetitions = Repetitions(field);
        if (repetition >= repetitions.Count || component < 1)
        {
            return string.Empty;
        }

        var components = repetitions[repetition].Split(Encoding.Component);
        return component - 1 < components.Length ? Encoding.Decode(components[component - 1].Split(Encoding.Subcomponent)[0]) : string.Empty;
    }

    /// <summary>Whole field decoded (for single-valued fields such as MSH-10).</summary>
    public string Value(int field) => Encoding.Decode(Field(field));
}

/// <summary>
/// Minimal HL7 v2 parser: enough of the encoding rules (separators from MSH, repetitions, escape sequences) to read
/// analyser result messages robustly without a full HL7 library. Segments may be separated by CR, LF or CRLF.
/// </summary>
internal sealed class Hl7Message
{
    private Hl7Message(Hl7Encoding encoding, IReadOnlyList<Hl7Segment> segments)
    {
        Encoding = encoding;
        Segments = segments;
    }

    public Hl7Encoding Encoding { get; }

    public IReadOnlyList<Hl7Segment> Segments { get; }

    public Hl7Segment Header => Segments[0];

    public string MessageType => Header.Component(9, 1);

    public string TriggerEvent => Header.Component(9, 2);

    public string ControlId => Header.Value(10);

    public string Version => Header.Component(12, 1);

    public static bool TryParse(string text, [NotNullWhen(true)] out Hl7Message? message, [NotNullWhen(false)] out string? error)
    {
        message = null;
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimStart())
            .Where(line => line.Length > 0)
            .ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("MSH", StringComparison.Ordinal) || lines[0].Length < 8)
        {
            error = "The message does not start with an MSH segment.";
            return false;
        }

        var fieldSeparator = lines[0][3];
        var encodingCharacters = lines[0][4..].Split(fieldSeparator)[0];
        if (encodingCharacters.Length < 3 || encodingCharacters.Contains(fieldSeparator, StringComparison.Ordinal))
        {
            error = "MSH-2 does not define the HL7 encoding characters.";
            return false;
        }

        var encoding = new Hl7Encoding(
            fieldSeparator,
            encodingCharacters[0],
            encodingCharacters[1],
            encodingCharacters[2],
            encodingCharacters.Length > 3 ? encodingCharacters[3] : '&');
        var segments = lines.Select(line => new Hl7Segment(line, encoding)).Where(s => s.Id.Length == 3).ToList();
        message = new Hl7Message(encoding, segments);
        error = null;
        return true;
    }
}
