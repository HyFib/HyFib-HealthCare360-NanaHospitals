using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>
/// A completed health check-up (CRM-02) completes the patient's open "check-up due" tasks and schedules the next
/// check-up <c>crm.healthCheckupRecallMonths</c> later (a year by default). A corporate check-up starts no recall,
/// because the employer arranges the next one.
/// </summary>
internal sealed class HealthCheckupCompletedHandler(HospitalDbContext db, EngagementSettings settings, TimeProvider time)
    : IDomainEventHandler<HealthCheckupCompleted>
{
    public async Task HandleAsync(HealthCheckupCompleted completed, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var completedOn = EngagementClock.LocalDate(completed.CompletedAt);
        var done = completedOn.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        foreach (var task in (await AutomaticFollowUps.OpenTasksAsync(db, completed.PatientId, cancellationToken)).Where(t => t.Type == FollowUpType.HealthCheckupDue))
        {
            task.CompleteAsVisited($"Health check-up done on {done}.", now);
        }

        if (completed.CorporateBatchId is not null || !await AutomaticFollowUps.CanFollowUpAsync(db, completed.PatientId, cancellationToken))
        {
            return;
        }

        var booking = await db.Set<PackageBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => b.Id == completed.BookingId)
            .Select(b => new { b.PackageName, b.VisitId })
            .FirstOrDefaultAsync(cancellationToken);
        var months = await settings.HealthCheckupRecallMonthsAsync(completed.FacilityId, cancellationToken);
        await AutomaticFollowUps.AddOnceAsync(db, new FollowUpTask
        {
            FacilityId = completed.FacilityId,
            PatientId = completed.PatientId,
            VisitId = booking?.VisitId,
            Type = FollowUpType.HealthCheckupDue,
            DueDate = FollowUpAutomation.HealthCheckupRecallDate(completedOn, months),
            Status = FollowUpStatus.Open,
            Source = FollowUpSource.System,
            SourceReference = FollowUpAutomation.HealthCheckupReference(completed.BookingId),
            Notes = $"{booking?.PackageName ?? "Health check-up"} done on {done}; the next check-up is due.",
        }, cancellationToken);
    }
}
