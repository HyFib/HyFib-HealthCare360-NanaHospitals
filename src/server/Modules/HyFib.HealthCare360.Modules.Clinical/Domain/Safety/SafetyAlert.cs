namespace HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

public enum SafetyAlertKind
{
    Allergy = 0,
    ClassAllergy,
    CrossSensitivity,
    DuplicateTherapy,
    Interaction,
    MaxDose,

    /// <summary>A dose limit exists but the dose could not be computed from the text entered.</summary>
    DoseUnverified,

    /// <summary>High-alert medication pattern (for example daily methotrexate).</summary>
    HighAlertMedication,

    /// <summary>Schedule H1/X medicine — needs explicit confirmation (enforced when signing).</summary>
    ControlledDrug,
}

public enum SafetyAlertSeverity
{
    Info = 0,
    Minor,
    Moderate,
    Major,
}

/// <summary>
/// One prescribing safety finding. <see cref="Key"/> is stable across re-checks (used to de-duplicate and to record
/// what was acknowledged); <see cref="IsBlocking"/> alerts stop signing unless the prescriber gives an override reason.
/// </summary>
public sealed record SafetyAlert(
    string Key,
    SafetyAlertKind Kind,
    SafetyAlertSeverity Severity,
    bool IsBlocking,
    string Message,
    string? Detail,
    IReadOnlyList<Guid> ItemIds);

/// <summary>A medicine as seen by the safety checks (a line being prescribed, or a current medication).</summary>
public sealed record SafetyDrug(
    Guid Id,
    string DrugName,
    string? GenericName,
    string? Strength,
    string? Route,
    string Dose,
    DoseFrequency Frequency,
    string? CustomFrequency,
    decimal? DosesPerDay,
    bool IsControlledDrug,
    bool IsCurrentMedication = false,
    string? PrescriptionNumber = null)
{
    public IReadOnlyList<string> Components { get; } = DrugNames.Components(GenericName, DrugName);

    public string Label => IsCurrentMedication
        ? $"{DrugName} (already prescribed{(PrescriptionNumber is null ? string.Empty : $" on {PrescriptionNumber}")})"
        : DrugName;
}

public sealed record SafetyAllergy(string Allergen, AllergyType Type, AllergySeverity Severity, string? Reaction);

public sealed record SafetyInteractionRule(string DrugA, string DrugB, InteractionSeverity Severity, string Effect, string? Management);

public sealed record SafetyDoseLimit(string GenericName, decimal MaxDailyDoseMg, string? Route);

public sealed record SafetyCheckInput(
    IReadOnlyList<SafetyDrug> Items,
    IReadOnlyList<SafetyDrug> CurrentMedications,
    IReadOnlyList<SafetyAllergy> Allergies,
    IReadOnlyList<SafetyInteractionRule> Interactions,
    IReadOnlyList<SafetyDoseLimit> DoseLimits);

public sealed record SafetyCheckResult(IReadOnlyList<SafetyAlert> Alerts)
{
    public bool IsBlocked => Alerts.Any(a => a.IsBlocking);

    public IReadOnlyList<SafetyAlert> BlockingAlerts => Alerts.Where(a => a.IsBlocking).ToList();

    /// <summary>Non-blocking findings the prescriber must acknowledge (controlled drugs have their own confirmation).</summary>
    public IReadOnlyList<SafetyAlert> Warnings => Alerts.Where(a => !a.IsBlocking && a.Kind != SafetyAlertKind.ControlledDrug).ToList();
}
