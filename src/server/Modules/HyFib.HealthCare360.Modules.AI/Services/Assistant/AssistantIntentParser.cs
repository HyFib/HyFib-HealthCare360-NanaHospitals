using System.Globalization;
using System.Text.RegularExpressions;

namespace HyFib.HealthCare360.Modules.AI.Services.Assistant;

internal static class AssistantIntents
{
    public const string Navigate = "navigate";
    public const string SearchPatient = "search_patient";
    public const string OpenPatient = "open_patient";
    public const string ListAppointments = "list_appointments";
    public const string BookFollowUp = "book_follow_up";
    public const string CreateNote = "create_note";
    public const string StartDictation = "start_dictation";
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [Navigate, SearchPatient, OpenPatient, ListAppointments, BookFollowUp, CreateNote, StartDictation, Unknown];
}

/// <summary>An interpreted command. Parameters are strings so the UI can drop them straight into routes and forms.</summary>
internal sealed record ParsedIntent(string Intent, IReadOnlyDictionary<string, string> Parameters, string ConfirmationText, bool RequiresConfirmation);

/// <summary>
/// Deterministic, offline command parser (AIV-01..03). It understands the everyday commands — "open patient UHID
/// NH26000123", "today's appointments", "book follow-up in 2 weeks", "start dictation", "go to billing" — without any AI
/// provider, so the assistant keeps working when AI is off or the internet is down. The LLM is consulted only for
/// commands these rules do not recognise. The <c>For*</c> builders are shared with the AI path so confirmations read
/// the same whichever understood the command.
/// </summary>
internal static partial class AssistantIntentParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static ParsedIntent Parse(string text, Guid? contextPatientId, DateOnly today)
    {
        var command = Normalize(text);
        if (command.Length == 0)
        {
            return ForUnknown();
        }

        return TryDictation(command, contextPatientId)
               ?? TryNote(command, contextPatientId)
               ?? TryFollowUp(command, contextPatientId, today)
               ?? TryAppointments(command, today)
               ?? TryPatient(command, contextPatientId)
               ?? TryNavigation(command)
               ?? ForUnknown();
    }

    /// <summary>Collapses whitespace and strips wake words, politeness and trailing punctuation ("Hey HC360, … please.").</summary>
    internal static string Normalize(string text)
    {
        var command = Whitespace().Replace(text, " ").Trim().TrimEnd('.', '!', '?', ',', ';', ':').Trim();
        string previous;
        do
        {
            previous = command;
            command = PolitePrefix().Replace(command, string.Empty).Trim();
            command = PoliteSuffix().Replace(command, string.Empty).Trim().TrimEnd('.', '!', '?', ',').Trim();
        }
        while (command.Length > 0 && command != previous);

        return command;
    }

    // ---------------------------------------------------------------- rules

    private static ParsedIntent? TryDictation(string command, Guid? patientId) => Dictation().IsMatch(command) ? ForDictation(patientId) : null;

    private static ParsedIntent? TryNote(string command, Guid? patientId)
    {
        var match = NoteCommand().Match(command);
        if (!match.Success)
        {
            match = NotePrefix().Match(command);
        }

        if (!match.Success)
        {
            return null;
        }

        var type = match.Groups["type"].Success ? match.Groups["type"].Value.ToLowerInvariant() : null;
        var text = match.Groups["text"].Success ? match.Groups["text"].Value.Trim() : null;
        return ForNote(type, string.IsNullOrEmpty(text) ? null : text, patientId);
    }

    private static ParsedIntent? TryFollowUp(string command, Guid? patientId, DateOnly today)
    {
        var interval = ParseInterval(command, today);
        var followUp = FollowUpWord().IsMatch(command);
        var explicitInterval = Interval().IsMatch(command) || NextUnit().IsMatch(command) || Fortnight().IsMatch(command);
        if (followUp ? interval is null && !BookingVerb().IsMatch(command) : !explicitInterval || interval is null || !Review().IsMatch(command))
        {
            // "follow-up list" is not a booking; "review" is only a follow-up with an explicit interval
            // ("review after 2 weeks"), never "review tomorrow's appointments".
            return null;
        }

        return ForFollowUp(interval?.Days, interval?.Label, today, patientId);
    }

    private static ParsedIntent? TryAppointments(string command, DateOnly today)
    {
        if (!AppointmentWord().IsMatch(command) || Book().IsMatch(command))
        {
            return null;
        }

        var date = ParseDate(command, today);
        if (date is null && !ListVerb().IsMatch(command))
        {
            return null;
        }

        var scope = MyWord().IsMatch(command) ? "mine" : AllWord().IsMatch(command) ? "all" : null;
        return ForAppointments(date ?? today, today, scope);
    }

    private static ParsedIntent? TryPatient(string command, Guid? patientId)
    {
        var keyword = UhidKeyword().Match(command);
        if (keyword.Success)
        {
            return ForOpenPatient(CompactId(keyword.Groups["id"].Value));
        }

        var format = UhidFormat().Match(command);
        if (format.Success)
        {
            return ForOpenPatient(format.Value.ToUpperInvariant());
        }

        var open = OpenPatientCommand().Match(command);
        if (open.Success)
        {
            var rest = open.Groups["rest"].Value.Trim();
            if (rest.Length == 0)
            {
                return patientId is { } id ? ForOpenCurrentPatient(id) : ForSearch(null, null, openIfSingleMatch: false);
            }

            // "open patient list" / "open patient registration" are screens, not a patient.
            return rest.ToLowerInvariant() is "list" or "search" or "registration" or "master" or "queue"
                ? null
                : MobileNumber(rest) is { } phone ? ForSearch(null, phone, openIfSingleMatch: true) : ForSearch(CleanName(rest), null, openIfSingleMatch: true);
        }

        var search = SearchPatientCommand().Match(command);
        if (search.Success)
        {
            var rest = search.Groups["rest"].Value.Trim();
            return MobileNumber(rest) is { } phone ? ForSearch(null, phone, openIfSingleMatch: false) : ForSearch(CleanName(rest), null, openIfSingleMatch: false);
        }

        return BareMobile().IsMatch(command) && MobileNumber(command) is { } bare ? ForSearch(null, bare, openIfSingleMatch: false) : null;
    }

    private static ParsedIntent? TryNavigation(string command)
    {
        var match = NavigateCommand().Match(command);
        var target = (match.Success ? NavigationTargets.Find(match.Groups["target"].Value) : null) ?? NavigationTargets.Find(command);
        return target is null ? null : ForNavigation(target);
    }

    // ---------------------------------------------------------------- dates and intervals

    internal static (int Days, string Label)? ParseInterval(string command, DateOnly today)
    {
        var match = Interval().Match(command);
        if (match.Success && SpokenNumbers.TryParse(match.Groups["n"].Value, out var count) && count > 0)
        {
            var unit = match.Groups["unit"].Value.ToLowerInvariant();
            var days = unit.StartsWith("day", StringComparison.Ordinal) ? count
                : unit.StartsWith("week", StringComparison.Ordinal) ? count * 7
                : count <= 24 ? today.AddMonths(count).DayNumber - today.DayNumber : int.MaxValue;
            var label = Plural(count, unit.StartsWith("day", StringComparison.Ordinal) ? "day" : unit.StartsWith("week", StringComparison.Ordinal) ? "week" : "month");
            return days is > 0 and <= 365 ? (days, label) : null;
        }

        var next = NextUnit().Match(command);
        if (next.Success)
        {
            return next.Groups["unit"].Value.Equals("week", StringComparison.OrdinalIgnoreCase)
                ? (7, "1 week")
                : (today.AddMonths(1).DayNumber - today.DayNumber, "1 month");
        }

        if (Fortnight().IsMatch(command))
        {
            return (14, "2 weeks");
        }

        if (DayAfterTomorrow().IsMatch(command))
        {
            return (2, "2 days");
        }

        return Tomorrow().IsMatch(command) ? (1, "1 day") : null;
    }

    /// <summary>Spoken dates, interpreted in the hospital's calendar (Indian day/month order for numeric dates).</summary>
    internal static DateOnly? ParseDate(string command, DateOnly today)
    {
        if (DayAfterTomorrow().IsMatch(command))
        {
            return today.AddDays(2);
        }

        if (Tomorrow().IsMatch(command))
        {
            return today.AddDays(1);
        }

        if (Yesterday().IsMatch(command))
        {
            return today.AddDays(-1);
        }

        if (TodayWord().IsMatch(command))
        {
            return today;
        }

        var weekday = Weekday().Match(command);
        if (weekday.Success && Enum.TryParse<DayOfWeek>(weekday.Groups["day"].Value, ignoreCase: true, out var day))
        {
            var ahead = ((int)day - (int)today.DayOfWeek + 7) % 7;
            return today.AddDays(ahead == 0 && weekday.Groups["next"].Success ? 7 : ahead);
        }

        var numeric = NumericDate().Match(command);
        if (numeric.Success && TryDate(numeric.Groups["d"].Value, int.Parse(numeric.Groups["m"].Value, Invariant), numeric.Groups["y"].Value, today, out var date))
        {
            return date;
        }

        var named = NamedDate().Match(command);
        if (named.Success)
        {
            var month = Array.IndexOf(MonthPrefixes, named.Groups["mon"].Value.ToLowerInvariant()[..3]) + 1;
            if (month > 0 && TryDate(named.Groups["d"].Value, month, named.Groups["y"].Value, today, out var namedDate))
            {
                return namedDate;
            }
        }

        return null;
    }

    public static string DescribeDays(int days) => days % 7 == 0 ? Plural(days / 7, "week") : Plural(days, "day");

    private static readonly string[] MonthPrefixes = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static bool TryDate(string dayText, int month, string yearText, DateOnly today, out DateOnly date)
    {
        date = default;
        var year = yearText.Length switch
        {
            0 => today.Year,
            2 => 2000 + int.Parse(yearText, Invariant),
            _ => int.Parse(yearText, Invariant),
        };
        var day = int.Parse(dayText, Invariant);
        if (month is < 1 or > 12 || year is < 2000 or > 2100 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    private static string Plural(int count, string unit) => count == 1 ? $"1 {unit}" : string.Create(Invariant, $"{count} {unit}s");

    private static string CompactId(string value) => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string? MobileNumber(string text)
    {
        var match = Mobile().Match(text);
        return match.Success ? SharedKernel.Phone.Normalize(new string(match.Groups["number"].Value.Where(char.IsAsciiDigit).ToArray())) : null;
    }

    private static string? CleanName(string text)
    {
        var cleaned = text.Trim().Trim('"', '\'', '“', '”', '.', ',');
        return cleaned.Length == 0 ? null : cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    // ---------------------------------------------------------------- builders (shared with the AI interpreter)

    public static ParsedIntent ForOpenPatient(string uhid) =>
        new(AssistantIntents.OpenPatient, Params(("uhid", uhid)), $"Opening patient {uhid}.", false);

    public static ParsedIntent ForOpenCurrentPatient(Guid patientId) =>
        new(AssistantIntents.OpenPatient, Params(("patientId", patientId.ToString())), "Opening the current patient's record.", false);

    public static ParsedIntent ForSearch(string? query, string? phone, bool openIfSingleMatch) => new(
        AssistantIntents.SearchPatient,
        Params(("query", query), ("phone", phone), ("openIfSingleMatch", openIfSingleMatch ? "true" : null)),
        phone is not null ? $"Searching patients with mobile number {phone}."
        : query is not null ? $"Searching patients for “{query}”."
        : "Which patient? Say the UHID, mobile number or name.",
        false);

    public static ParsedIntent ForAppointments(DateOnly date, DateOnly today, string? scope)
    {
        var when = date == today ? "today's appointments"
            : date == today.AddDays(1) ? "tomorrow's appointments"
            : date == today.AddDays(-1) ? "yesterday's appointments"
            : "appointments for " + date.ToString("ddd, d MMM yyyy", Invariant);
        return new ParsedIntent(AssistantIntents.ListAppointments,
            Params(("date", date.ToString("yyyy-MM-dd", Invariant)), ("scope", scope)), $"Showing {when}.", false);
    }

    public static ParsedIntent ForFollowUp(int? days, string? label, DateOnly today, Guid? patientId)
    {
        var patientHint = patientId is null ? " Open the patient first." : string.Empty;
        if (days is not { } value)
        {
            return new ParsedIntent(AssistantIntents.BookFollowUp, Params(("patientId", patientId?.ToString())),
                "When should the follow-up be? For example “follow-up in 2 weeks”." + patientHint, true);
        }

        var date = today.AddDays(value);
        return new ParsedIntent(AssistantIntents.BookFollowUp,
            Params(("days", value.ToString(Invariant)), ("date", date.ToString("yyyy-MM-dd", Invariant)), ("interval", label ?? DescribeDays(value)),
                ("patientId", patientId?.ToString())),
            $"Book a follow-up in {label ?? DescribeDays(value)}, on {date.ToString("ddd, d MMM yyyy", Invariant)}?" + patientHint,
            true);
    }

    public static ParsedIntent ForNote(string? noteType, string? text, Guid? patientId) => new(
        AssistantIntents.CreateNote,
        Params(("noteType", noteType), ("text", text), ("patientId", patientId?.ToString())),
        $"Create a {noteType ?? "clinical"} note?" + (patientId is null ? " Open the patient first." : string.Empty),
        true);

    public static ParsedIntent ForDictation(Guid? patientId) =>
        new(AssistantIntents.StartDictation, Params(("patientId", patientId?.ToString())), "Starting dictation.", false);

    public static ParsedIntent ForNavigation(NavigationTarget target) =>
        new(AssistantIntents.Navigate, Params(("route", target.Route), ("label", target.Label)), $"Opening {target.Label}.", false);

    public static ParsedIntent ForUnknown() => new(AssistantIntents.Unknown, Params(),
        "Sorry, I did not understand. Try “open patient UHID NH26000123”, “today's appointments” or “book a follow-up in 2 weeks”.", false);

    private static IReadOnlyDictionary<string, string> Params(params (string Key, string? Value)[] values) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v.Value)).ToDictionary(v => v.Key, v => v.Value!.Trim(), StringComparer.Ordinal);

    // ---------------------------------------------------------------- patterns

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?:(?:hey|hi|hello|ok|okay)[,!]?\s+)?(?:hc\s?360|assistant)[,:!]?\s+|^(?:hey|hi|hello|ok|okay)[,!]?\s+|^(?:please|kindly|can\s+you|could\s+you|would\s+you|will\s+you|i\s+want\s+to|i'?d\s+like\s+to|i\s+would\s+like\s+to|let'?s|go\s+ahead\s+and|just)\s+", Options)]
    private static partial Regex PolitePrefix();

    [GeneratedRegex(@"[,\s]+(?:please|thanks|thank\s+you|now|for\s+me)$", Options)]
    private static partial Regex PoliteSuffix();

    [GeneratedRegex(@"^(?:start|begin|take|open|launch|enable|turn\s+on)\s+(?:(?:a|the|voice)\s+)*dictation\b|^dictat(?:e|ion)\b|\bstart\s+dictating\b", Options)]
    private static partial Regex Dictation();

    [GeneratedRegex(@"^(?:create|add|write|make|start|new|open|record|take)\s+(?:(?:a|an|the)\s+)?(?:new\s+)?(?:(?<type>clinical|progress|consultation|soap|nursing|case|procedure|referral|discharge|operative)\s+)?notes?\b(?:[^:\-]*?(?:[:\-]|\bsaying\b|\bthat\s+says\b|\bstating\b)\s*(?<text>.+))?", Options)]
    private static partial Regex NoteCommand();

    [GeneratedRegex(@"^notes?\s*[:\-]\s*(?<text>.+)$", Options)]
    private static partial Regex NotePrefix();

    [GeneratedRegex(@"\b(?:follow[\s-]?ups?|followups?|re-?visit|recall\s+visit|come\s+back|next\s+visit)\b", Options)]
    private static partial Regex FollowUpWord();

    [GeneratedRegex(@"\breview\b", Options)]
    private static partial Regex Review();

    [GeneratedRegex(@"\b(?:book|schedule|fix|set|arrange|plan|add|create|give|keep)\b", Options)]
    private static partial Regex BookingVerb();

    [GeneratedRegex(@"\b(?:in|after|within)\s+(?:the\s+)?(?:next\s+)?(?<n>\d{1,3}|[a-z]+(?:\s+[a-z]+){0,2}?)\s+(?<unit>days?|weeks?|months?)\b", Options)]
    private static partial Regex Interval();

    [GeneratedRegex(@"\bnext\s+(?<unit>week|month)\b", Options)]
    private static partial Regex NextUnit();

    [GeneratedRegex(@"\bfortnight\b", Options)]
    private static partial Regex Fortnight();

    [GeneratedRegex(@"\bday\s+after\s+tomorrow\b", Options)]
    private static partial Regex DayAfterTomorrow();

    [GeneratedRegex(@"\btomorrow(?:'?s)?\b", Options)]
    private static partial Regex Tomorrow();

    [GeneratedRegex(@"\byesterday(?:'?s)?\b", Options)]
    private static partial Regex Yesterday();

    [GeneratedRegex(@"\btoday(?:'?s)?\b", Options)]
    private static partial Regex TodayWord();

    [GeneratedRegex(@"\b(?<next>next\s+)?(?:on\s+|this\s+)?(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b", Options)]
    private static partial Regex Weekday();

    [GeneratedRegex(@"\b(?<d>\d{1,2})[/.-](?<m>\d{1,2})(?:[/.-](?<y>\d{4}|\d{2}))?\b", Options)]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"\b(?<d>\d{1,2})(?:st|nd|rd|th)?\s+(?:of\s+)?(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?(?:\s+(?<y>\d{4}))?\b", Options)]
    private static partial Regex NamedDate();

    [GeneratedRegex(@"\b(?:appointments?|appts?|schedule|clinic\s+list|opd\s+list)\b", Options)]
    private static partial Regex AppointmentWord();

    [GeneratedRegex(@"\bbook\b", Options)]
    private static partial Regex Book();

    [GeneratedRegex(@"\b(?:show|list|display|view|what|what's|whats|how\s+many|any|my|get|check|see|who)\b", Options)]
    private static partial Regex ListVerb();

    [GeneratedRegex(@"\bmy\b", Options)]
    private static partial Regex MyWord();

    [GeneratedRegex(@"\ball\b", Options)]
    private static partial Regex AllWord();

    [GeneratedRegex(@"\b(?:u\.?\s?h\.?\s?i\.?\s?d\.?|patient\s+(?:id|number|no\.?)|hospital\s+(?:id|number|no\.?)|registration\s+(?:number|no\.?)|mrn)\s*(?:number|no\.?|#)?\s*[:\-]?\s*(?<id>[a-z]{0,6}[\s-]?\d(?:[\s-]?\d){1,14})", Options)]
    private static partial Regex UhidKeyword();

    [GeneratedRegex(@"\b[a-z]{2,6}\d{8}\b", Options)]
    private static partial Regex UhidFormat();

    [GeneratedRegex(@"^(?:open|show|pull\s+up|load|bring\s+up|go\s+to|display|view|get)\s+(?:the\s+)?(?:patient|chart|record|emr|file|case\s*sheet|profile)(?:'?s)?(?:\s+(?:of|for|named|called))?\s*(?<rest>.*)$", Options)]
    private static partial Regex OpenPatientCommand();

    [GeneratedRegex(@"^(?:search|find|look\s*up|lookup|locate)\s+(?:for\s+)?(?:an?\s+|the\s+)?(?:patients?\s*)?(?:named\s+|called\s+|with\s+(?:the\s+)?(?:name|mobile|phone)(?:\s+(?:number|no\.?))?\s+)?(?<rest>.+)$", Options)]
    private static partial Regex SearchPatientCommand();

    [GeneratedRegex(@"(?<!\d)(?:\+?91[\s-]?|0)?(?<number>[6-9](?:[\s-]?\d){9})(?!\d)", Options)]
    private static partial Regex Mobile();

    [GeneratedRegex(@"^(?:(?:open|show|find|search|patient|mobile|phone|number|with|for)\s+)*(?:\+?91[\s-]?|0)?[6-9](?:[\s-]?\d){9}$", Options)]
    private static partial Regex BareMobile();

    [GeneratedRegex(@"^(?:go\s+to|goto|open|show(?:\s+me)?|navigate\s+to|take\s+me\s+to|switch\s+to|display|launch|view|bring\s+up)\s+(?:the\s+)?(?<target>.+?)$", Options)]
    private static partial Regex NavigateCommand();
}
