using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// The admissions as the modules below Inpatient see them (<see cref="IAdmissionDirectory"/>): Clinical asks whether a
/// stay goes on before it takes an order for it. Read in every facility: the stay is what it is, whoever asks.
/// </summary>
internal sealed class AdmissionDirectory(HospitalDbContext db) : IAdmissionDirectory
{
    public async Task<StaySummary?> FindAsync(Guid admissionId, CancellationToken cancellationToken)
    {
        var admission = await db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => a.Id == admissionId)
            .Select(a => new { a.AdmissionNumber, a.PatientId, a.Status })
            .FirstOrDefaultAsync(cancellationToken);
        return admission is null ? null : new StaySummary(admissionId, admission.AdmissionNumber, admission.PatientId, StatusOf(admission.Status));
    }

    /// <summary>A stay goes on while the patient is in a bed; every other status but a cancellation is a discharge of some kind.</summary>
    internal static StayStatus StatusOf(AdmissionStatus status) => status switch
    {
        AdmissionStatus.Admitted or AdmissionStatus.DischargeInitiated => StayStatus.Open,
        AdmissionStatus.Cancelled => StayStatus.Cancelled,
        _ => StayStatus.Discharged,
    };
}
