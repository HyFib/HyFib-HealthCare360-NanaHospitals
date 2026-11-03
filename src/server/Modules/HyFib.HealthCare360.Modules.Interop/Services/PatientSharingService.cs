using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>
/// The sharing decision for one patient and one reader, with the surviving record when it was merged, and the scopes of
/// the consents under which the reader may read.
/// </summary>
internal sealed record PatientAccess(SharingDecision Decision, Guid? MergedIntoPatientId = null, IReadOnlyList<SharingScope>? Scopes = null)
{
    public bool IsAllowed => Decision == SharingDecision.Allowed;

    /// <summary>Whether a record of this kind and date (hospital time) is shared with the reader under any of those consents.</summary>
    public bool Covers(SharedRecordKind kind, DateOnly? date) => IsAllowed && Scopes is { } scopes && scopes.Any(s => s.Covers(kind, date));
}

/// <summary>
/// Applies the sharing rules (<see cref="PatientSharingRules"/>) to a patient and a reader: lifecycle (deleted, merged),
/// the record-sharing consents that admit the reader today for the purpose they stated (<see cref="RecordSharingConsent"/>)
/// and the platform's restricted-record guard (<see cref="PatientAccessGuard"/>, EHR-07: permission or an audited
/// break-the-glass grant).
/// </summary>
internal sealed class PatientSharingService(HospitalDbContext db, PatientAccessGuard guard)
{
    public async Task<PatientAccess> EvaluateAsync(Guid patientId, SharingReader reader, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new { p.IsDeleted, p.MergedIntoPatientId })
            .FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return new PatientAccess(SharingDecision.NotFound);
        }

        if (patient.IsDeleted || patient.MergedIntoPatientId is not null)
        {
            var gone = PatientSharingRules.Decide(new PatientSharingFacts(true, patient.IsDeleted, patient.MergedIntoPatientId, false, false));
            return new PatientAccess(gone, patient.MergedIntoPatientId);
        }

        var consents = await Consents(patientId).ToListAsync(ct);
        var scopes = RecordSharingConsent.ScopesFor(consents, reader);
        var consentGranted = scopes.Count > 0;

        // The restricted-record guard is consulted only for consenting patients (it never overrides a missing consent).
        var canOpen = consentGranted && (await guard.EnsureCanOpenChartAsync(patientId, ct)).IsSuccess;
        return new PatientAccess(PatientSharingRules.Decide(new PatientSharingFacts(true, false, null, consentGranted, canOpen)), null, scopes);
    }

    /// <summary>The patient's record-sharing consent history with each consent's scope (soft-deleted consent rows do not count).</summary>
    internal IQueryable<ConsentFact> Consents(Guid patientId) =>
        db.Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == patientId && c.Type == RecordSharingConsent.Type)
            .Select(c => new ConsentFact(c.Granted, c.CapturedAt, c.RevokedAt,
                new SharingScope(c.ShareWith, c.SharePurposes, c.ShareKinds, c.ShareRecordsFrom, c.ShareRecordsTo, c.ShareUntil)));

    /// <summary>The surviving record of a merge, when the reader may see it (otherwise it is not revealed).</summary>
    public async Task<Guid?> ShareableSurvivorAsync(Guid? mergedIntoPatientId, SharingReader reader, CancellationToken ct) =>
        mergedIntoPatientId is { } target && (await EvaluateAsync(target, reader, ct)).IsAllowed ? target : null;
}
