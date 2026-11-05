using HyFib.HealthCare360.Modules.Interop.Domain;
using Hl7.Fhir.Utility;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Search;

/// <summary>A page of matching resources and the total number of matches.</summary>
internal sealed record SearchPage(IReadOnlyList<R4.Resource> Resources, int Total);

/// <summary>A resource's value for one sort rule: a point in time or a text, or none.</summary>
internal readonly record struct SortValue(DateTimeOffset? Time, string? Text)
{
    public bool IsNone => Time is null && Text is null;

    /// <summary>Resources without a value come last, whichever the direction.</summary>
    public static int Compare(SortValue a, SortValue b, bool descending)
    {
        if (a.IsNone || b.IsNone)
        {
            return a.IsNone == b.IsNone ? 0 : a.IsNone ? 1 : -1;
        }

        var order = a.Time is { } x && b.Time is { } y ? x.CompareTo(y) : string.CompareOrdinal(a.Text, b.Text);
        return descending ? -order : order;
    }
}

/// <summary>
/// Evaluates a search in memory over a bounded set of FHIR resources (those mapped from one patient's record, or the
/// hospital's directory), so the search semantics are defined once on FHIR elements, whatever table a resource came
/// from. Results are in the order asked for with <c>_sort</c>; then, and when none is asked for, newest first (by the
/// type's main date), then by id, which makes offset paging stable. The directory, which has no main date, is by id.
/// </summary>
internal static class ResourceSearch
{
    public static SearchPage Execute(IEnumerable<R4.Resource> resources, SearchRequest request)
    {
        var matching = resources
            .Where(resource => Matches(resource, request))
            .Select(resource => new Ranked(
                resource,
                [.. request.Sort.Select(rule => SearchValues.SortValueOf(resource, rule))],
                SearchValues.SortKey(resource) ?? DateTimeOffset.MinValue))
            .ToList();
        matching.Sort((a, b) => Compare(a, b, request.Sort));

        IReadOnlyList<R4.Resource> page = request.Count == 0 ? [] : [.. matching.Skip(request.Offset).Take(request.Count).Select(r => r.Resource)];
        return new SearchPage(page, matching.Count);
    }

    /// <summary>The order asked for, rule by rule; then newest first by the main date; then by id, so the order is total.</summary>
    private static int Compare(Ranked a, Ranked b, IReadOnlyList<SortRule> rules)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            var order = SortValue.Compare(a.Values[i], b.Values[i], rules[i].Descending);
            if (order != 0)
            {
                return order;
            }
        }

        var newest = b.Main.CompareTo(a.Main);
        return newest != 0 ? newest : string.CompareOrdinal(a.Resource.Id, b.Resource.Id);
    }

    private sealed record Ranked(R4.Resource Resource, IReadOnlyList<SortValue> Values, DateTimeOffset Main);

    public static bool Matches(R4.Resource resource, SearchRequest request)
    {
        var definition = request.Definition;
        if (definition.IsEnteredInError is { } enteredInError && definition.StatusParameter is { } status
            && !request.Has(status) && enteredInError(resource))
        {
            return false;
        }

        return request.Criteria.All(criterion => Matches(resource, criterion));
    }

    public static bool Matches(R4.Resource resource, SearchCriterion criterion) => criterion switch
    {
        TokenCriterion token => SearchValues.Tokens(resource, token.Parameter).Any(t => token.AnyOf.Any(v => v.Matches(t.System, t.Code))),
        DateCriterion date => SearchValues.Dates(resource, date.Parameter).Any(range => date.Value.Matches(range)),
        StringCriterion text => text.AnyOf.Any(query => StartsWords(SearchValues.Strings(resource, text.Parameter), query)),
        _ => false,
    };

    /// <summary>Each word of <paramref name="query"/> starts a word of the values (case-insensitive): "asha r" finds Dr. Asha Rao.</summary>
    private static bool StartsWords(IEnumerable<string> values, string query)
    {
        var words = Words(string.Join(' ', values));
        var wanted = Words(query);
        return wanted.Count > 0 && wanted.All(w => words.Any(word => word.StartsWith(w, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> Words(string text) => [.. text.Split([' ', '.', ',', '-', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries)];
}

/// <summary>The values a FHIR resource has for each search parameter.</summary>
internal static class SearchValues
{
    public static IEnumerable<(string? System, string? Code)> Tokens(R4.Resource resource, string parameter)
    {
        if (parameter == SearchDefinitions.Id)
        {
            return [(null, resource.Id)];
        }

        return resource switch
        {
            R4.Encounter e => parameter switch
            {
                "status" => Literal(FhirSystems.EncounterStatus, e.Status),
                "class" => e.Class is null ? [] : [(e.Class.System, e.Class.Code)],
                _ => [],
            },
            R4.Observation o => parameter switch
            {
                "category" => Codings(o.Category),
                "code" => Codings(o.Code),
                "status" => Literal(FhirSystems.ObservationStatus, o.Status),
                _ => [],
            },
            R4.Condition c => parameter switch
            {
                "clinical-status" => Codings(c.ClinicalStatus),
                "verification-status" => Codings(c.VerificationStatus),
                "category" => Codings(c.Category),
                "code" => Codings(c.Code),
                _ => [],
            },
            R4.AllergyIntolerance a => parameter switch
            {
                "clinical-status" => Codings(a.ClinicalStatus),
                "verification-status" => Codings(a.VerificationStatus),
                "category" => a.Category.OfType<R4.AllergyIntolerance.AllergyIntoleranceCategory>()
                    .Select(c => ((string?)FhirSystems.AllergyCategory, (string?)c.GetLiteral())),
                _ => [],
            },
            R4.MedicationRequest m => parameter switch
            {
                "status" => Literal(FhirSystems.MedicationRequestStatus, m.Status),
                "intent" => Literal(FhirSystems.MedicationRequestIntent, m.Intent),
                "category" => Codings(m.Category),
                _ => [],
            },
            R4.DiagnosticReport d => parameter switch
            {
                "category" => Codings(d.Category),
                "code" => Codings(d.Code),
                "status" => Literal(FhirSystems.DiagnosticReportStatus, d.Status),
                _ => [],
            },
            R4.DocumentReference d => parameter switch
            {
                "type" => Codings(d.Type),
                "status" => Literal(FhirSystems.DocumentReferenceStatus, d.Status),
                _ => [],
            },
            R4.Immunization i => parameter switch
            {
                "status" => Literal(FhirSystems.ImmunizationStatus, i.Status),
                "vaccine-code" => Codings(i.VaccineCode),
                _ => [],
            },
            R4.Procedure p => parameter switch
            {
                "status" => Literal(FhirSystems.ProcedureStatus, p.Status),
                _ => [],
            },
            R4.Practitioner p => parameter switch
            {
                "identifier" => p.Identifier.Select(i => (i.System, i.Value)),
                "active" => Active(p.Active),
                _ => [],
            },
            R4.Organization o => parameter switch
            {
                "identifier" => o.Identifier.Select(i => (i.System, i.Value)),
                "active" => Active(o.Active),
                _ => [],
            },
            R4.Location l => parameter switch
            {
                "identifier" => l.Identifier.Select(i => (i.System, i.Value)),
                "type" => Codings(l.Type),
                "status" => Literal(FhirSystems.LocationStatus, l.Status),
                "partof" => Reference(l.PartOf),
                "organization" => Reference(l.ManagingOrganization),
                _ => [],
            },
            _ => [],
        };
    }

    /// <summary>The words a string parameter is matched against (a name's text, family, given names and prefixes).</summary>
    public static IEnumerable<string> Strings(R4.Resource resource, string parameter) => (resource, parameter) switch
    {
        (R4.Practitioner p, "name") => p.Name.SelectMany(n => new[] { n.Text, n.Family }.Concat(n.Given).Concat(n.Prefix)).OfType<string>(),
        (R4.Organization o, "name") => o.Name is { } name ? [name] : [],
        (R4.Location l, "name") => l.Name is { } name ? [name] : [],
        _ => [],
    };

    private static IEnumerable<(string? System, string? Code)> Active(bool? active) => [(null, active == false ? "false" : "true")];

    /// <summary>A reference matches as <c>{Type}/{id}</c> or as the bare id (the search value is made relative first).</summary>
    private static IEnumerable<(string? System, string? Code)> Reference(R4.ResourceReference? reference) =>
        reference?.Reference is { } target ? [(null, target), (null, target[(target.IndexOf('/', StringComparison.Ordinal) + 1)..])] : [];

    public static IEnumerable<DateRange> Dates(R4.Resource resource, string parameter)
    {
        if (parameter == SearchDefinitions.LastUpdated)
        {
            return resource.Meta?.LastUpdated is { } lastUpdated ? [DateRange.Instant(lastUpdated)] : [];
        }

        return (resource, parameter) switch
        {
            (R4.Encounter e, "date") => Period(e.Period),
            (R4.Observation o, "date") => DateTimeValue(o.Effective),
            (R4.Condition c, "onset-date") => DateTimeValue(c.Onset),
            (R4.Condition c, "recorded-date") => Parse(c.RecordedDate),
            (R4.AllergyIntolerance a, "date") => Parse(a.RecordedDate),
            (R4.MedicationRequest m, "authoredon") => Parse(m.AuthoredOn),
            (R4.DiagnosticReport d, "date") => DateTimeValue(d.Effective),
            (R4.DocumentReference d, "date") => d.Date is { } date ? [DateRange.Instant(date)] : [],
            (R4.Immunization i, "date") => DateTimeValue(i.Occurrence),
            (R4.Procedure p, "date") => DateTimeValue(p.Performed),
            _ => [],
        };
    }

    /// <summary>
    /// A resource's value for a sort rule. Where it has several (codes, names, or the instants of a period or of a date at
    /// its precision), the one that comes first in the asked order counts: the earliest start in increasing order, the
    /// latest end in decreasing order, so an ongoing stay is the latest of all. Codes compare as written, by character
    /// code; names and other texts compare lower-cased.
    /// </summary>
    public static SortValue SortValueOf(R4.Resource resource, SortRule rule)
    {
        if (rule.Type == R4.SearchParamType.Date)
        {
            var ranges = Dates(resource, rule.Parameter).ToList();
            return ranges.Count == 0 ? default : new(rule.Descending ? ranges.Max(r => r.End) : ranges.Min(r => r.Start), null);
        }

        List<string> texts = rule.Type == R4.SearchParamType.String
            ? [.. SortTexts(resource, rule.Parameter).OfType<string>().Where(t => t.Length > 0).Select(t => t.ToLowerInvariant())]
            : [.. Tokens(resource, rule.Parameter).Select(t => t.Code).OfType<string>().Where(c => c.Length > 0)];
        return texts.Count == 0 ? default : new(null, rule.Descending ? texts.Max(StringComparer.Ordinal) : texts.Min(StringComparer.Ordinal));
    }

    /// <summary>
    /// The texts a string parameter orders by: a person's family name, then given names, as a name index lists people.
    /// Patients never come here: their search is ordered in SQL by the same rule (<see cref="Services.PatientSearchQuery"/>).
    /// </summary>
    private static IEnumerable<string?> SortTexts(R4.Resource resource, string parameter) => (resource, parameter) switch
    {
        (R4.Practitioner p, "name") => p.Name.Select(FamilyThenGiven),
        (R4.Organization o, "name") => [o.Name],
        (R4.Location l, "name") => [l.Name],
        _ => [],
    };

    private static string? FamilyThenGiven(R4.HumanName name)
    {
        var parts = new[] { name.Family }.Concat(name.Given).Where(part => !string.IsNullOrWhiteSpace(part)).ToList();
        return parts.Count > 0 ? string.Join(' ', parts) : name.Text;
    }

    /// <summary>The main date of a resource, used to order search results (newest first).</summary>
    public static DateTimeOffset? SortKey(R4.Resource resource)
    {
        var parameter = resource switch
        {
            R4.Condition => "recorded-date",
            R4.MedicationRequest => "authoredon",
            R4.Patient or R4.Practitioner or R4.Organization or R4.Location => null,
            _ => "date",
        };

        return parameter is null ? null : Dates(resource, parameter).Select(r => (DateTimeOffset?)r.Start).FirstOrDefault();
    }

    private static IEnumerable<(string? System, string? Code)> Codings(R4.CodeableConcept? concept) =>
        concept is null ? [] : concept.Coding.Select(c => (c.System, c.Code));

    private static IEnumerable<(string? System, string? Code)> Codings(IEnumerable<R4.CodeableConcept> concepts) =>
        concepts.SelectMany(Codings);

    private static IEnumerable<(string? System, string? Code)> Literal<TEnum>(string system, TEnum? value)
        where TEnum : struct, Enum =>
        value is { } code ? [(system, code.GetLiteral())] : [];

    private static IEnumerable<DateRange> Parse(string? value) =>
        DateRange.TryParse(value, out var range) ? [range] : [];

    private static IEnumerable<DateRange> DateTimeValue(R4.DataType? value) =>
        value is R4.FhirDateTime dateTime ? Parse(dateTime.Value) : [];

    private static IEnumerable<DateRange> Period(R4.Period? period)
    {
        if (period is null || !DateRange.TryParse(period.Start, out var start))
        {
            return [];
        }

        return DateRange.TryParse(period.End, out var end) ? [new DateRange(start.Start, end.End)] : [DateRange.From(start.Start)];
    }
}
