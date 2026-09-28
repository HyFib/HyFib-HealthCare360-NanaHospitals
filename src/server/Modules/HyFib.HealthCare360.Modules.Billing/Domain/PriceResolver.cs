namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum PriceSource
{
    BasePrice = 0,
    TariffPlan,
    Override,
    FreeFollowUp,
}

/// <summary>The catalogue facts pricing needs (free of EF types so the rules stay unit-testable).</summary>
public sealed record PricingItem(Guid ServiceItemId, ServiceCategory Category, decimal BasePrice);

public sealed record PlanPrice(Guid Id, decimal Price, DateOnly EffectiveFrom, DateOnly? EffectiveTo);

public sealed record PriceQuote(decimal UnitPrice, PriceSource Source, decimal Multiplier, Guid? TariffPriceId);

/// <summary>
/// Pure pricing rules (MB-02). Order of precedence: an explicit override (pharmacy MRP, implant cost, doctor fee),
/// a free follow-up, the plan price effective on the service date, and finally the catalogue base price.
/// Bed-class multipliers apply only to services that are priced by room category.
/// </summary>
public static class PriceResolver
{
    private static readonly HashSet<ServiceCategory> BedClassSensitive =
        [ServiceCategory.Procedure, ServiceCategory.Surgery, ServiceCategory.Nursing, ServiceCategory.DoctorVisit];

    public static bool IsBedClassSensitive(ServiceCategory category) => BedClassSensitive.Contains(category);

    public static PriceQuote Resolve(
        PricingItem item,
        IEnumerable<PlanPrice> planPrices,
        DateOnly serviceDate,
        decimal bedMultiplier = 1m,
        bool isFreeFollowUp = false,
        decimal? unitPriceOverride = null)
    {
        if (unitPriceOverride is { } overridden)
        {
            return new PriceQuote(Money.Round(Math.Max(overridden, 0m)), PriceSource.Override, 1m, null);
        }

        if (isFreeFollowUp)
        {
            return new PriceQuote(0m, PriceSource.FreeFollowUp, 1m, null);
        }

        var multiplier = bedMultiplier > 0 && IsBedClassSensitive(item.Category) ? bedMultiplier : 1m;
        var effective = planPrices
            .Where(p => p.EffectiveFrom <= serviceDate && (p.EffectiveTo is null || serviceDate <= p.EffectiveTo.Value))
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefault();

        return effective is null
            ? new PriceQuote(Money.Round(Math.Max(item.BasePrice, 0m) * multiplier), PriceSource.BasePrice, multiplier, null)
            : new PriceQuote(Money.Round(Math.Max(effective.Price, 0m) * multiplier), PriceSource.TariffPlan, multiplier, effective.Id);
    }

    /// <summary>
    /// Price override for an automatic consultation charge. A negotiated payer tariff (CGHS, insurer, corporate) wins
    /// over the doctor's private fee; otherwise the consulting doctor's own fee applies; with neither, the plan/base
    /// price of the consultation item is used. Free follow-ups are handled by <see cref="Resolve"/>.
    /// </summary>
    public static decimal? ConsultationOverride(bool payerPlanHasPrice, decimal practitionerFee) =>
        payerPlanHasPrice || practitionerFee <= 0 ? null : practitionerFee;
}
