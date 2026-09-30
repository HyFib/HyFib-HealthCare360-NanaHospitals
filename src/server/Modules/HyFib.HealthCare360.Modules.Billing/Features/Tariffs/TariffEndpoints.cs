using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tariffs;

/// <summary>/api/v1/billing/tariffs — tariff plans, plan prices (with validity) and price quotes (MB-02).</summary>
internal static class TariffEndpoints
{
    private static readonly string[] Readers =
        [BillingPermissions.TariffsManage, BillingPermissions.ChargesRead, BillingPermissions.OpCreate, BillingPermissions.IpManage];

    private static readonly Dictionary<string, Expression<Func<TariffPrice, object?>>> PriceSort = new(StringComparer.OrdinalIgnoreCase)
    {
        ["effectiveFrom"] = p => p.EffectiveFrom,
        ["price"] = p => p.Price,
    };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/tariffs").WithTags("Billing - Tariffs");

        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapPost("/", Create).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SaveTariffPlanRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SaveTariffPlanRequest>();
        group.MapGet("/{id:guid}/prices", Prices).RequireAnyPermission(Readers);
        group.MapPut("/{id:guid}/prices", SavePrice).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SaveTariffPriceRequest>();
        group.MapPost("/{id:guid}/prices/bulk", SavePrices).RequirePermission(BillingPermissions.TariffsManage).WithValidation<BulkTariffPricesRequest>();
        group.MapDelete("/{id:guid}/prices/{priceId:guid}", DeletePrice).RequirePermission(BillingPermissions.TariffsManage);
        group.MapGet("/quote", Quote).RequireAnyPermission(Readers);
    }

    private static async Task<IResult> List(bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var plans = db.Set<TariffPlan>().AsNoTracking();
        if (includeInactive != true)
        {
            plans = plans.Where(p => p.IsActive);
        }

        var rows = await plans
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Code,
                p.Name,
                p.Type,
                p.PayerId,
                p.FacilityId,
                p.DefaultDiscountPercent,
                p.IsDefault,
                p.IsActive,
                PayerName = db.Set<Payer>().Where(x => x.Id == p.PayerId).Select(x => x.Name).FirstOrDefault(),
                PriceCount = db.Set<TariffPrice>().Count(x => x.TariffPlanId == p.Id),
            })
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(p => new TariffPlanListItem(
            p.Id, p.Code, p.Name, p.Type, p.PayerId, p.PayerName, p.FacilityId, p.DefaultDiscountPercent, p.IsDefault, p.IsActive, p.PriceCount)).ToList());
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var plan = await db.Set<TariffPlan>().AsNoTracking().Include(p => p.BedMultipliers).FirstOrDefaultAsync(p => p.Id == id, ct);
        return plan is null ? Error.NotFound(nameof(TariffPlan), id).ToProblem() : TypedResults.Ok(await ToDetailAsync(db, plan, ct));
    }

    private static async Task<IResult> Create(SaveTariffPlanRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<TariffPlan>().IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct))
        {
            return Error.Conflict("billing.tariff_code_exists", $"A tariff plan with code {code} already exists.").ToProblem();
        }

        var plan = new TariffPlan { Code = code };
        var applied = await ApplyAsync(db, plan, request, ct);
        if (applied.IsFailure)
        {
            return applied.Error!.ToProblem();
        }

        db.Add(plan);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/billing/tariffs/{plan.Id}", await ToDetailAsync(db, plan, ct));
    }

    private static async Task<IResult> Update(Guid id, SaveTariffPlanRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var plan = await db.Set<TariffPlan>().Include(p => p.BedMultipliers).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null)
        {
            return Error.NotFound(nameof(TariffPlan), id).ToProblem();
        }

        db.GuardVersion(plan, request.Version ?? 0);
        var applied = await ApplyAsync(db, plan, request, ct);
        if (applied.IsFailure)
        {
            return applied.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ToDetailAsync(db, plan, ct));
    }

    private static async Task<IResult> Prices(Guid id, [AsParameters] PageQuery query, DateOnly? onDate, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var prices = db.Set<TariffPrice>().AsNoTracking().Where(p => p.TariffPlanId == id);
        if (onDate is { } date)
        {
            prices = prices.Where(p => p.EffectiveFrom <= date && (p.EffectiveTo == null || p.EffectiveTo >= date));
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            var items = db.Set<ServiceItem>().Where(s => EF.Functions.ILike(s.Name, like) || EF.Functions.ILike(s.Code, like)).Select(s => s.Id);
            prices = prices.Where(p => items.Contains(p.ServiceItemId));
        }

        var result = await (
                from p in prices.OrderByKey(page, PriceSort, p => p.EffectiveFrom, defaultDescending: true)
                join s in db.Set<ServiceItem>() on p.ServiceItemId equals s.Id
                select new TariffPriceDto(p.Id, p.ServiceItemId, s.Code, s.Name, s.Category, s.BasePrice, p.Price, p.EffectiveFrom, p.EffectiveTo, p.Version))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> SavePrice(Guid id, SaveTariffPriceRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var outcome = await UpsertPricesAsync(db, id, [request], ct);
        return outcome.IsSuccess ? TypedResults.Ok(outcome.Value) : outcome.Error!.ToProblem();
    }

    private static async Task<IResult> SavePrices(Guid id, BulkTariffPricesRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var outcome = await UpsertPricesAsync(db, id, request.Prices, ct);
        return outcome.IsSuccess ? TypedResults.Ok(outcome.Value) : outcome.Error!.ToProblem();
    }

    private static async Task<IResult> DeletePrice(Guid id, Guid priceId, HospitalDbContext db, CancellationToken ct)
    {
        var price = await db.Set<TariffPrice>().FirstOrDefaultAsync(p => p.Id == priceId && p.TariffPlanId == id, ct);
        if (price is null)
        {
            return Error.NotFound(nameof(TariffPrice), priceId).ToProblem();
        }

        db.Remove(price);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    /// <summary>Price a service would be charged at for a visit, an admission, a plan, or the hospital default.</summary>
    private static async Task<IResult> Quote(
        Guid serviceItemId, Guid? visitId, Guid? admissionId, Guid? tariffPlanId, DateOnly? date,
        HospitalDbContext db, TariffService tariffs, ICurrentUser currentUser, TimeProvider time, CancellationToken ct)
    {
        var item = await db.Set<ServiceItem>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == serviceItemId, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(ServiceItem), serviceItemId).ToProblem();
        }

        TariffPlan? plan;
        Guid? bedServiceItemId = null;
        if (tariffPlanId is not null)
        {
            plan = await tariffs.FindPlanAsync(tariffPlanId, ct);
        }
        else if (admissionId is { } admission && await db.Set<IpAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.AdmissionId == admission, ct) is { } account)
        {
            plan = await tariffs.FindPlanAsync(account.TariffPlanId, ct);
            bedServiceItemId = account.BedServiceItemId;
        }
        else if (visitId is { } visitKey && await db.Set<Visit>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == visitKey, ct) is { } visit)
        {
            plan = await tariffs.ResolvePlanAsync(visit.PayerType, visit.PayerId, visit.FacilityId, ct);
        }
        else
        {
            plan = await tariffs.ResolvePlanAsync(PayerType.Self, null, currentUser.FacilityId, ct);
        }

        var quote = await tariffs.QuoteAsync(item, plan, date ?? BillingTime.Today(time), plan?.MultiplierFor(bedServiceItemId) ?? 1m, false, null, ct);
        return TypedResults.Ok(new PriceQuoteDto(
            item.Id, item.Code, item.Name, item.Category, quote.UnitPrice, quote.Source, plan?.Id, plan?.Name, quote.Multiplier, item.TaxRate,
            item.IsPriceEditable, item.IsDiscountAllowed));
    }

    private static async Task<Result> ApplyAsync(HospitalDbContext db, TariffPlan plan, SaveTariffPlanRequest request, CancellationToken ct)
    {
        if (request.PayerId is { } payerId && !await db.Set<Payer>().AnyAsync(p => p.Id == payerId, ct))
        {
            return Error.Field("payerId", "Choose a payer.");
        }

        var bedIds = request.BedMultipliers?.Select(m => m.BedServiceItemId).ToList() ?? [];
        if (bedIds.Count > 0 && await db.Set<ServiceItem>().CountAsync(s => bedIds.Contains(s.Id) && s.Category == ServiceCategory.BedCharge, ct) != bedIds.Count)
        {
            return Error.Field("bedMultipliers", "Bed-class multipliers must refer to bed-charge services.");
        }

        plan.Name = request.Name.Trim();
        plan.Type = request.Type;
        plan.PayerId = request.PayerId;
        plan.FacilityId = request.FacilityId;
        plan.DefaultDiscountPercent = request.DefaultDiscountPercent;
        plan.IsActive = request.IsActive;
        plan.IsDefault = request.IsDefault && request.IsActive;
        plan.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        plan.BedMultipliers.Clear();
        foreach (var multiplier in request.BedMultipliers ?? [])
        {
            plan.BedMultipliers.Add(new TariffBedMultiplier { TariffPlanId = plan.Id, BedServiceItemId = multiplier.BedServiceItemId, Multiplier = multiplier.Multiplier });
        }

        if (plan.IsDefault)
        {
            // One default per plan type and facility scope: the new default replaces the previous one.
            foreach (var other in await db.Set<TariffPlan>()
                         .Where(p => p.Id != plan.Id && p.IsDefault && p.Type == plan.Type && p.FacilityId == plan.FacilityId)
                         .ToListAsync(ct))
            {
                other.IsDefault = false;
            }
        }

        return Result.Success();
    }

    /// <summary>
    /// Adds or updates plan prices. A new price closes the previous open-ended price of the same service the day before
    /// it starts, so revisions never overlap and history is kept.
    /// </summary>
    private static async Task<Result<BulkTariffPricesResult>> UpsertPricesAsync(
        HospitalDbContext db, Guid planId, IReadOnlyList<SaveTariffPriceRequest> requests, CancellationToken ct)
    {
        if (!await db.Set<TariffPlan>().AnyAsync(p => p.Id == planId, ct))
        {
            return Error.NotFound(nameof(TariffPlan), planId);
        }

        var itemIds = requests.Select(r => r.ServiceItemId).Distinct().ToList();
        if (await db.Set<ServiceItem>().CountAsync(s => itemIds.Contains(s.Id), ct) != itemIds.Count)
        {
            return Error.Field("serviceItemId", "One or more services were not found.");
        }

        var existing = await db.Set<TariffPrice>().Where(p => p.TariffPlanId == planId && itemIds.Contains(p.ServiceItemId)).ToListAsync(ct);
        int created = 0, updated = 0;
        foreach (var request in requests)
        {
            var same = existing.FirstOrDefault(p => p.ServiceItemId == request.ServiceItemId && p.EffectiveFrom == request.EffectiveFrom);
            if (same is not null)
            {
                same.Price = request.Price;
                same.EffectiveTo = request.EffectiveTo;
                updated++;
                continue;
            }

            foreach (var previous in existing.Where(p => p.ServiceItemId == request.ServiceItemId && p.EffectiveFrom < request.EffectiveFrom
                                                         && (p.EffectiveTo is null || p.EffectiveTo >= request.EffectiveFrom)))
            {
                previous.EffectiveTo = request.EffectiveFrom.AddDays(-1);
            }

            var price = new TariffPrice
            {
                TariffPlanId = planId,
                ServiceItemId = request.ServiceItemId,
                Price = request.Price,
                EffectiveFrom = request.EffectiveFrom,
                EffectiveTo = request.EffectiveTo,
            };
            db.Add(price);
            existing.Add(price);
            created++;
        }

        await db.SaveChangesAsync(ct);
        return new BulkTariffPricesResult(created, updated);
    }

    private static async Task<TariffPlanDetail> ToDetailAsync(HospitalDbContext db, TariffPlan plan, CancellationToken ct)
    {
        var bedIds = plan.BedMultipliers.Select(m => m.BedServiceItemId).ToList();
        var beds = await db.Set<ServiceItem>().Where(s => bedIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => new { s.Code, s.Name }, ct);
        return new TariffPlanDetail(
            plan.Id, plan.Code, plan.Name, plan.Type, plan.PayerId, plan.FacilityId, plan.DefaultDiscountPercent, plan.IsDefault, plan.IsActive, plan.Description,
            plan.BedMultipliers.Select(m => new BedMultiplierDto(m.BedServiceItemId, beds.GetValueOrDefault(m.BedServiceItemId)?.Code, beds.GetValueOrDefault(m.BedServiceItemId)?.Name, m.Multiplier)).ToList(),
            plan.Version);
    }
}
