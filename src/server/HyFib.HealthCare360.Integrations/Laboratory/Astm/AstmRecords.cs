using System.Text;

namespace HyFib.HealthCare360.Integrations.Laboratory.Astm;

/// <summary>E1394 delimiters, declared by each message's header record (<c>H|\^&amp;</c>: field, repeat, component, escape).</summary>
internal sealed record AstmDelimiters(char Field, char Repeat, char Component, char Escape)
{
    public static AstmDelimiters Default { get; } = new('|', '\\', '^', '&');

    /// <summary>H.2 as written in a header record: repeat, component and escape delimiters.</summary>
    public string Definition => string.Concat(Repeat, Component, Escape);

    public static AstmDelimiters FromHeader(string? headerRecord) =>
        headerRecord is { Length: >= 5 } && char.ToUpperInvariant(headerRecord[0]) == 'H'
            ? new AstmDelimiters(headerRecord[1], headerRecord[2], headerRecord[3], headerRecord[4])
            : Default;

    /// <summary>Decodes <c>&amp;F&amp; &amp;S&amp; &amp;R&amp; &amp;E&amp;</c> (with the message's escape character).</summary>
    public string Unescape(string value)
    {
        if (value.IndexOf(Escape, StringComparison.Ordinal) < 0)
        {
            return value;
        }

        return value
            .Replace($"{Escape}F{Escape}", Field.ToString(), StringComparison.Ordinal)
            .Replace($"{Escape}S{Escape}", Component.ToString(), StringComparison.Ordinal)
            .Replace($"{Escape}R{Escape}", Repeat.ToString(), StringComparison.Ordinal)
            .Replace($"{Escape}E{Escape}", Escape.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Escapes delimiters in free text (names, ids) and removes line breaks, which would end the record.</summary>
    public string EscapeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == Escape) { builder.Append(Escape).Append('E').Append(Escape); }
            else if (c == Field) { builder.Append(Escape).Append('F').Append(Escape); }
            else if (c == Component) { builder.Append(Escape).Append('S').Append(Escape); }
            else if (c == Repeat) { builder.Append(Escape).Append('R').Append(Escape); }
            else if (!char.IsControl(c)) { builder.Append(c); }
        }

        return builder.ToString();
    }
}

/// <summary>One E1394 record. Field numbers are 1-based and field 1 is the record type (in H, field 2 is the delimiter definition).</summary>
internal sealed class AstmRecord
{
    private readonly string[] _fields;

    public AstmRecord(string text, AstmDelimiters delimiters)
    {
        Text = text;
        Delimiters = delimiters;
        _fields = text.Split(delimiters.Field);
    }

    public string Text { get; }

    public AstmDelimiters Delimiters { get; }

    public char Type => Text.Length > 0 ? char.ToUpperInvariant(Text[0]) : '\0';

    /// <summary>Raw field (still escaped).</summary>
    public string Field(int number) => number >= 1 && number <= _fields.Length ? _fields[number - 1] : string.Empty;

    public IReadOnlyList<string> Repeats(int field) =>
        Type == 'H' && field == 2 ? [Field(2)] : Field(field) is { Length: > 0 } raw ? raw.Split(Delimiters.Repeat) : [];

    /// <summary>Unescaped components (1-based positions map to list index + 1) of one repeat.</summary>
    public IReadOnlyList<string> Components(int field, int repeat = 0)
    {
        var repeats = Repeats(field);
        return repeat < repeats.Count ? repeats[repeat].Split(Delimiters.Component).Select(Delimiters.Unescape).ToList() : [];
    }

    public string Component(int field, int component, int repeat = 0)
    {
        var components = Components(field, repeat);
        return component >= 1 && component <= components.Count ? components[component - 1] : string.Empty;
    }

    public string Value(int field) => Delimiters.Unescape(Field(field));
}

internal static class AstmRecords
{
    /// <summary>Parses message text (records separated by CR); delimiters come from the message's header record.</summary>
    public static IReadOnlyList<AstmRecord> Parse(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && char.IsAsciiLetter(line[0]))
            .ToList();
        var delimiters = AstmDelimiters.FromHeader(lines.FirstOrDefault(l => char.ToUpperInvariant(l[0]) == 'H'));
        return lines.Select(line => new AstmRecord(line, delimiters)).ToList();
    }

    /// <summary>Joins fields into a record, dropping trailing empty fields (E1394 allows omitting them).</summary>
    public static string Build(AstmDelimiters delimiters, params string?[] fields) =>
        string.Join(delimiters.Field, fields.Select(f => f ?? string.Empty)).TrimEnd(delimiters.Field);
}
