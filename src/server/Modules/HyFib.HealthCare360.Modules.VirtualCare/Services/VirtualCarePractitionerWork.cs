using System.Globalization;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// A doctor's video consultations not yet held that no OPD appointment stands for (those are counted as appointments):
/// scheduled, in the waiting room or in progress. Every facility.
/// </summary>
internal sealed class VirtualCarePractitionerWork(HospitalDbContext db) : IPractitionerWorkSource
{
    public async Task<IReadOnlyList<PractitionerWork>> WorkAheadAsync(Guid practitionerId, CancellationToken cancellationToken)
    {
        var sessions = await db.Set<TelehealthSession>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.PractitionerId == practitionerId && s.AppointmentId == null
                        && (s.Status == TelehealthStatus.Scheduled || s.Status == TelehealthStatus.WaitingRoom || s.Status == TelehealthStatus.InProgress))
            .OrderBy(s => s.ScheduledStartAt)
            .Select(s => s.ScheduledStartAt)
            .ToListAsync(cancellationToken);
        return sessions.Count == 0
            ? []
            :
            [
                new PractitionerWork("telehealth", sessions.Count, sessions[0], [],
                    $"{sessions.Count} video consultation(s) from " +
                    TimeZoneInfo.ConvertTime(sessions[0], HospitalTimeZone.Zone).ToString("dd MMM yyyy", CultureInfo.InvariantCulture)),
            ];
    }
}
