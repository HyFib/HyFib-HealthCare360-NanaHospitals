namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum InteractionSeverity
{
    Minor = 0,
    Moderate,
    Major,
}

/// <summary>
/// A configurable drug–drug interaction (ERX-02). <see cref="DrugA"/>/<see cref="DrugB"/> are generic names or
/// ingredient phrases matched as whole words against each medicine's generic composition (so "warfarin" matches
/// "Warfarin sodium" and "potassium chloride" does not match "losartan potassium").
/// </summary>
public sealed class DrugInteraction : AuditableEntity
{
    public string DrugA { get; set; } = string.Empty;
    public string DrugB { get; set; } = string.Empty;
    public InteractionSeverity Severity { get; set; }

    /// <summary>What happens (for example "Additive bleeding risk").</summary>
    public string Effect { get; set; } = string.Empty;

    /// <summary>What to do (for example "Avoid; if essential monitor INR closely").</summary>
    public string? Management { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Maximum adult daily dose of a common medicine, used by the dose check before signing (ERX-02).</summary>
public sealed class DrugDoseLimit : AuditableEntity
{
    public string GenericName { get; set; } = string.Empty;
    public decimal MaxDailyDoseMg { get; set; }

    /// <summary>Route the limit applies to ("oral"); null = any route.</summary>
    public string? Route { get; set; }

    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Prescribing register of Schedule H1/X medicines (ERX-06): one append-only row per controlled line of every
/// signed prescription, recording who explicitly confirmed it.
/// </summary>
public sealed class ControlledDrugRegisterEntry : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PrescriptionId { get; set; }
    public string PrescriptionNumber { get; set; } = string.Empty;
    public Guid PrescriptionItemId { get; set; }
    public Guid PatientId { get; set; }
    public Guid PractitionerId { get; set; }
    public string DrugName { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Strength { get; set; }
    public string? Schedule { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset PrescribedAt { get; set; }
    public Guid? ConfirmedByUserId { get; set; }
}
