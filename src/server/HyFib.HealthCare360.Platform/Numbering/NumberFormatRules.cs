using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Platform.Numbering;

/// <summary>What an administrator asks to change in one series, and what the rules need to know about the hospital.</summary>
public sealed record NumberFormatChange
{
    public required NumberSeriesDefinition Series { get; init; }

    public required string Format { get; init; }

    public required NumberResetPolicy ResetPolicy { get; init; }

    /// <summary>The next number to issue, when the administrator sets it.</summary>
    public long? StartAt { get; init; }

    /// <summary>The format in use now (the default until the series is first changed).</summary>
    public required string CurrentFormat { get; init; }

    public required NumberResetPolicy CurrentResetPolicy { get; init; }

    /// <summary>The last number issued in the current period (0 if none yet).</summary>
    public long LastNumber { get; init; }

    public int FacilityCount { get; init; } = 1;

    /// <summary>The length of the longest facility code in use ({FAC} is at most 6 letters).</summary>
    public int LongestFacilityCode { get; init; } = NumberFormatRules.MaxFacilityCodeLength;

    /// <summary>The formats of the other series, which a changed format must differ from.</summary>
    public IReadOnlyCollection<OtherNumberFormat> OtherFormats { get; init; } = [];

    /// <summary>
    /// The facility code this series' numbers carry for {FAC}: the facility's own for a per-facility series, the main
    /// facility's for a shared one. Null when unknown, and {FAC} is then compared as written.
    /// </summary>
    public string? FacilityCode { get; init; }
}

/// <summary>
/// Another series' format now, whether that is the series' own default, and the facility code its numbers carry for
/// {FAC} (null when unknown).
/// </summary>
public sealed record OtherNumberFormat(string Name, string Format, bool IsDefault, string? FacilityCode = null);

/// <summary>How long the numbers of a format can get, part by part.</summary>
public sealed record NumberFormatLength(int FacilityCode, int Date, int RunningNumber, int FixedText)
{
    public int Total => FacilityCode + Date + RunningNumber + FixedText;
}

/// <summary>
/// The rules a number format must follow before an administrator may save it, with messages written for them. The staff
/// app checks the same rules as the administrator types (<c>modules/numbering/numberFormat.ts</c>); keep the two alike.
/// </summary>
public static class NumberFormatRules
{
    public const string FormatField = "format";
    public const string ResetPolicyField = "resetPolicy";
    public const string StartAtField = "startAt";

    /// <summary>The longest format the database keeps.</summary>
    public const int FormatColumnLength = 64;

    public const int MaxSequenceWidth = 12;

    /// <summary>A running number is measured as at least this many digits: it grows past its padding over the years.</summary>
    public const int AssumedSequenceDigits = 8;

    public const int MaxFacilityCodeLength = 6;

    /// <summary>What is wrong with how a format is written, most important first (nothing when it is well formed).</summary>
    public static IReadOnlyList<string> FormatProblems(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return ["Enter a format, for example {FAC}{YY}{SEQ:6}."];
        }

        var problems = new List<string>();
        if (format.Length > FormatColumnLength)
        {
            problems.Add($"Use at most {FormatColumnLength} characters.");
        }

        var parts = NumberFormat.Parse(format);
        if (parts.Any(p => p.Kind == NumberFormatPartKind.Unclosed))
        {
            problems.Add("A { has no closing }. Parts in braces look like {YY} or {SEQ:6}.");
        }

        var unknown = parts.Where(p => p.Kind == NumberFormatPartKind.Unknown).Select(p => p.Text).Distinct(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            problems.Add($"{JoinWords(unknown, "and")} {(unknown.Count == 1 ? "is not a part" : "are not parts")} a number can have. " +
                         "Use {FAC}, {YYYY}, {YY}, {MM}, {DD} and {SEQ:n}.");
        }

        var notAllowed = parts.Where(p => p.Kind == NumberFormatPartKind.Text).SelectMany(p => p.Text).Where(c => !IsFixedTextCharacter(c)).ToList();
        if (notAllowed.Count > 0)
        {
            problems.Add($"Outside the braces use only capital letters A to Z, digits, - and /. Not allowed: {DescribeCharacters(notAllowed)}.");
        }

        var sequences = parts.Where(p => p.Kind == NumberFormatPartKind.Sequence).ToList();
        if (sequences.Count == 0)
        {
            problems.Add("Add {SEQ} or {SEQ:n} for the running number.");
        }
        else if (sequences.Count > 1)
        {
            problems.Add("Use the running number {SEQ} only once.");
        }

        if (sequences.Any(s => s.Width is < 1 or > MaxSequenceWidth))
        {
            problems.Add($"In {{SEQ:n}}, n is the number of digits and must be from 1 to {MaxSequenceWidth}.");
        }

        return problems;
    }

    /// <summary>
    /// The longest number <paramref name="format"/> can make: the facility code at <paramref name="facilityCodeLength"/>
    /// letters, the date parts, the running number at its padding or <see cref="AssumedSequenceDigits"/> digits, whichever
    /// is more, and the fixed text.
    /// </summary>
    public static NumberFormatLength LongestNumber(string format, int facilityCodeLength)
    {
        int facility = 0, date = 0, sequence = 0, text = 0;
        foreach (var part in NumberFormat.Parse(format))
        {
            switch (part.Kind)
            {
                case NumberFormatPartKind.FacilityCode:
                    facility += facilityCodeLength;
                    break;
                case NumberFormatPartKind.Year:
                    date += 4;
                    break;
                case NumberFormatPartKind.ShortYear or NumberFormatPartKind.Month or NumberFormatPartKind.Day:
                    date += 2;
                    break;
                case NumberFormatPartKind.Sequence:
                    sequence += Math.Max(part.Width ?? 0, AssumedSequenceDigits);
                    break;
                default:
                    text += part.Text.Length;
                    break;
            }
        }

        return new NumberFormatLength(facility, date, sequence, text);
    }

    /// <summary>
    /// Why numbers that restart with <paramref name="policy"/> cannot use <paramref name="format"/>: without the date parts
    /// of the period, the same numbers would be issued again after the restart. Null when they can.
    /// </summary>
    public static string? ResetProblem(string format, NumberResetPolicy policy)
    {
        var kinds = NumberFormat.Parse(format).Select(p => p.Kind).ToHashSet();
        var year = kinds.Contains(NumberFormatPartKind.Year) || kinds.Contains(NumberFormatPartKind.ShortYear);
        var month = kinds.Contains(NumberFormatPartKind.Month);
        var day = kinds.Contains(NumberFormatPartKind.Day);
        return policy switch
        {
            NumberResetPolicy.Yearly when !year =>
                "Numbers that start again every year must contain the year ({YY} or {YYYY}), or numbers from earlier years would be issued again.",
            NumberResetPolicy.Monthly when !(year && month) =>
                "Numbers that start again every month must contain the year and the month ({YY} or {YYYY}, and {MM}), or earlier numbers would be issued again.",
            NumberResetPolicy.Daily when !(year && month && day) =>
                "Numbers that start again every day must contain the year, the month and the day ({YY} or {YYYY}, {MM} and {DD}), or earlier numbers would be issued again.",
            _ => null,
        };
    }

    /// <summary>
    /// How the numbers of <paramref name="format"/> read with every run of digits (the date parts, the running number and
    /// digits in the fixed text) taken as one place, "#": {FAC}OB{YY}{SEQ:7}, {FAC}OB{SEQ:9} and {FAC}OB26{SEQ} all read
    /// "{FAC}OB#". Two formats that read alike can make the same number: once a running number outgrows its padding
    /// ({SEQ:6} reaching 1000000 matches {SEQ:7}), in the year written into one of them, or as {SEQ} and {SEQ:1}. With
    /// <paramref name="facilityCode"/>, {FAC} reads as that code, so {FAC}AB{SEQ} and AB{FAC}{SEQ} both read "ABAB#" at
    /// facility AB; without it, {FAC} stays as written.
    /// </summary>
    public static string Outline(string format, string? facilityCode = null)
    {
        var outline = new StringBuilder();
        void Digits()
        {
            if (outline.Length == 0 || outline[^1] != '#')
            {
                outline.Append('#');
            }
        }

        void Characters(string text)
        {
            foreach (var c in text)
            {
                if (char.IsAsciiDigit(c))
                {
                    Digits();
                }
                else
                {
                    outline.Append(c);
                }
            }
        }

        foreach (var part in NumberFormat.Parse(format))
        {
            switch (part.Kind)
            {
                case NumberFormatPartKind.Year or NumberFormatPartKind.ShortYear or NumberFormatPartKind.Month
                    or NumberFormatPartKind.Day or NumberFormatPartKind.Sequence:
                    Digits();
                    break;
                case NumberFormatPartKind.Text:
                    Characters(part.Text);
                    break;
                case NumberFormatPartKind.FacilityCode when facilityCode is not null:
                    Characters(facilityCode);
                    break;
                default:
                    outline.Append(part.Text);
                    break;
            }
        }

        return outline.ToString();
    }

    /// <summary>
    /// The largest next number <paramref name="format"/> allows: as many digits as the running number is measured with,
    /// or for a locked series its padding, so that its numbers keep the shape other systems read. Null when the format
    /// has no single, valid running number.
    /// </summary>
    public static long? LargestStartAt(NumberSeriesDefinition series, string format)
    {
        var sequences = NumberFormat.Parse(format).Where(p => p.Kind == NumberFormatPartKind.Sequence).ToList();
        if (sequences is not [{ Width: null or (>= 1 and <= MaxSequenceWidth) } sequence])
        {
            return null;
        }

        var digits = series.IsLocked ? sequence.Width ?? AssumedSequenceDigits : Math.Max(sequence.Width ?? 0, AssumedSequenceDigits);
        var largest = 0L;
        for (var digit = 0; digit < digits; digit++)
        {
            largest = (largest * 10) + 9;
        }

        return largest;
    }

    /// <summary>
    /// Everything that stops the change from being saved, keyed by the request field it is about (empty when it can be
    /// saved). The format is checked in full when it or the reset changes; only the next number otherwise. A locked
    /// series' format and reset are refused before these rules (see the endpoint).
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Check(NumberFormatChange change)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages))
            {
                errors[field] = messages = [];
            }

            messages.Add(message);
        }

        var series = change.Series;
        var formatChanged = !string.Equals(change.Format, change.CurrentFormat, StringComparison.Ordinal);
        var resetChanged = change.ResetPolicy != change.CurrentResetPolicy;
        var syntax = FormatProblems(change.Format);
        foreach (var problem in syntax)
        {
            Add(FormatField, problem);
        }

        if (syntax.Count == 0 && (formatChanged || resetChanged))
        {
            if (ResetProblem(change.Format, change.ResetPolicy) is { } resetProblem)
            {
                Add(ResetPolicyField, resetProblem);
            }

            var parts = NumberFormat.Parse(change.Format);
            if (series.PerFacility && change.FacilityCount > 1 && parts.All(p => p.Kind != NumberFormatPartKind.FacilityCode))
            {
                Add(FormatField, "Add {FAC}. Each facility counts its own numbers, so without the facility code two facilities would issue the same numbers.");
            }

            var length = LongestNumber(change.Format, Math.Clamp(change.LongestFacilityCode, 0, MaxFacilityCodeLength));
            if (length.Total > series.MaxLength)
            {
                Add(FormatField, TooLong(series, parts, length));
            }

            // Two series whose numbers read alike, with their facility codes in place of {FAC}, can issue the same numbers
            // (OP and IP bills share a table). Two defaults that look alike were chosen that way and are kept apart by
            // their tables, so restoring a default is always allowed.
            var same = formatChanged
                ? change.OtherFormats.FirstOrDefault(other => Alike(change.Format, change.FacilityCode, other.Format, other.FacilityCode)
                                                              && !(other.IsDefault && string.Equals(change.Format, series.DefaultFormat, StringComparison.Ordinal)))
                : null;
            if (same is not null)
            {
                Add(FormatField, SameNumbersMessage(change, same));
            }
        }

        if (change.StartAt is { } startAt)
        {
            if (startAt < 1)
            {
                Add(StartAtField, "Enter a number of 1 or more.");
            }
            else if (startAt <= change.LastNumber)
            {
                Add(StartAtField, $"Number {change.LastNumber} has already been issued{PeriodWords(change.CurrentResetPolicy)}. Enter a number above {change.LastNumber}.");
            }
            else if (syntax.Count == 0 && LargestStartAt(series, change.Format) is { } largest && startAt > largest)
            {
                Add(StartAtField, series.IsLocked
                    ? $"The next number can be at most {largest.ToString(CultureInfo.InvariantCulture)}, so that these numbers keep the shape other systems read."
                    : $"The next number can have at most {largest.ToString(CultureInfo.InvariantCulture).Length} digits.");
            }
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);
    }

    /// <summary>Why <paramref name="other"/> can make the numbers the change would make, and what to do about it.</summary>
    private static string SameNumbersMessage(NumberFormatChange change, OtherNumberFormat other)
    {
        const string Fix = "Change the letters, - or / so the two kinds of number can be told apart.";
        var withCode = new[] { (change.Format, change.FacilityCode), (other.Format, other.FacilityCode) }
            .Where(f => f.FacilityCode is not null && HasFacilityCode(f.Format))
            .Select(f => f.FacilityCode!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Alike as written, and not because of where a code is filled in: the plainer explanations apply.
        var codesAgree = withCode.Count <= 1 && HasFacilityCode(change.Format) == HasFacilityCode(other.Format);
        if (codesAgree && string.Equals(other.Format, change.Format, StringComparison.Ordinal))
        {
            return $"{other.Name} already uses this format. Change the fixed text so the two kinds of number can be told apart.";
        }

        if (codesAgree && string.Equals(Outline(other.Format), Outline(change.Format), StringComparison.Ordinal))
        {
            return $"{other.Name} uses {other.Format}, which can make the same numbers: the two differ only in digits, dates or the length of the running number. {Fix}";
        }

        var codes = withCode.Count == 1 ? $"code {withCode[0]}" : $"codes {JoinWords(withCode, "and")}";
        return $"{other.Name} uses {other.Format}. With the facility {codes} in place of {{FAC}}, the two can make the same numbers. {Fix}";
    }

    /// <summary>
    /// Whether two formats can make the same numbers: read with their facility codes in place of {FAC}, or as written when
    /// a code they need is unknown.
    /// </summary>
    private static bool Alike(string format, string? facilityCode, string other, string? otherFacilityCode) =>
        (facilityCode is not null || !HasFacilityCode(format)) && (otherFacilityCode is not null || !HasFacilityCode(other))
            ? string.Equals(Outline(format, facilityCode), Outline(other, otherFacilityCode), StringComparison.Ordinal)
            : string.Equals(Outline(format), Outline(other), StringComparison.Ordinal);

    private static bool HasFacilityCode(string format) => NumberFormat.Parse(format).Any(p => p.Kind == NumberFormatPartKind.FacilityCode);

    private static string TooLong(NumberSeriesDefinition series, IReadOnlyList<NumberFormatPart> parts, NumberFormatLength length)
    {
        var pieces = new List<string>();
        if (length.FacilityCode > 0)
        {
            pieces.Add($"facility code {length.FacilityCode}");
        }

        if (length.Date > 0)
        {
            pieces.Add($"date {length.Date}");
        }

        pieces.Add($"running number {length.RunningNumber}");
        if (length.FixedText > 0)
        {
            pieces.Add($"fixed text {length.FixedText}");
        }

        var fixes = new List<string>();
        if (length.FixedText > 0)
        {
            fixes.Add("shorten the fixed text");
        }

        if (parts.Any(p => p.Kind == NumberFormatPartKind.Year))
        {
            fixes.Add("use {YY} instead of {YYYY}");
        }

        if (parts.Any(p => p.Kind == NumberFormatPartKind.Sequence && p.Width > AssumedSequenceDigits))
        {
            fixes.Add("use fewer digits in {SEQ:n}");
        }

        if (!series.PerFacility && length.FacilityCode > 0)
        {
            fixes.Add("leave out {FAC}");
        }

        var fix = fixes.Count == 0 ? "Leave out a part." : char.ToUpperInvariant(fixes[0][0]) + JoinWords(fixes, "or")[1..] + ".";
        return $"Numbers in this format can be up to {length.Total} characters long ({string.Join(", ", pieces)}), and only {series.MaxLength} fit. {fix}";
    }

    private static string PeriodWords(NumberResetPolicy policy) => policy switch
    {
        NumberResetPolicy.Yearly => " this year",
        NumberResetPolicy.Monthly => " this month",
        NumberResetPolicy.Daily => " today",
        _ => string.Empty,
    };

    private static bool IsFixedTextCharacter(char c) => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '-' or '/';

    /// <summary>"small letters, spaces and "."": the characters, in the order first seen, described for a reader.</summary>
    private static string DescribeCharacters(IEnumerable<char> characters)
    {
        var words = new List<string>();
        foreach (var c in characters)
        {
            var word = char.IsAsciiLetterLower(c) ? "small letters" : char.IsWhiteSpace(c) ? "spaces" : $"\"{c}\"";
            if (!words.Contains(word, StringComparer.Ordinal))
            {
                words.Add(word);
            }
        }

        return JoinWords(words, "and");
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string JoinWords(IReadOnlyList<string> words, string conjunction) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        _ => $"{string.Join(", ", words.Take(words.Count - 1))} {conjunction} {words[^1]}",
    };
}
