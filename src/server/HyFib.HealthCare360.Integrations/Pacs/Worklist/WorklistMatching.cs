using System.Globalization;

namespace HyFib.HealthCare360.Integrations.Pacs.Worklist;

/// <summary>
/// The matching keys of a modality worklist query (DICOM PS3.4 K.6.1.2.2), as the modality sent them. A null key, an
/// empty one or a lone <c>*</c> matches everything (universal matching).
/// </summary>
/// <param name="Dates">Scheduled Procedure Step Start Date: one date or a range.</param>
/// <param name="Times">Scheduled Procedure Step Start Time: one time or a range, applied to each day.</param>
internal sealed record WorklistKeys(
    string? PatientName = null,
    string? PatientId = null,
    string? AccessionNumber = null,
    string? RequestedProcedureId = null,
    string? Modality = null,
    string? StationAeTitle = null,
    DicomRange? Dates = null,
    DicomRange? Times = null)
{
    public static WorklistKeys Any { get; } = new();
}

/// <summary>A DICOM range (<c>from-to</c>, <c>from-</c>, <c>-to</c> or one value), with inclusive bounds as comparable text.</summary>
internal sealed record DicomRange(string? From, string? To)
{
    /// <summary>A DA value or range (<c>yyyyMMdd</c>); null for universal matching or a value that is not a date.</summary>
    public static DicomRange? ParseDate(string? value) => Parse(value, ParseDay);

    /// <summary>A TM value or range (<c>HH[MM[SS[.F]]]</c>, colons tolerated); the upper bound covers the whole minute or hour given.</summary>
    public static DicomRange? ParseTime(string? value) => Parse(value, ParseClock);

    public bool Contains(string value) =>
        (From is null || string.CompareOrdinal(value, From) >= 0) && (To is null || string.CompareOrdinal(value, To) <= 0);

    /// <summary>The dates of a DA range as calendar days; open ends stay open.</summary>
    public (DateOnly? From, DateOnly? To) Days() => (Day(From), Day(To));

    private static DateOnly? Day(string? value) =>
        value is not null && DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static DicomRange? Parse(string? value, Func<string, bool, string?> part)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text == "*")
        {
            return null;
        }

        var dash = text.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            var single = part(text, false);
            var upper = part(text, true);
            return single is null || upper is null ? null : new DicomRange(single, upper);
        }

        var from = dash == 0 ? null : part(text[..dash].Trim(), false);
        var to = dash == text.Length - 1 ? null : part(text[(dash + 1)..].Trim(), true);
        if ((dash > 0 && from is null) || (dash < text.Length - 1 && to is null) || (from is null && to is null))
        {
            return null;
        }

        return new DicomRange(from, to);
    }

    private static string? ParseDay(string text, bool upper) =>
        text.Length == 8 && text.All(char.IsAsciiDigit) && DateOnly.TryParseExact(text, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? text
            : null;

    private static string? ParseClock(string text, bool upper)
    {
        var digits = text.Split('.')[0].Replace(":", string.Empty, StringComparison.Ordinal);
        if (digits.Length is not (2 or 4 or 6) || !digits.All(char.IsAsciiDigit)
            || int.Parse(digits.AsSpan(0, 2), CultureInfo.InvariantCulture) > 23
            || (digits.Length >= 4 && int.Parse(digits.AsSpan(2, 2), CultureInfo.InvariantCulture) > 59)
            || (digits.Length == 6 && int.Parse(digits.AsSpan(4, 2), CultureInfo.InvariantCulture) > 59))
        {
            return null;
        }

        return digits.PadRight(6, upper ? '9' : '0') switch
        {
            // "12" up to 12:59:59 and "1230" up to 12:30:59: a padded "99" is the end of that minute or hour.
            var padded when upper => padded[..2] + Math.Min(int.Parse(padded.AsSpan(2, 2), CultureInfo.InvariantCulture), 59).ToString("00", CultureInfo.InvariantCulture)
                                     + Math.Min(int.Parse(padded.AsSpan(4, 2), CultureInfo.InvariantCulture), 59).ToString("00", CultureInfo.InvariantCulture),
            var padded => padded,
        };
    }
}

/// <summary>DICOM wildcard matching: <c>*</c> stands for any run of characters, <c>?</c> for one character.</summary>
internal static class DicomWildcard
{
    /// <summary>Longest pattern considered; longer keys match nothing (DICOM values are at most 64 characters).</summary>
    public const int MaxPatternLength = 64;

    public static bool IsUniversal(string? pattern) => string.IsNullOrWhiteSpace(pattern) || pattern.Trim().All(c => c == '*');

    public static bool IsMatch(string? pattern, string? value, bool ignoreCase = true)
    {
        if (IsUniversal(pattern))
        {
            return true;
        }

        var p = pattern!.Trim();
        if (p.Length > MaxPatternLength)
        {
            return false;
        }

        var v = (value ?? string.Empty).Trim();
        if (ignoreCase)
        {
            p = p.ToUpperInvariant();
            v = v.ToUpperInvariant();
        }

        // Linear-time matching with one backtrack point per star (no regular expressions, so no catastrophic patterns).
        int pi = 0, vi = 0, star = -1, mark = 0;
        while (vi < v.Length)
        {
            if (pi < p.Length && (p[pi] == '?' || p[pi] == v[vi]))
            {
                pi++;
                vi++;
            }
            else if (pi < p.Length && p[pi] == '*')
            {
                star = pi++;
                mark = vi;
            }
            else if (star >= 0)
            {
                pi = star + 1;
                vi = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (pi < p.Length && p[pi] == '*')
        {
            pi++;
        }

        return pi == p.Length;
    }
}

/// <summary>Which worklist entries answer a query, and how they are presented (pure; unit-tested).</summary>
internal static class WorklistMatcher
{
    /// <summary>Projection radiography: a CR reader and a DR room do the same X-ray work.</summary>
    private static readonly HashSet<string> Projection = new(StringComparer.OrdinalIgnoreCase) { "CR", "DX" };

    /// <summary>
    /// True when the entry answers every key: names and identifiers with wildcards (names without regard to case, and a
    /// family name alone finds the person), the modality (CR and DX stand for each other), the station (an entry with no
    /// station assigned is offered to every station), and the start date and time ranges.
    /// </summary>
    public static bool Matches(ModalityWorklistItem item, WorklistKeys keys) =>
        NameMatches(keys.PatientName, item.PatientName)
        && DicomWildcard.IsMatch(keys.PatientId, item.PatientId)
        && DicomWildcard.IsMatch(keys.AccessionNumber, item.AccessionFitsDicom ? item.AccessionNumber : string.Empty)
        && DicomWildcard.IsMatch(keys.RequestedProcedureId, item.RequestedProcedureId)
        && ModalityMatches(keys.Modality, item.Modality)
        && (DicomWildcard.IsUniversal(keys.StationAeTitle) || item.ScheduledStationAeTitle is null
            || DicomWildcard.IsMatch(keys.StationAeTitle, item.ScheduledStationAeTitle))
        && (keys.Dates is null || keys.Dates.Contains(item.ScheduledStepStartDate))
        && (keys.Times is null || keys.Times.Contains(item.ScheduledStepStartTime));

    public static bool ModalityMatches(string? requested, string itemModality)
    {
        if (DicomWildcard.IsUniversal(requested))
        {
            return true;
        }

        var code = requested!.Trim();
        return string.Equals(code, itemModality, StringComparison.OrdinalIgnoreCase)
               || (Projection.Contains(code) && Projection.Contains(itemModality));
    }

    /// <summary>
    /// The modality code an entry is sent with: X-ray work goes as the code the modality asked for, or as the X-ray code
    /// it is set up for, so a DR room sees DX and a CR reader sees CR; other entries keep their own code.
    /// </summary>
    public static string PresentedModality(string itemModality, string? requested, IReadOnlyList<string> allowed)
    {
        if (!Projection.Contains(itemModality))
        {
            return itemModality;
        }

        if (!DicomWildcard.IsUniversal(requested) && Projection.Contains(requested!.Trim()))
        {
            return requested.Trim().ToUpperInvariant();
        }

        return allowed.FirstOrDefault(Projection.Contains) ?? itemModality;
    }

    /// <summary>
    /// Person-name matching: wildcards as usual, without regard to case and ignoring empty trailing components
    /// ("SHARMA^RAVI^^" is "SHARMA^RAVI"). A key without components or wildcards also finds the family name alone, as
    /// staff type "SHARMA" at the console.
    /// </summary>
    public static bool NameMatches(string? pattern, string name)
    {
        if (DicomWildcard.IsUniversal(pattern))
        {
            return true;
        }

        var key = pattern!.Split('=')[0].Trim().TrimEnd('^');
        var value = name.Split('=')[0].Trim().TrimEnd('^');
        if (DicomWildcard.IsMatch(key, value))
        {
            return true;
        }

        return !key.Contains('^', StringComparison.Ordinal) && !key.Contains('*', StringComparison.Ordinal) && !key.Contains('?', StringComparison.Ordinal)
               && DicomWildcard.IsMatch(key, value.Split('^')[0]);
    }
}
