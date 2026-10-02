using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>
/// A signed consultation (CRM-02) settles the "come back" tasks it fulfils and schedules the next ones, inside the
/// signing transaction:
/// <list type="bullet">
/// <item>open "after consultation" and "chronic review" tasks falling due within
/// <see cref="FollowUpAutomation.FulfilmentWindowDays"/> days complete as visited;</item>
/// <item>a follow-up date adds an "after consultation" task due on it;</item>
/// <item>a patient with a chronic problem gets the next chronic review, which replaces any earlier automatic one.</item>
/// </list>
/// In-patient notes start nothing: the discharge starts the post-discharge call. Task notes carry no clinical details,
/// because the CRM team does not see the chart.
/// </summary>
internal sealed class EncounterSignedHandler(HospitalDbContext db, EngagementSettings settings, TimeProvider time) : IDomainEventHandler<EncounterSigned>
{
    public async Task HandleAsync(EncounterSigned signed, CancellationToken cancellationToken)
    {
        if (signed.AdmissionId is not null || signed.Type == EncounterType.InPatient
            || !await AutomaticFollowUps.CanFollowUpAsync(db, signed.PatientId, cancellationToken))
        {
            return;
        }

        var now = time.GetUtcNow();
        var seenOn = EngagementClock.LocalDate(signed.SignedAt);
        var seen = seenOn.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        var open = await AutomaticFollowUps.OpenTasksAsync(db, signed.PatientId, cancellationToken);
        foreach (var task in open.Where(t => FollowUpAutomation.ClosesOnVisit(t.Type) && FollowUpAutomation.IsAround(t.DueDate, seenOn)))
        {
            task.CompleteAsVisited($"Seen on {seen}.", now);
        }

        if (signed.FollowUpDate is { } followUpDate && followUpDate > seenOn)
        {
            var doctor = await db.Set<Practitioner>().Where(p => p.Id == signed.PractitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(cancellationToken);
            await AutomaticFollowUps.AddOnceAsync(db, new FollowUpTask
            {
                FacilityId = signed.FacilityId,
                PatientId = signed.PatientId,
                VisitId = signed.VisitId,
                Type = FollowUpType.PostConsultation,
                DueDate = followUpDate,
                Status = FollowUpStatus.Open,
                Source = FollowUpSource.System,
                SourceReference = FollowUpAutomation.ConsultationReference(signed.EncounterId),
                Notes = $"Follow-up advised by {doctor ?? "the doctor"} on {seen}.",
            }, cancellationToken);
        }

        var hasChronicProblem = await db.Set<Diagnosis>().AnyAsync(
            d => d.PatientId == signed.PatientId && d.Type != DiagnosisType.Differential && d.Status == DiagnosisStatus.Chronic, cancellationToken);
        if (!hasChronicProblem)
        {
            return;
        }

        var reference = FollowUpAutomation.ChronicReviewReference(signed.EncounterId);
        foreach (var earlier in open.Where(t => t.IsOpen && t.Type == FollowUpType.ChronicReview && t.Source == FollowUpSource.System && t.SourceReference != reference))
        {
            earlier.Cancel($"Replaced by the next review after the visit on {seen}.");
        }

        var reviewOn = FollowUpAutomation.ChronicReviewDate(seenOn, await settings.ChronicReviewMonthsAsync(signed.FacilityId, cancellationToken));
        if (FollowUpAutomation.NeedsChronicReview(signed.FollowUpDate, reviewOn))
        {
            await AutomaticFollowUps.AddOnceAsync(db, new FollowUpTask
            {
                FacilityId = signed.FacilityId,
                PatientId = signed.PatientId,
                VisitId = signed.VisitId,
                Type = FollowUpType.ChronicReview,
                DueDate = reviewOn,
                Status = FollowUpStatus.Open,
                Source = FollowUpSource.System,
                SourceReference = reference,
                Notes = $"Chronic care review after the visit on {seen}.",
            }, cancellationToken);
        }
    }
}
