using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Search;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>The Patient search criteria, translated from FHIR tokens to HC360 columns (AND across criteria, OR inside one).</summary>
internal sealed class PatientCriteria
{
    public List<Guid[]> Ids { get; } = [];

    /// <summary>Per identifier criterion: UHIDs and ABHA blind indexes that match it.</summary>
    public List<(string[] Uhids, string[] AbhaIndexes)> Identifiers { get; } = [];

    /// <summary>Per name criterion: alternatives, each a list of words that must all appear.</summary>
    public List<string[][]> Names { get; } = [];

    public List<DateParameter> BirthDates { get; } = [];

    public List<DateParameter> LastUpdated { get; } = [];

    public List<Gender[]> Genders { get; } = [];

    /// <summary>
    /// Builds the criteria of a Patient search. Identifier tokens: <c>urn:hc360:uhid|X</c> (exact UHID),
    /// <c>https://healthid.ndhm.gov.in|NN-NNNN-NNNN-NNNN</c> (ABHA number through its blind index — the number itself is
    /// never stored in clear) or a bare value, which is tried as both. Unknown systems match nothing.
    /// </summary>
    public static PatientCriteria From(SearchRequest request, Func<string, IEnumerable<string>> blindIndexes)
    {
        var criteria = new PatientCriteria();
        foreach (var criterion in request.Criteria)
        {
            switch (criterion)
            {
                case TokenCriterion { Parameter: SearchDefinitions.Id } ids:
                    criteria.Ids.Add([.. ids.AnyOf.Select(t => FhirIds.TryParse(t.Code, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)]);
                    break;

                case TokenCriterion { Parameter: "identifier" } identifier:
                    var uhids = new List<string>();
                    var indexes = new List<string>();
                    foreach (var token in identifier.AnyOf.Where(t => t.Code is not null))
                    {
                        if (token.System is null or FhirSystems.Uhid)
                        {
                            uhids.Add(token.Code!.Trim().ToUpperInvariant());
                        }

                        if (token.System is null or FhirSystems.Abha && Masking.Normalize(token.Code!) is { Length: 14 } abha && abha.All(char.IsAsciiDigit))
                        {
                            indexes.AddRange(blindIndexes(abha));
                        }
                    }

                    criteria.Identifiers.Add(([.. uhids], [.. indexes]));
                    break;

                case TokenCriterion { Parameter: "gender" } gender:
                    criteria.Genders.Add([.. gender.AnyOf.Select(t => Gender(t.Code)).OfType<Gender>().Distinct()]);
                    break;

                case StringCriterion { Parameter: "name" } name:
                    criteria.Names.Add([.. name.AnyOf.Select(alternative => alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4).ToArray())]);
                    break;

                case DateCriterion { Parameter: "birthdate" } birthdate:
                    criteria.BirthDates.Add(birthdate.Value);
                    break;

                case DateCriterion { Parameter: SearchDefinitions.LastUpdated } lastUpdated:
                    criteria.LastUpdated.Add(lastUpdated.Value);
                    break;
            }
        }

        return criteria;
    }

    private static Gender? Gender(string? code) => code?.ToLowerInvariant() switch
    {
        "male" => SharedKernel.Gender.Male,
        "female" => SharedKernel.Gender.Female,
        "other" => SharedKernel.Gender.Other,
        "unknown" => SharedKernel.Gender.Unknown,
        _ => null,
    };
}

/// <summary>
/// The SQL side of a Patient search: only active, unmerged patients with a record-sharing consent that admits this reader
/// today for the purpose they stated, restricted (VIP) patients only for callers who may open them, then the criteria.
/// Written against plain IQueryable, so every rule except the database-specific name match is unit-tested in memory.
/// </summary>
internal static class PatientSearchQuery
{
    public static IQueryable<Patient> Apply(
        IQueryable<Patient> patients,
        IQueryable<PatientConsent> consents,
        PatientCriteria criteria,
        bool includeRestricted,
        SharingReader reader,
        Func<string, Expression<Func<Patient, bool>>> nameContains)
    {
        var query = patients
            .Where(p => p.IsActive && p.MergedIntoPatientId == null)
            .Where(RecordSharingConsent.IsGranted(consents, reader));
        if (!includeRestricted)
        {
            // Restricted (VIP) records, and records in a sensitive category (R-4), are not listed to clients that may not open them.
            query = query.Where(p => !p.IsVip && p.SensitiveCategories == SensitiveCategory.None);
        }

        foreach (var ids in criteria.Ids)
        {
            query = query.Where(p => ids.Contains(p.Id));
        }

        foreach (var (uhids, indexes) in criteria.Identifiers)
        {
            query = query.Where(p => uhids.Contains(p.Uhid) || (p.AbhaNumberIndex != null && indexes.Contains(p.AbhaNumberIndex)));
        }

        foreach (var genders in criteria.Genders)
        {
            query = query.Where(Any(genders.Select(Is)));
        }

        foreach (var alternatives in criteria.Names)
        {
            query = query.Where(Any(alternatives.Select(words => All(words.Select(nameContains)))));
        }

        foreach (var date in criteria.BirthDates)
        {
            query = query.Where(BirthDate(date));
        }

        foreach (var date in criteria.LastUpdated)
        {
            query = query.Where(LastUpdated(date));
        }

        return query;
    }

    /// <summary>
    /// The order asked for with <c>_sort</c>, by the values the Patient resource is served with (as the in-memory search
    /// orders the other types); then newest registrations first, then by id (stable paging; never sorted by an enum
    /// column as stored). A patient without the value comes last, whichever the direction.
    /// </summary>
    public static IQueryable<Patient> Ordered(IQueryable<Patient> patients, IReadOnlyList<SortRule> sort)
    {
        IOrderedQueryable<Patient>? ordered = null;
        foreach (var rule in sort)
        {
            var descending = rule.Descending;
            ordered = rule.Parameter switch
            {
                "name" => By(patients, ordered, NameKey, descending),
                "birthdate" => By(patients, By(patients, ordered, p => p.DateOfBirth == null, false), descending ? BirthDateEnd : BirthDateStart, descending),
                "gender" => By(patients, ordered, GenderKey, descending),
                SearchDefinitions.LastUpdated => By(patients, ordered, p => p.UpdatedAt != null && p.UpdatedAt > p.CreatedAt ? p.UpdatedAt.Value : p.CreatedAt, descending),
                SearchDefinitions.Id => By(patients, ordered, p => p.Id, descending),
                _ => throw new ArgumentOutOfRangeException(nameof(sort), rule.Parameter, "Not a sortable Patient parameter."),
            };
        }

        return ordered is null
            ? patients.OrderByDescending(p => p.RegisteredAt).ThenBy(p => p.Id)
            : ordered.ThenByDescending(p => p.RegisteredAt).ThenBy(p => p.Id);
    }

    /// <summary>
    /// The name as served (family name, then given names), lower-cased and compared byte by byte, so the database orders
    /// names as the in-memory search does, whatever its collation.
    /// </summary>
    private static readonly Expression<Func<Patient, string>> NameKey = p => EF.Functions.Collate(
        ((p.LastName != null ? p.LastName + " " : "") + p.FirstName + (p.MiddleName != null ? " " + p.MiddleName : "")).ToLower(), "C");

    /// <summary>
    /// The start and the end of the birth date as served: the day, or for a date of birth estimated from a stated age, the
    /// whole year (it is published with the year only).
    /// </summary>
    private static readonly Expression<Func<Patient, DateOnly?>> BirthDateStart = p =>
        p.DateOfBirth == null ? null
        : p.IsDateOfBirthEstimated ? new DateOnly(p.DateOfBirth.Value.Year, 1, 1)
        : p.DateOfBirth.Value;

    private static readonly Expression<Func<Patient, DateOnly?>> BirthDateEnd = p =>
        p.DateOfBirth == null ? null
        : p.IsDateOfBirthEstimated ? new DateOnly(p.DateOfBirth.Value.Year + 1, 1, 1)
        : p.DateOfBirth.Value.AddDays(1);

    /// <summary>The FHIR code's place in alphabetical order: female, male, other, unknown.</summary>
    private static readonly Expression<Func<Patient, int>> GenderKey = p =>
        p.Gender == Gender.Female ? 0 : p.Gender == Gender.Male ? 1 : p.Gender == Gender.Other ? 2 : 3;

    private static IOrderedQueryable<Patient> By<TKey>(
        IQueryable<Patient> patients, IOrderedQueryable<Patient>? ordered, Expression<Func<Patient, TKey>> key, bool descending) =>
        ordered is null
            ? descending ? patients.OrderByDescending(key) : patients.OrderBy(key)
            : descending ? ordered.ThenByDescending(key) : ordered.ThenBy(key);

    private static Expression<Func<Patient, bool>> Is(Gender gender) => p => p.Gender == gender;

    private static Expression<Func<Patient, bool>> BirthDate(DateParameter date)
    {
        var (from, before, negate) = date.InstantBounds();
        DateOnly? fromDate = from is { } f ? DateOnly.FromDateTime(f.ToOffset(FhirTime.Ist).DateTime) : null;
        DateOnly? beforeDate = before is { } b ? DateOnly.FromDateTime(b.ToOffset(FhirTime.Ist).DateTime) : null;
        return negate
            ? p => p.DateOfBirth != null && (p.DateOfBirth < fromDate || p.DateOfBirth >= beforeDate)
            : p => p.DateOfBirth != null && (fromDate == null || p.DateOfBirth >= fromDate) && (beforeDate == null || p.DateOfBirth < beforeDate);
    }

    private static Expression<Func<Patient, bool>> LastUpdated(DateParameter date)
    {
        var (from, before, negate) = date.InstantBounds();
        var fromUtc = from?.ToUniversalTime();
        var beforeUtc = before?.ToUniversalTime();
        return negate
            ? p => (p.UpdatedAt ?? p.CreatedAt) < fromUtc || (p.UpdatedAt ?? p.CreatedAt) >= beforeUtc
            : p => (fromUtc == null || (p.UpdatedAt ?? p.CreatedAt) >= fromUtc) && (beforeUtc == null || (p.UpdatedAt ?? p.CreatedAt) < beforeUtc);
    }

    private static Expression<Func<Patient, bool>> Any(IEnumerable<Expression<Func<Patient, bool>>> predicates) =>
        Combine(predicates, Expression.OrElse, fallback: false);

    private static Expression<Func<Patient, bool>> All(IEnumerable<Expression<Func<Patient, bool>>> predicates) =>
        Combine(predicates, Expression.AndAlso, fallback: true);

    private static Expression<Func<Patient, bool>> Combine(
        IEnumerable<Expression<Func<Patient, bool>>> predicates, Func<Expression, Expression, BinaryExpression> join, bool fallback)
    {
        var parameter = Expression.Parameter(typeof(Patient), "p");
        Expression? body = null;
        foreach (var predicate in predicates)
        {
            var rebound = new ParameterRebinder(predicate.Parameters[0], parameter).Visit(predicate.Body);
            body = body is null ? rebound : join(body, rebound);
        }

        return Expression.Lambda<Func<Patient, bool>>(body ?? Expression.Constant(fallback), parameter);
    }

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
