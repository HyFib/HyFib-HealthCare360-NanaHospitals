using System.Globalization;
using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// A doctor's OPD work ahead: appointments booked or confirmed from now on, patients with them in the clinic today (checked
/// in or in consultation; one left so from an earlier day is not work ahead), and patients waiting for a slot with them.
/// Every facility.
/// </summary>
internal sealed class SchedulingPractitionerWork(HospitalDbContext db, TimeProvider time) : IPractitionerWorkSource
{
    public async Task<IReadOnlyList<PractitionerWork>> WorkAheadAsync(Guid practitionerId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(now, HospitalTimeZone.Zone);
        var today = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
        List<PractitionerWork> work = [];
        var booked = await db.Set<Appointment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(a => a.PractitionerId == practitionerId
                        && (((a.Status == AppointmentStatus.Booked || a.Status == AppointmentStatus.Confirmed) && a.StartsAt > now)
                            || ((a.Status == AppointmentStatus.CheckedIn || a.Status == AppointmentStatus.InConsultation) && a.StartsAt >= today)))
            .OrderBy(a => a.StartsAt)
            .Select(a => new { a.Number, a.StartsAt, a.Status })
            .ToListAsync(cancellationToken);
        var inClinic = booked.Where(a => a.Status is AppointmentStatus.CheckedIn or AppointmentStatus.InConsultation).ToList();
        if (inClinic.Count > 0)
        {
            work.Add(new PractitionerWork("in-clinic", inClinic.Count, inClinic[0].StartsAt,
                inClinic.Take(PractitionerWorkSources.Named).Select(a => a.Number).ToList(),
                $"{inClinic.Count} patient(s) checked in to see them now"));
        }

        var ahead = booked.Where(a => a.Status is AppointmentStatus.Booked or AppointmentStatus.Confirmed).ToList();
        if (ahead.Count > 0)
        {
            work.Add(new PractitionerWork("appointments", ahead.Count, ahead[0].StartsAt,
                ahead.Take(PractitionerWorkSources.Named).Select(a => a.Number).ToList(),
                $"{ahead.Count} OPD appointment(s) booked from {Day(ahead[0].StartsAt)}"));
        }

        var waiting = await db.Set<WaitlistEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .CountAsync(w => w.PractitionerId == practitionerId && (w.Status == WaitlistStatus.Waiting || w.Status == WaitlistStatus.Offered), cancellationToken);
        if (waiting > 0)
        {
            work.Add(new PractitionerWork("waitlist", waiting, null, [], $"{waiting} patient(s) on the waiting list for a slot with them"));
        }

        return work;
    }

    private static string Day(DateTimeOffset at) =>
        TimeZoneInfo.ConvertTime(at, HospitalTimeZone.Zone).ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
}
