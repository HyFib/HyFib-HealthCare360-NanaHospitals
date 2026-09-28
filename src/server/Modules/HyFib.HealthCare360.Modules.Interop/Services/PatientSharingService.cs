using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>The sharing decision for one patient, with the surviving record when it was merged.</summary>
internal sealed record PatientAccess(SharingDecision Decision, Guid? MergedIntoPatientId = null)
{
    public bool IsAllowed => Decision == SharingDecision.Allowed;
}

/// <summary>
/// Applies the sharing rules (<see cref="PatientSharingRules"/>) to a patient: lifecycle (deleted, merged), the
/// record-sharing consent (<see cref="RecordSharingConsent"/>) and the platform's restricted-record guard
/// (<see cref="PatientAccessGuard"/>, EHR-07: permission or an audited break-the-glass grant).
/// </summary>
internal sealed class PatientSharingService(HospitalDbContext db, PatientAccessGuard guard)
{
    public async Task<PatientAccess> EvaluateAsync(Guid patientId, CancellationToken ct)
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
        var consentGranted = RecordSharingConsent.IsGranted(consents);

        // The restricted-record guard is consulted only for consenting patients (it never overrides a missing consent).
        var canOpen = consentGranted && (await guard.EnsureCanOpenAsync(patientId, ct)).IsSuccess;
        return new PatientAccess(PatientSharingRules.Decide(new PatientSharingFacts(true, false, null, consentGranted, canOpen)));
    }

    /// <summary>The patient's record-sharing consent history (soft-deleted consent rows do not count).</summary>
    internal IQueryable<ConsentFact> Consents(Guid patientId) =>
        db.Set<PatientConsent>().AsNoTracking()
            .Where(c => c.PatientId == patientId && c.Type == RecordSharingConsent.Type)
            .Select(c => new ConsentFact(c.Granted, c.CapturedAt, c.RevokedAt));

    /// <summary>The surviving record of a merge, when the caller may see it (otherwise it is not revealed).</summary>
    public async Task<Guid?> ShareableSurvivorAsync(Guid? mergedIntoPatientId, CancellationToken ct) =>
        mergedIntoPatientId is { } target && (await EvaluateAsync(target, ct)).IsAllowed ? target : null;
}
