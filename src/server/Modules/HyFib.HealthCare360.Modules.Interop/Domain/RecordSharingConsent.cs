using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>One recorded record-sharing consent decision of a patient.</summary>
internal sealed record ConsentFact(bool Granted, DateTimeOffset CapturedAt, DateTimeOffset? RevokedAt);

/// <summary>
/// EHR-05 shares records "subject to consent". A patient's record is shared only while the patient holds a granted,
/// unrevoked <see cref="ConsentType.DataSharing"/> consent (captured under EHR-06) that no later recorded refusal
/// supersedes. This is the same rule Engagement applies to marketing consent. The query form is written against plain
/// <see cref="IQueryable{T}"/>, so it is unit-tested in memory and translated to SQL in production.
/// </summary>
internal static class RecordSharingConsent
{
    /// <summary>The consent type that authorises sharing the record with other providers.</summary>
    public const ConsentType Type = ConsentType.DataSharing;

    /// <summary>Patients whose record may be shared now.</summary>
    public static Expression<Func<Patient, bool>> IsGranted(IQueryable<PatientConsent> consents) =>
        p => consents.Any(c => c.PatientId == p.Id && c.Type == ConsentType.DataSharing && c.Granted && c.RevokedAt == null
                               && !consents.Any(d => d.PatientId == p.Id && d.Type == ConsentType.DataSharing && !d.Granted
                                                     && d.CapturedAt > c.CapturedAt));

    /// <summary>The same rule over one patient's record-sharing consent history.</summary>
    public static bool IsGranted(IReadOnlyCollection<ConsentFact> history) =>
        history.Any(c => c.Granted && c.RevokedAt is null && !history.Any(d => !d.Granted && d.CapturedAt > c.CapturedAt));
}
