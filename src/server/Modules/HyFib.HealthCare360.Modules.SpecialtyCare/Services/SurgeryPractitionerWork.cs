using System.Globalization;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>
/// The surgeries a practitioner is booked for as surgeon, anaesthetist or assisting nurse / OT in-charge and that have not ended: asked for, scheduled, in
/// pre-op, in theatre, or postponed (waiting for a new date). Every facility.
/// </summary>
internal sealed class SurgeryPractitionerWork(HospitalDbContext db) : IPractitionerWorkSource
{
    public async Task<IReadOnlyList<PractitionerWork>> WorkAheadAsync(Guid practitionerId, CancellationToken cancellationToken)
    {
        var surgeries = await db.Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => (s.SurgeonId == practitionerId || s.AnaesthetistId == practitionerId || s.NurseId == practitionerId)
                        && (s.Status == SurgeryStatus.Requested || s.Status == SurgeryStatus.Scheduled
                            || s.Status == SurgeryStatus.InPreOp || s.Status == SurgeryStatus.InProgress || s.Status == SurgeryStatus.Postponed))
            .OrderBy(s => s.ScheduledStart == null)
            .ThenBy(s => s.ScheduledStart)
            .Select(s => new { s.BookingNumber, s.ScheduledStart })
            .ToListAsync(cancellationToken);
        if (surgeries.Count == 0)
        {
            return [];
        }

        var first = surgeries[0].ScheduledStart;
        return
        [
            new PractitionerWork("surgeries", surgeries.Count, first,
                surgeries.Take(PractitionerWorkSources.Named).Select(s => s.BookingNumber).ToList(),
                $"{surgeries.Count} surgery booking(s) with them as surgeon, anaesthetist or OT nurse" +
                (first is { } at ? ", the first on " + TimeZoneInfo.ConvertTime(at, HospitalTimeZone.Zone).ToString("dd MMM yyyy", CultureInfo.InvariantCulture) : string.Empty)),
        ];
    }
}
