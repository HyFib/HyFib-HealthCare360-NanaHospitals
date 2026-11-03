using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>One recorded record-sharing consent decision of a patient, with what a given consent covers.</summary>
internal sealed record ConsentFact(bool Granted, DateTimeOffset CapturedAt, DateTimeOffset? RevokedAt, SharingScope? Scope = null);

/// <summary>Who reads through the FHIR API, the purpose they stated (none: null), and the hospital's date today.</summary>
internal sealed record SharingReader(Guid UserId, SharingPurpose? Purpose, DateOnly Today);

/// <summary>
/// What one given record-sharing consent covers: the accounts that may read, what for, which kinds of records, the
/// records' dates and the last day it lets anyone read. An empty list or a missing date leaves that part open, so a
/// consent recorded without a scope shares the whole record with every FHIR account, as before scopes existed, except
/// the kinds a consent must name (counselling notes, <see cref="SharedRecordKinds.NamedOnly"/>).
/// </summary>
internal sealed record SharingScope(
    IReadOnlyCollection<Guid> ShareWith,
    IReadOnlyCollection<SharingPurpose> Purposes,
    IReadOnlyCollection<SharedRecordKind> Kinds,
    DateOnly? RecordsFrom,
    DateOnly? RecordsTo,
    DateOnly? Until)
{
    /// <summary>A consent without a scope.</summary>
    public static SharingScope Everything { get; } = new([], [], [], null, null, null);

    /// <summary>
    /// Whether this reader may read under the consent today: it has not ended, names them (or nobody), and allows the
    /// purpose they stated. A reader who states no purpose is admitted only by a consent that allows any purpose.
    /// </summary>
    public bool Admits(SharingReader reader) =>
        (Until is null || reader.Today <= Until)
        && (ShareWith.Count == 0 || ShareWith.Contains(reader.UserId))
        && (Purposes.Count == 0 || (reader.Purpose is { } purpose && Purposes.Contains(purpose)));

    /// <summary>
    /// Whether a record of this kind, dated <paramref name="date"/> (hospital time), is shared under the consent. A kind
    /// the consent must name is shared only when it does. Once the consent names dates, a record without a date is not
    /// shared.
    /// </summary>
    public bool Covers(SharedRecordKind kind, DateOnly? date) =>
        (Kinds.Count == 0 ? !SharedRecordKinds.NamedOnly(kind) : Kinds.Contains(kind))
        && ((RecordsFrom is null && RecordsTo is null)
            || (date is { } day && (RecordsFrom is null || day >= RecordsFrom) && (RecordsTo is null || day <= RecordsTo)));
}

/// <summary>
/// EHR-05 shares records "subject to consent". A patient's record is shared only under a granted, unrevoked
/// <see cref="ConsentType.DataSharing"/> consent (captured under EHR-06) that no later recorded refusal supersedes; this is
/// the same rule Engagement applies to marketing consent. Each such consent is one permission with its own scope
/// (<see cref="SharingScope"/>): a reader may read what any of them, admitting that reader today, covers. The query form
/// is written against plain <see cref="IQueryable{T}"/>, so it is unit-tested in memory and translated to SQL in production.
/// </summary>
internal static class RecordSharingConsent
{
    /// <summary>The consent type that authorises sharing the record with other providers.</summary>
    public const ConsentType Type = ConsentType.DataSharing;

    /// <summary>A purpose no consent can name: what a reader who stated none is compared with in SQL.</summary>
    private const SharingPurpose NoPurpose = (SharingPurpose)(-1);

    /// <summary>Patients whose record this reader may read now (whatever its kinds and dates: a Patient search).</summary>
    public static Expression<Func<Patient, bool>> IsGranted(IQueryable<PatientConsent> consents, SharingReader reader)
    {
        var user = reader.UserId;
        var stated = reader.Purpose is not null;
        var purpose = reader.Purpose ?? NoPurpose;
        var today = reader.Today;
        return p => consents.Any(c => c.PatientId == p.Id && c.Type == ConsentType.DataSharing && c.Granted && c.RevokedAt == null
                                      && (c.ShareUntil == null || c.ShareUntil >= today)
                                      && (c.ShareWith.Count == 0 || c.ShareWith.Contains(user))
                                      && (c.SharePurposes.Count == 0 || (stated && c.SharePurposes.Contains(purpose)))
                                      && !consents.Any(d => d.PatientId == p.Id && d.Type == ConsentType.DataSharing && !d.Granted
                                                            && d.CapturedAt > c.CapturedAt));
    }

    /// <summary>
    /// The scopes of the patient's standing consents that admit this reader now. None: nothing of the record is shared
    /// with them.
    /// </summary>
    public static IReadOnlyList<SharingScope> ScopesFor(IReadOnlyCollection<ConsentFact> history, SharingReader reader) =>
        [.. history.Where(c => c.Granted && c.RevokedAt is null && !history.Any(d => !d.Granted && d.CapturedAt > c.CapturedAt))
            .Select(c => c.Scope ?? SharingScope.Everything)
            .Where(scope => scope.Admits(reader))];

    /// <summary>The same rule as the query form, over one patient's record-sharing consent history.</summary>
    public static bool IsGranted(IReadOnlyCollection<ConsentFact> history, SharingReader reader) => ScopesFor(history, reader).Count > 0;
}
