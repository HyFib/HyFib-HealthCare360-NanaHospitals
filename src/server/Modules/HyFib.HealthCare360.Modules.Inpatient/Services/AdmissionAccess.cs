using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>The admission facts chart screens need, loaded without tracking.</summary>
public sealed record AdmissionRef(Guid Id, Guid PatientId, Guid FacilityId, AdmissionStatus Status, Guid? WardId, Guid? BedId, DateTimeOffset? DischargedAt)
{
    public bool AcceptsCharting(DateTimeOffset now) => ChartingRules.IsOpen(Status, DischargedAt, now);
}

/// <summary>
/// Gatekeeper for nursing and MAR endpoints: enforces the restricted-record rules (VIP/confidential patients need
/// permission or break-the-glass) and writes the PHI-access audit entry once per read, as the module guide requires
/// for every endpoint that returns clinical data. Writes additionally require the chart to be open.
/// </summary>
public sealed class AdmissionAccess(HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time)
{
    public async Task<Result<AdmissionRef>> OpenForReadAsync(Guid admissionId, string what, CancellationToken ct)
    {
        var admission = await LoadAsync(admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var access = await guard.EnsureCanOpenChartAsync(admission.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        await audit.PatientRecordAccessedAsync(admission.PatientId, what, ct);
        return admission;
    }

    public async Task<Result<AdmissionRef>> OpenForChartingAsync(Guid admissionId, CancellationToken ct)
    {
        var admission = await LoadAsync(admissionId, ct);
        if (admission is null)
        {
            return Error.NotFound(nameof(Admission), admissionId);
        }

        var access = await guard.EnsureCanOpenChartAsync(admission.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!;
        }

        return admission.AcceptsCharting(time.GetUtcNow())
            ? admission
            : Error.Rule("admission.chart_closed", "This admission is closed for nursing documentation. Late entries after 24 hours go through Medical Records.");
    }

    private Task<AdmissionRef?> LoadAsync(Guid admissionId, CancellationToken ct) =>
        db.Set<Admission>().AsNoTracking()
            .Where(a => a.Id == admissionId)
            .Select(a => new AdmissionRef(a.Id, a.PatientId, a.FacilityId, a.Status, a.WardId, a.BedId, a.DischargedAt))
            .FirstOrDefaultAsync(ct);
}
