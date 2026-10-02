using System.Globalization;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>
/// A booked appointment closes the loop on CRM work (CRM-01/02) in two ways:
/// <list type="bullet">
/// <item>Open leads for the same person (same mobile and matching name) are marked converted.</item>
/// <item>Open "come back" follow-ups (review, vaccination, chronic review, check-up due) are completed as booked when
/// the appointment is near or after their due date (<see cref="FollowUpAutomation.FulfilmentWindowDays"/>). An earlier
/// appointment, likely for something else, leaves them open.</item>
/// </list>
/// Post-discharge, payment and feedback calls stay open, because they are still needed.
/// </summary>
internal sealed class AppointmentBookedHandler(HospitalDbContext db, TimeProvider time) : IDomainEventHandler<AppointmentBooked>
{
    public async Task HandleAsync(AppointmentBooked booked, CancellationToken cancellationToken)
    {
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == booked.PatientId)
            .Select(p => new { p.Phone, p.FullName })
            .FirstOrDefaultAsync(cancellationToken);
        if (patient is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (patient.Phone is { } phone)
        {
            var leads = await db.Set<Lead>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(l => l.Phone == phone && (l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified))
                .ToListAsync(cancellationToken);
            foreach (var lead in leads.Where(l => LeadMatching.IsSamePerson(l.Name, patient.FullName)))
            {
                lead.Convert(booked.PatientId, booked.AppointmentId, now);
            }
        }

        var tasks = await db.Set<FollowUpTask>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(t => t.PatientId == booked.PatientId && (t.Status == FollowUpStatus.Open || t.Status == FollowUpStatus.InProgress))
            .ToListAsync(cancellationToken);
        var note = $"Appointment booked for {EngagementClock.ToLocal(booked.StartsAt).ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture)} ({booked.Channel}).";
        var bookedFor = EngagementClock.LocalDate(booked.StartsAt);
        foreach (var task in tasks.Where(t => FollowUpTask.ClosesOnBooking(t.Type) && FollowUpAutomation.IsAround(t.DueDate, bookedFor)))
        {
            task.CompleteAsBooked(note, now);
        }
    }
}
