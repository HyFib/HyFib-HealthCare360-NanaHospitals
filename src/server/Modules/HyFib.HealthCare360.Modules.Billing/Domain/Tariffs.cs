namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum TariffType
{
    General = 0,
    Corporate,
    Insurance,
    GovernmentScheme,
    Staff,
}

/// <summary>
/// A price list (MB-02): general (walk-in), corporate, insurance/TPA, government scheme (PM-JAY, CGHS, ECHS) or staff.
/// Charges are priced from the plan of the visit/admission payer. Services a plan does not list fall back to the
/// catalogue base price, so a new plan only has to carry the negotiated exceptions.
/// </summary>
public sealed class TariffPlan : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public TariffType Type { get; set; }

    /// <summary>Payer the plan was negotiated with (insurer, TPA, corporate or scheme), if any.</summary>
    public Guid? PayerId { get; set; }

    /// <summary>Limits the plan to one facility; <c>null</c> applies to every facility of the hospital group.</summary>
    public Guid? FacilityId { get; set; }

    /// <summary>Concession applied to every discountable service (for example staff or charity plans).</summary>
    public decimal DefaultDiscountPercent { get; set; }

    /// <summary>The fallback plan for its <see cref="Type"/>; the General default prices self-paying patients.</summary>
    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Description { get; set; }

    /// <summary>Room-category pricing: in-patient procedures, visits and nursing cost more in private rooms and ICU.</summary>
    public List<TariffBedMultiplier> BedMultipliers { get; set; } = [];

    /// <summary>Multiplier for the admission's current bed class (1 when the bed class has no rule).</summary>
    public decimal MultiplierFor(Guid? bedServiceItemId) =>
        bedServiceItemId is { } bed && BedMultipliers.FirstOrDefault(m => m.BedServiceItemId == bed) is { Multiplier: > 0 } rule
            ? rule.Multiplier
            : 1m;
}

/// <summary>
/// Bed-class multiplier of a tariff plan, keyed by the bed's service item (BED-GEN, BED-PVT, BED-ICU…). Saving the plan
/// replaces them, so the plan's audit event lists them as they were and as they are (<c>bedMultipliers</c>).
/// </summary>
[AuditedWithParent]
public sealed class TariffBedMultiplier : Entity
{
    public Guid TariffPlanId { get; set; }
    public Guid BedServiceItemId { get; set; }
    public decimal Multiplier { get; set; } = 1m;
}

/// <summary>
/// Price of one service in one plan for a validity window. A revision is a new row starting later, so history is kept
/// and a bill for an earlier service date still resolves the price that applied on that date.
/// </summary>
public sealed class TariffPrice : AuditableEntity
{
    public Guid TariffPlanId { get; set; }
    public Guid ServiceItemId { get; set; }
    public decimal Price { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    public bool IsEffectiveOn(DateOnly date) => EffectiveFrom <= date && (EffectiveTo is null || date <= EffectiveTo.Value);
}
