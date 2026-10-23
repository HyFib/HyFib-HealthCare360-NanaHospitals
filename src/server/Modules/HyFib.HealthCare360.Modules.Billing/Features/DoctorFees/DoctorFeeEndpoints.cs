using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.DoctorFees;

/// <summary>/api/v1/doctor-fees — sharing rules, accruals, payouts (TDS, deductions, approval) and statements (DFA-01..05).</summary>
internal static class DoctorFeeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var rules = app.MapGroup("/api/v1/doctor-fees/rules").WithTags("Doctor fees");
        rules.MapGet("/", ListRules).RequireAnyPermission(BillingPermissions.ShareRulesManage, BillingPermissions.PayoutsManage);
        rules.MapPost("/", CreateRule).RequirePermission(BillingPermissions.ShareRulesManage).WithValidation<SaveShareRuleRequest>();
        rules.MapPut("/{id:guid}", UpdateRule).RequirePermission(BillingPermissions.ShareRulesManage).WithValidation<SaveShareRuleRequest>().RequireVersion();
        rules.MapDelete("/{id:guid}", DeleteRule).RequirePermission(BillingPermissions.ShareRulesManage);

        var payouts = app.MapGroup("/api/v1/doctor-fees").WithTags("Doctor fees");
        payouts.MapGet("/accruals", ListAccruals).RequirePermission(BillingPermissions.PayoutsManage);
        payouts.MapGet("/payouts", ListPayouts).RequirePermission(BillingPermissions.PayoutsManage);
        payouts.MapGet("/payouts/{id:guid}", GetPayout).RequirePermission(BillingPermissions.PayoutsManage);
        payouts.MapPost("/payouts", CreatePayout).RequirePermission(BillingPermissions.PayoutsManage).WithValidation<CreatePayoutRequest>();
        payouts.MapPost("/payouts/{id:guid}/approve", ApprovePayout).RequirePermission(BillingPermissions.PayoutsManage).WithValidation<PayoutVersionRequest>();
        payouts.MapPost("/payouts/{id:guid}/pay", PayPayout).RequirePermission(BillingPermissions.PayoutsManage).WithValidation<PayPayoutRequest>();
        payouts.MapDelete("/payouts/{id:guid}", DiscardPayout).RequirePermission(BillingPermissions.PayoutsManage);
        payouts.MapGet("/statements", Statement).RequirePermission(BillingPermissions.PayoutsManage);
    }

    private static async Task<IResult> ListRules(Guid? practitionerId, bool? includeInactive, HospitalDbContext db, CancellationToken ct)
    {
        var rules = db.Set<DoctorShareRule>().AsNoTracking();
        if (includeInactive != true)
        {
            rules = rules.Where(r => r.IsActive);
        }

        if (practitionerId is not null)
        {
            rules = rules.Where(r => r.PractitionerId == null || r.PractitionerId == practitionerId);
        }

        var rows = await (
                from r in rules
                join p in db.Set<Practitioner>() on r.PractitionerId equals (Guid?)p.Id into doctors
                from p in doctors.DefaultIfEmpty()
                join s in db.Set<ServiceItem>() on r.ServiceItemId equals (Guid?)s.Id into items
                from s in items.DefaultIfEmpty()
                orderby r.PractitionerId, r.EffectiveFrom descending
                select new ShareRuleDto(
                    r.Id, r.PractitionerId, p == null ? null : p.DisplayName, r.ServiceItemId, s == null ? null : s.Code, r.Category, r.PayerType, r.Method,
                    r.Value, r.EffectiveFrom, r.EffectiveTo, r.IsActive, r.Notes, r.Version))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> CreateRule(SaveShareRuleRequest request, DoctorFeeService fees, CancellationToken ct)
    {
        var saved = await fees.SaveRuleAsync(null, request, ct);
        return saved.IsSuccess ? TypedResults.Created($"/api/v1/doctor-fees/rules/{saved.Value}", new { id = saved.Value }) : saved.Error!.ToProblem();
    }

    private static async Task<IResult> UpdateRule(Guid id, SaveShareRuleRequest request, DoctorFeeService fees, CancellationToken ct)
    {
        var saved = await fees.SaveRuleAsync(id, request, ct);
        return saved.IsSuccess ? TypedResults.NoContent() : saved.Error!.ToProblem();
    }

    private static async Task<IResult> DeleteRule(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var rule = await db.Set<DoctorShareRule>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return Error.NotFound(nameof(DoctorShareRule), id).ToProblem();
        }

        db.Remove(rule);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListAccruals(
        [AsParameters] PageQuery query, Guid? practitionerId, DateOnly? from, DateOnly? to, AccrualStatus? status, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var accruals = Filter(db.Set<DoctorFeeAccrual>().AsNoTracking(), practitionerId, from, to);
        if (status is not null)
        {
            accruals = accruals.Where(a => a.Status == status);
        }

        var result = await accruals.OrderByDescending(a => a.AccruedOn).ThenBy(a => a.BillNumber).ToPagedResultAsync(page, ct);
        var names = await DoctorNamesAsync(db, result.Items.Select(a => a.PractitionerId), ct);
        return TypedResults.Ok(result.Map(a => ToDto(a, names.GetValueOrDefault(a.PractitionerId))));
    }

    private static async Task<IResult> ListPayouts(
        [AsParameters] PageQuery query, Guid? practitionerId, PayoutStatus? status, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var payouts = db.Set<DoctorPayout>().AsNoTracking().Include(p => p.Deductions).AsQueryable();
        if (practitionerId is not null)
        {
            payouts = payouts.Where(p => p.PractitionerId == practitionerId);
        }

        if (status is not null)
        {
            payouts = payouts.Where(p => p.Status == status);
        }

        var result = await payouts.OrderByDescending(p => p.PeriodTo).ToPagedResultAsync(page, ct);
        var names = await DoctorNamesAsync(db, result.Items.Select(p => p.PractitionerId), ct);
        var ids = result.Items.Select(p => p.Id).ToList();
        var counts = await db.Set<DoctorFeeAccrual>().Where(a => a.PayoutId != null && ids.Contains(a.PayoutId.Value))
            .GroupBy(a => a.PayoutId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return TypedResults.Ok(result.Map(p => ToDto(p, names.GetValueOrDefault(p.PractitionerId), counts.GetValueOrDefault(p.Id))));
    }

    private static async Task<IResult> GetPayout(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var payout = await db.Set<DoctorPayout>().AsNoTracking().Include(p => p.Deductions).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payout is null)
        {
            return Error.NotFound(nameof(DoctorPayout), id).ToProblem();
        }

        var name = await db.Set<Practitioner>().Where(p => p.Id == payout.PractitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
        var count = await db.Set<DoctorFeeAccrual>().CountAsync(a => a.PayoutId == id, ct);
        return TypedResults.Ok(ToDto(payout, name, count));
    }

    private static async Task<IResult> CreatePayout(CreatePayoutRequest request, DoctorFeeService fees, CancellationToken ct)
    {
        var created = await fees.CreatePayoutAsync(request, ct);
        return created.IsSuccess ? TypedResults.Created($"/api/v1/doctor-fees/payouts/{created.Value}", new { id = created.Value }) : created.Error!.ToProblem();
    }

    private static async Task<IResult> ApprovePayout(Guid id, PayoutVersionRequest request, DoctorFeeService fees, CancellationToken ct) =>
        (await fees.ApprovePayoutAsync(id, request.Version, ct)).ToNoContent();

    private static async Task<IResult> PayPayout(Guid id, PayPayoutRequest request, DoctorFeeService fees, CancellationToken ct) =>
        (await fees.PayPayoutAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> DiscardPayout(Guid id, uint version, DoctorFeeService fees, CancellationToken ct) =>
        (await fees.DiscardPayoutAsync(id, version, ct)).ToNoContent();

    /// <summary>Doctor fee statement for a period: every accrual (with reversals) and the payouts made.</summary>
    private static async Task<IResult> Statement(Guid practitionerId, DateOnly from, DateOnly to, HospitalDbContext db, CancellationToken ct)
    {
        if (to < from)
        {
            return Error.Field("to", "The period must end on or after its start.").ToProblem();
        }

        var accruals = await Filter(db.Set<DoctorFeeAccrual>().AsNoTracking(), practitionerId, from, to).OrderBy(a => a.AccruedOn).ToListAsync(ct);
        var payouts = await db.Set<DoctorPayout>().AsNoTracking().Include(p => p.Deductions)
            .Where(p => p.PractitionerId == practitionerId && p.PeriodFrom <= to && p.PeriodTo >= from)
            .OrderBy(p => p.PeriodFrom)
            .ToListAsync(ct);
        var name = await db.Set<Practitioner>().Where(p => p.Id == practitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
        return TypedResults.Ok(new DoctorStatement(
            practitionerId,
            name,
            from,
            to,
            Accrued: accruals.Where(a => a.ReversalOfId is null).Sum(a => a.ShareAmount),
            Reversed: -accruals.Where(a => a.ReversalOfId is not null).Sum(a => a.ShareAmount),
            InPayout: accruals.Where(a => a.Status == AccrualStatus.InPayout).Sum(a => a.ShareAmount),
            PaidOut: accruals.Where(a => a.Status == AccrualStatus.Paid).Sum(a => a.ShareAmount),
            Unpaid: accruals.Where(a => a.Status == AccrualStatus.Accrued).Sum(a => a.ShareAmount),
            accruals.Select(a => ToDto(a, name)).ToList(),
            payouts.Select(p => ToDto(p, name, accruals.Count(a => a.PayoutId == p.Id))).ToList()));
    }

    private static IQueryable<DoctorFeeAccrual> Filter(IQueryable<DoctorFeeAccrual> accruals, Guid? practitionerId, DateOnly? from, DateOnly? to)
    {
        if (practitionerId is not null)
        {
            accruals = accruals.Where(a => a.PractitionerId == practitionerId);
        }

        if (from is not null)
        {
            accruals = accruals.Where(a => a.AccruedOn >= from);
        }

        if (to is not null)
        {
            accruals = accruals.Where(a => a.AccruedOn <= to);
        }

        return accruals;
    }

    private static async Task<Dictionary<Guid, string>> DoctorNamesAsync(HospitalDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Practitioner>().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.DisplayName, ct);
    }

    private static AccrualDto ToDto(DoctorFeeAccrual a, string? practitionerName) => new(
        a.Id, a.PractitionerId, practitionerName, a.BillId, a.BillNumber, a.ServiceCode, a.Description, a.Category, a.PayerType, a.ServiceDate, a.AccruedOn,
        a.BasisAmount, a.Method, a.Rate, a.ShareAmount, a.Status, a.PayoutId, a.ReversalOfId is not null);

    private static PayoutDto ToDto(DoctorPayout p, string? practitionerName, int accrualCount) => new(
        p.Id, p.PayoutNumber, p.PractitionerId, practitionerName, p.PeriodFrom, p.PeriodTo, p.GrossAmount, p.DeductionAmount, p.TdsPercent, p.TdsAmount,
        p.NetAmount, p.Status, p.ApprovedBy, p.ApprovedAt, p.PaidAt, p.PaidReference, p.PaymentMode, p.Notes, accrualCount,
        p.Deductions.Select(d => new PayoutDeductionDto(d.Id, d.Description, d.Amount)).ToList(), p.Version);
}
