using System.Globalization;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Platform.Messaging;

namespace HyFib.HealthCare360.Modules.Engagement.Services.Handlers;

/// <summary>
/// A vaccine dose (CRM-02) completes the reminders started by earlier doses of the same vaccine, and its next-due date
/// starts the reminder for the next dose. A next-due date that is not after today starts nothing: the patient is with
/// the clinician recording it. Doses transcribed from the card in any order close only reminders of older doses.
/// </summary>
internal sealed class ImmunizationRecordedHandler(HospitalDbContext db, TimeProvider time) : IDomainEventHandler<ImmunizationRecorded>
{
    public async Task HandleAsync(ImmunizationRecorded recorded, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var given = recorded.AdministeredOn.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        var waiting = (await AutomaticFollowUps.OpenTasksAsync(db, recorded.PatientId, cancellationToken))
            .Where(t => t.Type == FollowUpType.Vaccination && FollowUpAutomation.ImmunizationOf(t.SourceReference) is { } dose && dose != recorded.ImmunizationId)
            .ToList();
        if (waiting.Count > 0)
        {
            var doseIds = waiting.Select(t => FollowUpAutomation.ImmunizationOf(t.SourceReference)!.Value).ToList();
            var doses = await db.Set<Immunization>().AsNoTracking()
                .Where(i => doseIds.Contains(i.Id))
                .Select(i => new { i.Id, i.VaccineName, i.VaccineCode, i.AdministeredOn })
                .ToDictionaryAsync(i => i.Id, cancellationToken);
            foreach (var task in waiting)
            {
                if (doses.TryGetValue(FollowUpAutomation.ImmunizationOf(task.SourceReference)!.Value, out var earlier)
                    && recorded.AdministeredOn > earlier.AdministeredOn
                    && FollowUpAutomation.IsSameVaccine(earlier.VaccineName, earlier.VaccineCode, recorded.VaccineName, recorded.VaccineCode))
                {
                    task.CompleteAsVisited($"{recorded.VaccineName} given on {given}.", now);
                }
            }
        }

        if (recorded.NextDueOn is not { } nextDue || nextDue <= EngagementClock.LocalDate(now) || recorded.FacilityId is not { } facilityId
            || !await AutomaticFollowUps.CanFollowUpAsync(db, recorded.PatientId, cancellationToken))
        {
            return;
        }

        var visitId = recorded.EncounterId is { } encounterId
            ? await db.Set<Encounter>().Where(e => e.Id == encounterId).Select(e => e.VisitId).FirstOrDefaultAsync(cancellationToken)
            : null;
        await AutomaticFollowUps.AddOnceAsync(db, new FollowUpTask
        {
            FacilityId = facilityId,
            PatientId = recorded.PatientId,
            VisitId = visitId,
            Type = FollowUpType.Vaccination,
            DueDate = nextDue,
            Status = FollowUpStatus.Open,
            Source = FollowUpSource.System,
            SourceReference = FollowUpAutomation.ImmunizationReference(recorded.ImmunizationId),
            Notes = recorded.DoseNumber is { } number
                ? $"Next dose of {recorded.VaccineName} (dose {number + 1}) after the dose of {given}."
                : $"Next dose of {recorded.VaccineName} after the dose of {given}.",
        }, cancellationToken);
    }
}

/// <summary>A dose marked as entered in error cancels the reminder it started.</summary>
internal sealed class ImmunizationEnteredInErrorHandler(HospitalDbContext db) : IDomainEventHandler<ImmunizationEnteredInError>
{
    public async Task HandleAsync(ImmunizationEnteredInError voided, CancellationToken cancellationToken)
    {
        var reference = FollowUpAutomation.ImmunizationReference(voided.ImmunizationId);
        var tasks = await db.Set<FollowUpTask>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(t => t.Source == FollowUpSource.System && t.SourceReference == reference
                        && (t.Status == FollowUpStatus.Open || t.Status == FollowUpStatus.InProgress))
            .ToListAsync(cancellationToken);
        foreach (var task in tasks)
        {
            task.Cancel("The dose was marked as entered in error.");
        }
    }
}
