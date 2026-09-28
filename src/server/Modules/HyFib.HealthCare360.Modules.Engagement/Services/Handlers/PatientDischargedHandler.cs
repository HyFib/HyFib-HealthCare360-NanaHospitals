using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>
/// After an in-patient discharge (CRM-02, FB-02), this handler adds two things:
/// <list type="bullet">
/// <item>a post-discharge call on the CRM worklist, due two days later;</item>
/// <item>a personal feedback link sent by SMS.</item>
/// </list>
/// The handler runs inside the discharge transaction. It is idempotent, and it never fails the discharge for an
/// Engagement reason: a missing form, consent or portal address only skips the survey, and the reason is logged.
/// </summary>
internal sealed class PatientDischargedHandler(
    HospitalDbContext db,
    FeedbackInvitationService invitations,
    ILogger<PatientDischargedHandler> logger) : IDomainEventHandler<PatientDischarged>
{
    public async Task HandleAsync(PatientDischarged discharged, CancellationToken cancellationToken)
    {
        var visit = await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(v => v.Id == discharged.VisitId)
            .Select(v => new { v.FacilityId })
            .FirstOrDefaultAsync(cancellationToken);
        var isDeceased = await db.Set<Patient>().Where(p => p.Id == discharged.PatientId).Select(p => p.IsDeceased).FirstOrDefaultAsync(cancellationToken);
        if (visit is null || isDeceased)
        {
            return;
        }

        if (DischargeFollowUpPolicy.ShouldCall(discharged.DischargeType))
        {
            await AddCallAsync(discharged, visit.FacilityId, cancellationToken);
        }

        if (DischargeFollowUpPolicy.ShouldRequestFeedback(discharged.DischargeType))
        {
            var invited = await invitations.InviteAsync(
                new FeedbackInviteRequest(visit.FacilityId, discharged.PatientId, discharged.VisitId, discharged.AdmissionId, FeedbackFormType.IP, null),
                cancellationToken);
            if (invited.IsFailure)
            {
                logger.LogInformation("No feedback link for admission {AdmissionId}: {Reason}", discharged.AdmissionId, invited.Error!.Code);
            }
        }
    }

    private async Task AddCallAsync(PatientDischarged discharged, Guid facilityId, CancellationToken ct)
    {
        var reference = DischargeFollowUpPolicy.SourceReference(discharged.AdmissionId);
        if (db.Set<FollowUpTask>().Local.Any(t => t.SourceReference == reference && t.Type == FollowUpType.PostDischarge)
            || await db.Set<FollowUpTask>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .AnyAsync(t => t.Source == FollowUpSource.Discharge && t.SourceReference == reference && t.Type == FollowUpType.PostDischarge, ct))
        {
            return;
        }

        db.Add(new FollowUpTask
        {
            FacilityId = facilityId,
            PatientId = discharged.PatientId,
            VisitId = discharged.VisitId,
            AdmissionId = discharged.AdmissionId,
            Type = FollowUpType.PostDischarge,
            DueDate = DischargeFollowUpPolicy.CallDate(EngagementClock.LocalDate(discharged.DischargedAt)),
            Priority = DischargeFollowUpPolicy.PriorityFor(discharged.DischargeType),
            Status = FollowUpStatus.Open,
            Source = FollowUpSource.Discharge,
            SourceReference = reference,
            Notes = $"Post-discharge call ({discharged.DischargeType} discharge).",
        });
    }
}
