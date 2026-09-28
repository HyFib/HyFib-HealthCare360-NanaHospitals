using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Clinical.Services.Handlers;

/// <summary>
/// A lab/imaging/cardiology report was released: complete the ordered line and put the report on the ordering doctor's
/// "results to review" list (EMR-01). A re-released (amended) report re-opens the review so a changed result is seen.
/// </summary>
internal sealed class DiagnosticReportReleasedHandler(HospitalDbContext db) : IDomainEventHandler<DiagnosticReportReleased>
{
    public async Task HandleAsync(DiagnosticReportReleased domainEvent, CancellationToken cancellationToken)
    {
        ClinicalOrder? order = null;
        if (domainEvent.OrderItemId is { } itemId)
        {
            order = await db.Set<ClinicalOrder>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Items.Any(i => i.Id == itemId), cancellationToken);
            order?.UpdateItemStatus(itemId, OrderItemStatus.Completed, domainEvent.ReleasedAt, domainEvent.ReportId.ToString());
        }

        var review = await db.Set<ResultReview>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(r => r.ReportId == domainEvent.ReportId, cancellationToken);
        if (review is not null)
        {
            review.Reopen(domainEvent.Title, domainEvent.HasCriticalFindings, domainEvent.ReleasedAt);
            return;
        }

        db.Add(new ResultReview
        {
            FacilityId = order?.FacilityId ?? Guid.Empty,
            PatientId = domainEvent.PatientId,
            ReportId = domainEvent.ReportId,
            Kind = domainEvent.Kind,
            Title = domainEvent.Title,
            HasCriticalFindings = domainEvent.HasCriticalFindings,
            ReleasedAt = domainEvent.ReleasedAt,
            OrderId = order?.Id,
            OrderItemId = domainEvent.OrderItemId,
            VisitId = domainEvent.VisitId ?? order?.VisitId,
            EncounterId = order?.EncounterId,
            PractitionerId = order?.OrderingPractitionerId,
        });
    }
}
