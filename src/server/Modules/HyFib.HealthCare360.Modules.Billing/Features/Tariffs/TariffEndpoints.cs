using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tariffs;

/// <summary>/api/v1/billing/tariffs — tariff plans, plan prices (with validity) and price quotes (MB-02).</summary>
internal static class TariffEndpoints
{
    /// <summary>Refused on save: a change to the plan's terms is waiting for approval (the approvals' own code).</summary>
    private const string WaitingCode = "approval.already_requested";

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
        group.MapPut("/{id:guid}", Update).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SaveTariffPlanRequest>().RequireVersion();
        group.MapGet("/{id:guid}/prices", Prices).RequireAnyPermission(Readers);
        group.MapPut("/{id:guid}/prices", SavePrice).RequirePermission(BillingPermissions.TariffsManage).WithValidation<SaveTariffPriceRequest>()
            .FirstSaveCreates();
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

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, TariffApprovals tariffs, CancellationToken ct)
    {
        var plan = await db.Set<TariffPlan>().AsNoTracking().Include(p => p.BedMultipliers).FirstOrDefaultAsync(p => p.Id == id, ct);
        return plan is null
            ? Error.NotFound(nameof(TariffPlan), id).ToProblem()
            : TypedResults.Ok(await ToDetailAsync(db, plan, ct) with
            {
                PendingTerms = await tariffs.PendingTermsAsync(id, ct),
                PendingPrices = await tariffs.PendingPricesAsync(id, ct),
            });
    }

    private static async Task<IResult> Create(SaveTariffPlanRequest request, HospitalDbContext db, TariffApprovals tariffs, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<TariffPlan>().IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct))
        {
            return Error.Conflict("billing.tariff_code_exists", $"A tariff plan with code {code} already exists.").ToProblem();
        }

        var plan = new TariffPlan { Code = code };
        var applied = await ApplyDetailsAsync(db, plan, request, ct);
        if (applied.IsFailure)
        {
            return applied.Error!.ToProblem();
        }

        // What a plan's patients pay changes only when two people agree (maker-checker): a new plan is made out of use, and
        // goes live with the terms a second person approves.
        var asked = Terms(request);
        var now = asked.OutOfUse();
        now.ApplyTo(plan);
        db.Add(plan);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        PendingTariffTerms? pending = null;
        if (asked != now)
        {
            var approval = await tariffs.AskTermsAsync(plan, now, asked, request.Reason, ct);
            if (approval.IsFailure)
            {
                return approval.Error!.ToProblem();
            }

            pending = new PendingTariffTerms(approval.Value.Id, asked, approval.Value.RequestedByName, approval.Value.RequestedAt);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/v1/billing/tariffs/{plan.Id}", await ToDetailAsync(db, plan, ct) with { PendingTerms = pending });
    }

    private static async Task<IResult> Update(Guid id, SaveTariffPlanRequest request, HospitalDbContext db, TariffApprovals tariffs, CancellationToken ct)
    {
        var plan = await db.Set<TariffPlan>().Include(p => p.BedMultipliers).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null)
        {
            return Error.NotFound(nameof(TariffPlan), id).ToProblem();
        }

        db.GuardVersion(plan, request.Version ?? 0);
        var applied = await ApplyDetailsAsync(db, plan, request, ct);
        if (applied.IsFailure)
        {
            return applied.Error!.ToProblem();
        }

        // Every change to the terms waits for a second person: for a plan nothing only narrows, as its patients move to
        // another plan's prices when it is taken out of use. While a change waits, a save must ask for the same terms.
        var before = TariffPlanTerms.Of(plan);
        var asked = Terms(request);
        var waiting = await tariffs.PendingTermsAsync(plan.Id, ct);
        if (waiting is not null && waiting.Asked != asked)
        {
            return Error.Conflict(WaitingCode,
                    $"A change to tariff plan {plan.Code}'s terms, asked for by {waiting.RequestedBy}, is waiting for approval. Withdraw it in Approvals first, or wait for its decision.")
                .ToProblem();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (waiting is null && asked != before)
        {
            var approval = await tariffs.AskTermsAsync(plan, before, asked, request.Reason, ct);
            if (approval.IsFailure)
            {
                return approval.Error!.ToProblem();
            }

            await transaction.CommitAsync(ct);
            return TypedResults.Accepted($"/api/v1/approvals/{approval.Value.Id}", approval.Value);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Ok(await ToDetailAsync(db, plan, ct) with { PendingTerms = waiting });
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
            var items = db.Set<ServiceItem>().Where(s => EF.Functions.ILike(s.Name, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(s.Code, like, QueryExtensions.LikeEscape)).Select(s => s.Id);
            prices = prices.Where(p => items.Contains(p.ServiceItemId));
        }

        var result = await (
                from p in prices.OrderByKey(page, PriceSort, p => p.EffectiveFrom, defaultDescending: true)
                join s in db.Set<ServiceItem>() on p.ServiceItemId equals s.Id
                select new TariffPriceDto(p.Id, p.ServiceItemId, s.Code, s.Name, s.Category, s.BasePrice, p.Price, p.EffectiveFrom, p.EffectiveTo, p.Version))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static Task<IResult> SavePrice(Guid id, SaveTariffPriceRequest request, HospitalDbContext db, TariffApprovals tariffs, CancellationToken ct) =>
        AskPricesAsync(db, tariffs, id, [request], [], request.Reason, checkVersions: true, ct);

    private static Task<IResult> SavePrices(Guid id, BulkTariffPricesRequest request, HospitalDbContext db, TariffApprovals tariffs, CancellationToken ct) =>
        AskPricesAsync(db, tariffs, id, request.Prices, [], request.Reason, checkVersions: false, ct);

    private static async Task<IResult> DeletePrice(Guid id, Guid priceId, HospitalDbContext db, TariffApprovals tariffs, CancellationToken ct)
    {
        if (!await db.Set<TariffPrice>().AnyAsync(p => p.Id == priceId && p.TariffPlanId == id, ct))
        {
            return Error.NotFound(nameof(TariffPrice), priceId).ToProblem();
        }

        return await AskPricesAsync(db, tariffs, id, [], [priceId], null, checkVersions: false, ct);
    }

    /// <summary>
    /// Asks a second person to set and remove a plan's prices (maker-checker): a single price, a bulk revision as one
    /// request, or a price removed. Nothing changes until it is approved; while one waits, the plan's prices are not asked
    /// for again (<c>approval.already_requested</c>).
    /// </summary>
    private static async Task<IResult> AskPricesAsync(
        HospitalDbContext db, TariffApprovals tariffs, Guid planId, IReadOnlyList<SaveTariffPriceRequest> set, IReadOnlyList<Guid> remove, string? reason,
        bool checkVersions, CancellationToken ct)
    {
        var plan = await db.Set<TariffPlan>().FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null)
        {
            return Error.NotFound(nameof(TariffPlan), planId).ToProblem();
        }

        var itemIds = set.Select(r => r.ServiceItemId).Distinct().ToList();
        if (await db.Set<ServiceItem>().CountAsync(s => itemIds.Contains(s.Id), ct) != itemIds.Count)
        {
            return Error.Field("serviceItemId", "One or more services were not found.").ToProblem();
        }

        if (checkVersions)
        {
            // The form's version of the row it changes (REL-04): a price changed meanwhile, or one added for that date since
            // the form was loaded (sent without a version), is reported, not overwritten.
            foreach (var row in set)
            {
                var current = await db.Set<TariffPrice>().AsNoTracking()
                    .Where(p => p.TariffPlanId == planId && p.ServiceItemId == row.ServiceItemId && p.EffectiveFrom == row.EffectiveFrom)
                    .Select(p => (uint?)p.Version).FirstOrDefaultAsync(ct);
                if (current is { } version && row.Version != version)
                {
                    return Error.ConcurrencyConflict.ToProblem();
                }
            }
        }

        var approval = await tariffs.AskPricesAsync(plan, set, remove, reason, ct);
        return approval.IsSuccess
            ? TypedResults.Accepted($"/api/v1/approvals/{approval.Value.Id}", approval.Value)
            : approval.Error!.ToProblem();
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

    /// <summary>The terms of the plan a request asks for.</summary>
    private static TariffPlanTerms Terms(SaveTariffPlanRequest request) =>
        TariffPlanTerms.Create(request.Type, request.FacilityId, request.DefaultDiscountPercent, request.IsDefault, request.IsActive, request.BedMultipliers);

    /// <summary>Checks a plan's references and sets what does not change what its patients pay: name, payer and description.</summary>
    private static async Task<Result> ApplyDetailsAsync(HospitalDbContext db, TariffPlan plan, SaveTariffPlanRequest request, CancellationToken ct)
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
        plan.PayerId = request.PayerId;
        plan.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        return Result.Success();
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
