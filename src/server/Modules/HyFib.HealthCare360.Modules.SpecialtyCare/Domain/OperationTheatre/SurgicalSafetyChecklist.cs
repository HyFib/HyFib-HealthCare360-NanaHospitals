using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>The three pauses of the WHO Surgical Safety Checklist (2009).</summary>
public enum SafetyPhase
{
    /// <summary>Before induction of anaesthesia.</summary>
    SignIn = 0,

    /// <summary>Before skin incision.</summary>
    TimeOut,

    /// <summary>Before the patient leaves the operating room.</summary>
    SignOut,
}

/// <summary>
/// WHO Surgical Safety Checklist (OT-03). Items follow the WHO 2009 wording; each is confirmed individually with
/// the confirming person and time. A phase is complete when every item is confirmed (or validly not applicable);
/// completed phases are locked so the record cannot be altered after the fact.
/// </summary>
public static class SurgicalSafetyChecklist
{
    public static IReadOnlyDictionary<SafetyPhase, IReadOnlyList<ChecklistItemDefinition>> Phases { get; } =
        new Dictionary<SafetyPhase, IReadOnlyList<ChecklistItemDefinition>>
        {
            [SafetyPhase.SignIn] =
            [
                new("identity_confirmed", "Patient has confirmed identity, site, procedure and consent", false),
                new("site_marked", "Site marked", true),
                new("anaesthesia_check", "Anaesthesia machine and medication check complete", false),
                new("pulse_oximeter", "Pulse oximeter on the patient and functioning", false),
                new("allergy_reviewed", "Known allergy reviewed", false),
                new("airway_risk", "Difficult airway / aspiration risk assessed; equipment and assistance available if needed", false),
                new("blood_loss_risk", "Risk of >500 ml blood loss (7 ml/kg in children) assessed; IV access and fluids planned", false),
            ],
            [SafetyPhase.TimeOut] =
            [
                new("team_introduced", "All team members introduced by name and role", false),
                new("patient_site_procedure", "Surgeon, anaesthetist and nurse confirm patient, site and procedure", false),
                new("surgeon_critical_events", "Surgeon reviews critical steps, expected duration and anticipated blood loss", false),
                new("anaesthesia_concerns", "Anaesthesia team reviews patient-specific concerns", false),
                new("sterility_confirmed", "Nursing team confirms sterility (including indicator results) and equipment", false),
                new("antibiotic_prophylaxis", "Antibiotic prophylaxis given within the last 60 minutes", true),
                new("imaging_displayed", "Essential imaging displayed", true),
            ],
            [SafetyPhase.SignOut] =
            [
                new("procedure_recorded", "Name of the procedure recorded", false),
                new("counts_correct", "Instrument, sponge and needle counts are correct", true),
                new("specimens_labelled", "Specimens labelled (labels read aloud, including patient name)", true),
                new("equipment_problems", "Equipment problems identified and addressed", false),
                new("recovery_concerns", "Key concerns for recovery and management reviewed by surgeon, anaesthetist and nurse", false),
            ],
        };

    public static List<SafetyChecklistEntry> NewEntries() =>
        Phases.SelectMany(p => p.Value.Select(i => new SafetyChecklistEntry(p.Key, i.Key, ChecklistItemState.Pending, null, null, null, null)))
            .ToList();

    public static bool IsPhaseComplete(IReadOnlyCollection<SafetyChecklistEntry> entries, SafetyPhase phase) =>
        Phases[phase].All(i => entries.Any(e => e.Phase == phase && e.Key == i.Key && e.State != ChecklistItemState.Pending));

    /// <summary>The phase that must be completed before <paramref name="phase"/> may be recorded.</summary>
    public static SafetyPhase? Prerequisite(SafetyPhase phase) => phase switch
    {
        SafetyPhase.TimeOut => SafetyPhase.SignIn,
        SafetyPhase.SignOut => SafetyPhase.TimeOut,
        _ => null,
    };

    /// <summary>
    /// Confirms items of one phase. A confirmed item is an attestation: it cannot be withdrawn, and re-submitting it
    /// keeps the original confirmer and time (so a client may safely send the whole phase again).
    /// </summary>
    public static Result Apply(List<SafetyChecklistEntry> entries, SafetyPhase phase, IReadOnlyList<ChecklistInput> inputs, StaffStamp by)
    {
        var phaseEntries = entries.Where(e => e.Phase == phase)
            .Select(e => new ChecklistEntry(e.Key, e.State, e.Remarks, e.ConfirmedByUserId, e.ConfirmedByName, e.ConfirmedAt))
            .ToList();
        var confirmed = phaseEntries.Where(e => e.State != ChecklistItemState.Pending).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var fresh = inputs.Where(i => !confirmed.Contains(i.Key)).ToList();

        var result = ChecklistRules.Apply(Phases[phase], phaseEntries, fresh, by, allowReset: false);
        if (result.IsFailure)
        {
            return result;
        }

        entries.RemoveAll(e => e.Phase == phase);
        entries.AddRange(phaseEntries.Select(e => new SafetyChecklistEntry(phase, e.Key, e.State, e.Remarks, e.RecordedByUserId, e.RecordedByName, e.RecordedAt)));
        return Result.Success();
    }
}

/// <summary>A confirmed (or pending) WHO checklist item.</summary>
public sealed record SafetyChecklistEntry(
    SafetyPhase Phase,
    string Key,
    ChecklistItemState State,
    string? Remarks,
    Guid? ConfirmedByUserId,
    string? ConfirmedByName,
    DateTimeOffset? ConfirmedAt);
