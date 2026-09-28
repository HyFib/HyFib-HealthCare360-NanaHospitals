namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>Whether the facade may return a patient's record to the caller, and why not.</summary>
internal enum SharingDecision
{
    Allowed = 0,

    /// <summary>No such patient (or the id cannot be a patient id).</summary>
    NotFound,

    /// <summary>The patient record was deleted (410 Gone).</summary>
    Deleted,

    /// <summary>The record was merged into another UHID (410 Gone).</summary>
    Merged,

    /// <summary>No active record-sharing consent (403, excluded from searches).</summary>
    NoConsent,

    /// <summary>Restricted (VIP) record the caller may not open (403, excluded from searches).</summary>
    Restricted,
}

/// <summary>What is known about a patient when deciding whether to share their record.</summary>
/// <param name="Exists">A patient row with this id exists (including a deleted one).</param>
/// <param name="IsDeleted">The row is soft-deleted.</param>
/// <param name="MergedIntoPatientId">Surviving record when this one was merged away.</param>
/// <param name="ConsentGranted">An active record-sharing consent is on file (see <see cref="RecordSharingConsent"/>).</param>
/// <param name="CanOpenRestricted">The restricted-record guard lets the caller open the record (always true for non-VIP records).</param>
internal sealed record PatientSharingFacts(bool Exists, bool IsDeleted, Guid? MergedIntoPatientId, bool ConsentGranted, bool CanOpenRestricted);

/// <summary>
/// The order in which the sharing rules apply. Existence and lifecycle come first (404, 410), then consent, then the
/// restricted-record guard (EHR-07). Consent is checked before the VIP guard, so a record without consent is never
/// opened, not even by break-the-glass.
/// </summary>
internal static class PatientSharingRules
{
    public static SharingDecision Decide(PatientSharingFacts facts) => facts switch
    {
        { Exists: false } => SharingDecision.NotFound,
        { IsDeleted: true } => SharingDecision.Deleted,
        { MergedIntoPatientId: not null } => SharingDecision.Merged,
        { ConsentGranted: false } => SharingDecision.NoConsent,
        { CanOpenRestricted: false } => SharingDecision.Restricted,
        _ => SharingDecision.Allowed,
    };
}
