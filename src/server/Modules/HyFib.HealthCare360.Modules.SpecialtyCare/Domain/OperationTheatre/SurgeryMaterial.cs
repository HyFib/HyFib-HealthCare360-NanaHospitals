using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

public enum MaterialKind
{
    Implant = 0,
    Consumable,
}

/// <summary>
/// An implant or consumable used in a surgery (OT-06). Each row is billed as its own ad-hoc charge at the actual
/// price, and implants keep manufacturer/lot/serial so every patient who received a recalled device can be found.
/// </summary>
public sealed class SurgeryMaterial : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid SurgeryBookingId { get; set; }
    public Guid PatientId { get; set; }
    public MaterialKind Kind { get; set; }

    /// <summary>Item master id in SupplyChain, when the item came from the OT store.</summary>
    public Guid? SupplyItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? LotNumber { get; set; }
    public string? SerialNumber { get; set; }
    public DateOnly? ExpiryDate { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public string? RecordedByName { get; set; }

    public ChargeStatus ChargeStatus { get; set; }
    public string? ChargeError { get; set; }

    public decimal Amount => Money.Round(Quantity * UnitPrice);

    /// <summary>
    /// Traceability rule: an implant without a lot or serial number could not be recalled, and an expired implant
    /// must never be used.
    /// </summary>
    public static Result Validate(MaterialKind kind, string? lotNumber, string? serialNumber, DateOnly? expiryDate, DateOnly today)
    {
        if (kind == MaterialKind.Implant && string.IsNullOrWhiteSpace(lotNumber) && string.IsNullOrWhiteSpace(serialNumber))
        {
            return Error.Field("lotNumber", "Enter the lot or serial number of the implant (needed for device recalls).");
        }

        return expiryDate is { } expiry && expiry < today
            ? Error.Rule("material.expired", "This item is past its expiry date and must not be used.")
            : Result.Success();
    }
}
