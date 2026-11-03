using System.Globalization;
using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>The patients admitted under a doctor as their consultant, while the stay is in progress (discharge started too). Every facility.</summary>
internal sealed class InpatientPractitionerWork(HospitalDbContext db) : IPractitionerWorkSource
{
    public async Task<IReadOnlyList<PractitionerWork>> WorkAheadAsync(Guid practitionerId, CancellationToken cancellationToken)
    {
        var stays = await db.Set<Admission>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => a.ConsultantPractitionerId == practitionerId
                        && (a.Status == AdmissionStatus.Admitted || a.Status == AdmissionStatus.DischargeInitiated))
            .OrderBy(a => a.AdmittedAt)
            .Select(a => new { a.AdmissionNumber, a.AdmittedAt })
            .ToListAsync(cancellationToken);
        return stays.Count == 0
            ? []
            :
            [
                new PractitionerWork("admissions", stays.Count, stays[0].AdmittedAt,
                    stays.Take(PractitionerWorkSources.Named).Select(s => s.AdmissionNumber).ToList(),
                    $"{stays.Count} patient(s) admitted under them, the first since " +
                    TimeZoneInfo.ConvertTime(stays[0].AdmittedAt, HospitalTimeZone.Zone).ToString("dd MMM yyyy", CultureInfo.InvariantCulture)),
            ];
    }
}
