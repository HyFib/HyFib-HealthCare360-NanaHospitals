using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>
/// The care team of a stay, for Clinical's sign-off rules (maker-checker, slice 15): its consultant, the admitting doctor,
/// and the doctors who recorded a consultant visit during it (cross-consultations, rounds for a colleague). A stay is
/// read in any facility, as a discharge summary and a result are.
/// </summary>
internal sealed class StayCareTeams(HospitalDbContext db) : IStayCareTeams
{
    public async Task<StayCareTeam?> OfAdmissionAsync(Guid admissionId, CancellationToken ct)
    {
        var stay = await db.Set<Admission>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => a.Id == admissionId)
            .Select(a => new { a.Id, a.PatientId, a.ConsultantPractitionerId, a.AdmittingPractitionerId })
            .FirstOrDefaultAsync(ct);
        return stay is null ? null : await TeamAsync(stay.Id, stay.PatientId, stay.ConsultantPractitionerId, stay.AdmittingPractitionerId, ct);
    }

    public async Task<StayCareTeam?> CurrentStayAsync(Guid patientId, CancellationToken ct)
    {
        var stay = await db.Set<Admission>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(a => a.PatientId == patientId && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated))
            .OrderByDescending(a => a.AdmittedAt)
            .Select(a => new { a.Id, a.PatientId, a.ConsultantPractitionerId, a.AdmittingPractitionerId })
            .FirstOrDefaultAsync(ct);
        return stay is null ? null : await TeamAsync(stay.Id, stay.PatientId, stay.ConsultantPractitionerId, stay.AdmittingPractitionerId, ct);
    }

    private async Task<StayCareTeam> TeamAsync(Guid admissionId, Guid patientId, Guid? consultant, Guid? admitting, CancellationToken ct)
    {
        var visiting = await db.Set<ConsultantVisit>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(v => v.AdmissionId == admissionId).Select(v => v.PractitionerId).Distinct().ToListAsync(ct);
        var doctors = visiting.ToHashSet();
        if (admitting is { } a)
        {
            doctors.Add(a);
        }

        return new StayCareTeam(admissionId, patientId, consultant, doctors);
    }
}
