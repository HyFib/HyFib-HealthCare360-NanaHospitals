namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum AllergyType
{
    Drug = 0,
    Food,
    Environmental,
    Other,
}

public enum AllergySeverity
{
    Mild = 0,
    Moderate,
    Severe,
    Anaphylaxis,
}

public enum AllergyStatus
{
    Active = 0,
    Inactive,
    Resolved,

    /// <summary>Recorded by mistake; kept for the audit trail but ignored by safety checks and the banner.</summary>
    EnteredInError,
}

/// <summary>
/// A patient allergy or intolerance (EHR-01/03). Active drug allergies drive the prescribing safety check, so the
/// allergen should be the generic drug or drug class ("Amoxicillin", "Penicillin", "Sulfa drugs", "NSAIDs").
/// </summary>
public sealed class Allergy : AuditableEntity
{
    public Guid PatientId { get; set; }
    public string Allergen { get; set; } = string.Empty;
    public AllergyType Type { get; set; }
    public string? Reaction { get; set; }
    public AllergySeverity Severity { get; set; }
    public AllergyStatus Status { get; private set; }
    public DateOnly? OnsetDate { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public string? Notes { get; set; }
    public string? StatusReason { get; private set; }

    public bool IsActive => Status == AllergyStatus.Active;

    /// <summary>Changing away from Active needs a reason because it silences prescribing alerts.</summary>
    public Result ChangeStatus(AllergyStatus status, string? reason)
    {
        if (Status == AllergyStatus.EnteredInError)
        {
            return Error.Rule("allergy.voided", "This allergy was marked as entered in error and cannot be changed. Record a new one.");
        }

        if (status != AllergyStatus.Active && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("statusReason", "Give a reason: this stops the allergy from appearing in prescribing checks.");
        }

        Status = status;
        StatusReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        return Result.Success();
    }
}
