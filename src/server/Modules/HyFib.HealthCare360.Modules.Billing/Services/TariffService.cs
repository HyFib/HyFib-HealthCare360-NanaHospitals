using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Decides which tariff plan prices a payer's services and quotes prices from it (MB-02).</summary>
public sealed class TariffService(HospitalDbContext db)
{
    /// <summary>
    /// The payer's negotiated plan; otherwise the default plan of the payer type (facility-specific first); otherwise the
    /// hospital's General default. Returns <c>null</c> only when no plan exists at all (catalogue base prices apply).
    /// </summary>
    public async Task<TariffPlan?> ResolvePlanAsync(PayerType payerType, Guid? payerId, Guid? facilityId, CancellationToken ct)
    {
        if (payerId is { } id)
        {
            var planId = await db.Set<Payer>().Where(p => p.Id == id).Select(p => p.TariffPlanId).FirstOrDefaultAsync(ct);
            if (await FindPlanAsync(planId, ct) is { IsActive: true } payerPlan)
            {
                return payerPlan;
            }
        }

        var type = ToTariffType(payerType);
        var candidates = await db.Set<TariffPlan>().AsNoTracking().Include(p => p.BedMultipliers)
            .Where(p => p.IsActive && p.IsDefault && (p.FacilityId == null || p.FacilityId == facilityId)
                        && (p.Type == type || p.Type == TariffType.General))
            .ToListAsync(ct);
        return candidates
            .OrderByDescending(p => p.Type == type)
            .ThenByDescending(p => p.FacilityId != null)
            .FirstOrDefault();
    }

    public async Task<TariffPlan?> FindPlanAsync(Guid? planId, CancellationToken ct) =>
        planId is { } id
            ? await db.Set<TariffPlan>().AsNoTracking().Include(p => p.BedMultipliers).FirstOrDefaultAsync(p => p.Id == id, ct)
            : null;

    public async Task<PriceQuote> QuoteAsync(
        ServiceItem item, TariffPlan? plan, DateOnly serviceDate, decimal bedMultiplier, bool isFreeFollowUp, decimal? unitPriceOverride, CancellationToken ct)
    {
        List<PlanPrice> prices = plan is null
            ? []
            : await db.Set<TariffPrice>().AsNoTracking()
                .Where(p => p.TariffPlanId == plan.Id && p.ServiceItemId == item.Id)
                .Select(p => new PlanPrice(p.Id, p.Price, p.EffectiveFrom, p.EffectiveTo))
                .ToListAsync(ct);

        return PriceResolver.Resolve(new PricingItem(item.Id, item.Category, item.BasePrice), prices, serviceDate, bedMultiplier, isFreeFollowUp, unitPriceOverride);
    }

    public Task<bool> HasPlanPriceAsync(Guid planId, Guid serviceItemId, DateOnly date, CancellationToken ct) =>
        db.Set<TariffPrice>().AnyAsync(
            p => p.TariffPlanId == planId && p.ServiceItemId == serviceItemId && p.EffectiveFrom <= date && (p.EffectiveTo == null || p.EffectiveTo >= date),
            ct);

    public static TariffType ToTariffType(PayerType payerType) => payerType switch
    {
        PayerType.Insurance => TariffType.Insurance,
        PayerType.Corporate => TariffType.Corporate,
        PayerType.GovernmentScheme => TariffType.GovernmentScheme,
        PayerType.Staff => TariffType.Staff,
        _ => TariffType.General,
    };
}
