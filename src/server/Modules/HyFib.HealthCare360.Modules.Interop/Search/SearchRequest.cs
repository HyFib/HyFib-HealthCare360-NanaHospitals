using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using Microsoft.Extensions.Primitives;

namespace HyFib.HealthCare360.Modules.Interop.Search;

/// <summary>One parsed search criterion (parameters AND together; values inside one criterion OR together).</summary>
internal abstract record SearchCriterion(string Parameter);

internal sealed record TokenCriterion(string Parameter, IReadOnlyList<TokenValue> AnyOf) : SearchCriterion(Parameter);

internal sealed record DateCriterion(string Parameter, DateParameter Value) : SearchCriterion(Parameter);

internal sealed record StringCriterion(string Parameter, IReadOnlyList<string> AnyOf) : SearchCriterion(Parameter);

/// <summary>A validated search.</summary>
/// <param name="PatientId">The <c>patient</c>/<c>subject</c> id for patient-scoped types (as sent; may not be a GUID).</param>
/// <param name="LinkParameters">The accepted parameters as sent (without paging), echoed in the bundle links.</param>
internal sealed record SearchRequest(
    ResourceSearchDefinition Definition,
    string? PatientId,
    IReadOnlyList<SearchCriterion> Criteria,
    int Count,
    int Offset,
    bool Pretty,
    IReadOnlyList<KeyValuePair<string, string>> LinkParameters)
{
    public bool Has(string parameter) => Criteria.Any(c => c.Parameter == parameter);

    /// <summary>Names of the parameters used (for the audit trail; values may be PHI and are never recorded).</summary>
    public IReadOnlyList<string> ParameterNames =>
        LinkParameters.Select(p => p.Key).Append(SearchDefinitions.Count).Distinct(StringComparer.Ordinal).ToList();
}

/// <summary>The outcome of parsing: a request, or the error to return.</summary>
internal sealed record SearchParse(SearchRequest? Request, FhirError? Error);

/// <summary>
/// Validates FHIR search parameters strictly: an unknown parameter, a modifier, a malformed value or a second patient is
/// a 400 OperationOutcome rather than being ignored, so a mistyped filter never silently widens the result to more PHI.
/// </summary>
internal static class SearchRequestParser
{
    private static readonly string[] JsonFormats = ["json", "application/json", "application/fhir+json", "application/json+fhir", "text/json"];

    public static SearchParse Parse(ResourceSearchDefinition definition, IEnumerable<KeyValuePair<string, StringValues>> parameters)
    {
        var criteria = new List<SearchCriterion>();
        var echo = new List<KeyValuePair<string, string>>();
        string? patient = null;
        int? count = null;
        var offset = 0;
        var pretty = false;

        foreach (var (key, values) in parameters)
        {
            foreach (var raw in values)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var value = raw.Trim();
                if (key.Contains(':', StringComparison.Ordinal))
                {
                    return Fail(FhirOutcomes.Unsupported($"Search modifiers are not supported ('{FhirOutcomes.Printable(key)}')."));
                }

                switch (key)
                {
                    case SearchDefinitions.Count:
                        if (count is not null || !int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var requested))
                        {
                            return Fail(FhirOutcomes.BadRequest("_count must be a single whole number of 0 or more."));
                        }

                        count = requested;
                        continue;

                    case SearchDefinitions.Offset:
                        if (!int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out offset))
                        {
                            return Fail(FhirOutcomes.BadRequest("_offset must be a whole number of 0 or more."));
                        }

                        continue;

                    case SearchDefinitions.Format:
                        if (!IsJson(value))
                        {
                            return Fail(FhirOutcomes.NotAcceptableFormat());
                        }

                        echo.Add(new(key, value));
                        continue;

                    case SearchDefinitions.Pretty:
                        if (!bool.TryParse(value, out pretty))
                        {
                            return Fail(FhirOutcomes.BadRequest("_pretty must be true or false."));
                        }

                        echo.Add(new(key, value));
                        continue;

                    case SearchDefinitions.Patient or SearchDefinitions.Subject when definition.Find(key) is not null:
                        if (patient is not null || TokenValue.SplitOr(value).Count > 1)
                        {
                            return Fail(FhirOutcomes.BadRequest("Search one patient at a time (a single patient or subject value)."));
                        }

                        if (PatientIdFromReference(value) is not { } id)
                        {
                            return Fail(FhirOutcomes.BadRequest("patient/subject must reference a Patient (Patient/{id} or {id})."));
                        }

                        patient = id;
                        echo.Add(new(key, value));
                        continue;
                }

                if (definition.Find(key) is not { } parameter)
                {
                    return Fail(FhirOutcomes.Unsupported(
                        $"Search parameter '{FhirOutcomes.Printable(key)}' is not supported for {definition.Type}. See /fhir/r4/metadata for the supported parameters."));
                }

                var criterion = ParseCriterion(parameter, value, out var error);
                if (criterion is null)
                {
                    return Fail(FhirOutcomes.BadRequest(error ?? $"Invalid value for '{parameter.Name}'."));
                }

                criteria.Add(criterion);
                echo.Add(new(key, value));
            }
        }

        if (definition.IsPatientScoped && patient is null)
        {
            return Fail(FhirOutcomes.BadRequest(
                $"{definition.Type} searches need a patient: add patient=Patient/{{id}} (search Patient by identifier first).", FhirOutcomes.PatientRequired));
        }

        if (!definition.IsPatientScoped && !criteria.Any(c => SearchDefinitions.PatientSelectors.Contains(c.Parameter, StringComparer.Ordinal)))
        {
            return Fail(FhirOutcomes.BadRequest($"Patient searches need at least one of: {string.Join(", ", SearchDefinitions.PatientSelectors)}."));
        }

        var size = count is null ? SearchDefinitions.DefaultCount : Math.Min(count.Value, SearchDefinitions.MaxCount);
        return new SearchParse(new SearchRequest(definition, patient, criteria, size, offset, pretty, echo), null);
    }

    /// <summary>Accepts <c>Patient/{id}</c>, <c>{id}</c> or an absolute URL ending in <c>Patient/{id}</c>; other types are refused.</summary>
    public static string? PatientIdFromReference(string reference)
    {
        var value = reference.Trim();
        var slash = value.LastIndexOf('/');
        if (slash < 0)
        {
            return value.Length > 0 ? value : null;
        }

        var prefix = value[..slash];
        var type = prefix[(prefix.LastIndexOf('/') + 1)..];
        var id = value[(slash + 1)..];
        return type == FhirResourceTypes.Patient && id.Length > 0 ? id : null;
    }

    public static bool IsJson(string format)
    {
        var mediaType = format.Split(';', 2)[0].Trim();
        return JsonFormats.Contains(mediaType, StringComparer.OrdinalIgnoreCase);
    }

    private static SearchCriterion? ParseCriterion(SearchParameterDefinition parameter, string value, out string? error)
    {
        error = null;
        switch (parameter.Type)
        {
            case Hl7.Fhir.Model.SearchParamType.Date:
                if (TokenValue.SplitOr(value).Count > 1)
                {
                    error = $"'{parameter.Name}' takes one date per parameter; repeat the parameter for a range (ge… and le…).";
                    return null;
                }

                if (!DateParameter.TryParse(value, out var date, out error))
                {
                    return null;
                }

                if (parameter.Name == "birthdate" && !DateRange.IsDateOnly(value))
                {
                    error = "birthdate takes a date (yyyy, yyyy-MM or yyyy-MM-dd) without a time.";
                    return null;
                }

                return new DateCriterion(parameter.Name, date!);

            case Hl7.Fhir.Model.SearchParamType.String:
                var words = TokenValue.SplitOr(value).Select(v => TokenValue.Unescape(v).Trim()).ToList();
                if (words.Any(w => w.Length < 2))
                {
                    error = $"'{parameter.Name}' needs at least 2 characters.";
                    return null;
                }

                return new StringCriterion(parameter.Name, words);

            default:
                var tokens = TokenValue.SplitOr(value).Select(TokenValue.Parse).ToList();
                if (tokens.Count == 0 || tokens.Any(t => t.System is null && t.Code is null))
                {
                    error = $"'{parameter.Name}' needs a code (system|code or code).";
                    return null;
                }

                return new TokenCriterion(parameter.Name, tokens);
        }
    }

    private static SearchParse Fail(FhirError error) => new(null, error);
}
