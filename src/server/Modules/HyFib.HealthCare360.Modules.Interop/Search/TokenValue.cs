using System.Text;

namespace HyFib.HealthCare360.Modules.Interop.Search;

/// <summary>
/// A token search value: <c>code</c> (any system), <c>system|code</c>, <c>|code</c> (no system) or <c>system|</c> (any
/// code of the system). Codes compare case-insensitively (ICD-10 "e11.9" finds "E11.9"); systems compare exactly.
/// </summary>
/// <param name="System">null = any system; empty = only codes without a system.</param>
/// <param name="Code">null = any code of <paramref name="System"/>.</param>
internal sealed record TokenValue(string? System, string? Code)
{
    public static TokenValue Parse(string raw)
    {
        var separator = IndexOfUnescaped(raw, '|');
        if (separator < 0)
        {
            return new TokenValue(null, Unescape(raw.Trim()));
        }

        var system = Unescape(raw[..separator].Trim());
        var code = Unescape(raw[(separator + 1)..].Trim());
        return new TokenValue(system, code.Length == 0 ? null : code);
    }

    public bool Matches(string? system, string? code)
    {
        if (Code is not null && !string.Equals(Code, code, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return System switch
        {
            null => true,
            "" => string.IsNullOrEmpty(system),
            _ => string.Equals(System, system, StringComparison.Ordinal),
        };
    }

    /// <summary>Splits a parameter value on unescaped commas (FHIR "OR").</summary>
    public static IReadOnlyList<string> SplitOr(string value)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
                continue;
            }

            if (value[i] == ',')
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }

        parts.Add(value[start..]);
        return parts.Where(p => p.Trim().Length > 0).ToList();
    }

    /// <summary>Removes FHIR search escapes (<c>\,</c> <c>\|</c> <c>\$</c> <c>\\</c>).</summary>
    public static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
            }

            text.Append(value[i]);
        }

        return text.ToString();
    }

    private static int IndexOfUnescaped(string value, char target)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
                continue;
            }

            if (value[i] == target)
            {
                return i;
            }
        }

        return -1;
    }
}
