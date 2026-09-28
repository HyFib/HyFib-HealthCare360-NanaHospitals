using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Clinical.Services;

/// <summary>
/// One place for the PHI rules every clinical endpoint must follow: restricted (VIP) records are opened only through
/// <see cref="PatientAccessGuard"/> (permission or audited break-the-glass, EHR-07) and each read is recorded in the
/// tamper-evident audit log (SEC-04). Writes are guarded too, because writing into a chart reveals it.
/// </summary>
public sealed class ClinicalAccess(HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit)
{
    /// <summary>Guard + one PHI-access audit entry. Call once per request that returns clinical data.</summary>
    public async Task<Result> OpenAsync(Guid patientId, string what, CancellationToken ct)
    {
        var access = await guard.EnsureCanOpenAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access;
        }

        await audit.PatientRecordAccessedAsync(patientId, what, ct);
        return Result.Success();
    }

    /// <summary>Guard only (writes are audited by the data-change audit).</summary>
    public Task<Result> EnsureCanOpenAsync(Guid patientId, CancellationToken ct) => guard.EnsureCanOpenAsync(patientId, ct);

    /// <summary>
    /// For new clinical entries: the patient must exist, be active and not merged away (clinical data recorded on a
    /// merged duplicate would be invisible on the surviving record).
    /// </summary>
    public async Task<Result> EnsureCanRecordAsync(Guid patientId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new { p.MergedIntoPatientId, p.IsActive })
            .FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), patientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Record clinical data on the surviving record.");
        }

        return await guard.EnsureCanOpenAsync(patientId, ct);
    }
}
