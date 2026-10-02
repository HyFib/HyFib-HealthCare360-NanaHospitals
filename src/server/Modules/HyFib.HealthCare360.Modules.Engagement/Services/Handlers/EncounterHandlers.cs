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

        if (FollowUpAutomation.ConsultationDueDate(signed.FollowUpDate, seenOn) is { } due)
        {
            await ConsultationFollowUp.AddAsync(db, signed.EncounterId, signed.FacilityId, signed.PatientId, signed.VisitId, signed.PractitionerId, due, seenOn,
                cancellationToken);
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

/// <summary>
/// An amended consultation note keeps its "after consultation" follow-up in step (CRM-02): a follow-up date added
/// starts the task, a changed one moves it (and the patient is reminded of the new date), and a removed one cancels it.
/// A closed follow-up is never reopened; the chronic review is left to the next visit.
/// </summary>
internal sealed class EncounterAmendedHandler(HospitalDbContext db) : IDomainEventHandler<EncounterAmended>
{
    public async Task HandleAsync(EncounterAmended amended, CancellationToken cancellationToken)
    {
        // The amended note is saved in this unit of work, so the tracked encounter holds the new follow-up date.
        var encounter = await db.Set<Encounter>().FindAsync([amended.EncounterId], cancellationToken);
        if (encounter?.SignedAt is not { } signedAt || encounter.AdmissionId is not null || encounter.Type == EncounterType.InPatient)
        {
            return;
        }

        var seenOn = EngagementClock.LocalDate(signedAt);
        var due = FollowUpAutomation.ConsultationDueDate(encounter.FollowUpDate, seenOn);
        var reference = FollowUpAutomation.ConsultationReference(encounter.Id);
        var task = await db.Set<FollowUpTask>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(t => t.Source == FollowUpSource.System && t.SourceReference == reference && t.Type == FollowUpType.PostConsultation,
                cancellationToken);
        if (task is null)
        {
            if (due is { } added && await AutomaticFollowUps.CanFollowUpAsync(db, encounter.PatientId, cancellationToken))
            {
                await ConsultationFollowUp.AddAsync(db, encounter.Id, encounter.FacilityId, encounter.PatientId, encounter.VisitId, encounter.PractitionerId,
                    added, seenOn, cancellationToken);
            }

            return;
        }

        if (!task.IsOpen)
        {
            return;
        }

        if (due is { } moved)
        {
            task.Reschedule(moved);
        }
        else
        {
            var amendedOn = EngagementClock.LocalDate(amended.AmendedAt).ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
            task.Cancel($"The follow-up date was removed when the note was amended on {amendedOn}.");
        }
    }
}

/// <summary>The "after consultation" task a note's follow-up date starts, linked to the visit so the reminder names the doctor.</summary>
internal static class ConsultationFollowUp
{
    public static async Task AddAsync(
        HospitalDbContext db, Guid encounterId, Guid facilityId, Guid patientId, Guid? visitId, Guid practitionerId, DateOnly due, DateOnly seenOn,
        CancellationToken ct)
    {
        var doctor = await db.Set<Practitioner>().Where(p => p.Id == practitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
        await AutomaticFollowUps.AddOnceAsync(db, new FollowUpTask
        {
            FacilityId = facilityId,
            PatientId = patientId,
            VisitId = visitId,
            Type = FollowUpType.PostConsultation,
            DueDate = due,
            Status = FollowUpStatus.Open,
            Source = FollowUpSource.System,
            SourceReference = FollowUpAutomation.ConsultationReference(encounterId),
            Notes = $"Follow-up advised by {doctor ?? "the doctor"} on {seenOn.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}.",
        }, ct);
    }
}
