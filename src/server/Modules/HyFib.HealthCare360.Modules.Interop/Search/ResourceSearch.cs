using HyFib.HealthCare360.Modules.Interop.Domain;
using Hl7.Fhir.Utility;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Search;

/// <summary>A page of matching resources and the total number of matches.</summary>
internal sealed record SearchPage(IReadOnlyList<R4.Resource> Resources, int Total);

/// <summary>
/// Evaluates a patient-scoped search in memory over the FHIR resources mapped from one patient's record (a bounded set),
/// so the search semantics are defined once on FHIR elements, whatever table a resource came from. Results are newest
/// first (by the type's main date), then by id, which makes offset paging stable.
/// </summary>
internal static class ResourceSearch
{
    public static SearchPage Execute(IEnumerable<R4.Resource> resources, SearchRequest request)
    {
        var matching = resources
            .Where(resource => Matches(resource, request))
            .OrderByDescending(resource => SearchValues.SortKey(resource) ?? DateTimeOffset.MinValue)
            .ThenBy(resource => resource.Id, StringComparer.Ordinal)
            .ToList();

        IReadOnlyList<R4.Resource> page = request.Count == 0 ? [] : matching.Skip(request.Offset).Take(request.Count).ToList();
        return new SearchPage(page, matching.Count);
    }

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
        _ => false,
    };
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
            _ => [],
        };
    }

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
            _ => [],
        };
    }

    /// <summary>The main date of a resource, used to order search results (newest first).</summary>
    public static DateTimeOffset? SortKey(R4.Resource resource)
    {
        var parameter = resource switch
        {
            R4.Condition => "recorded-date",
            R4.MedicationRequest => "authoredon",
            R4.Patient => null,
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
