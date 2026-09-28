using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Insurance;

/// <summary>
/// /api/v1/insurance — payers (insurers, TPAs, corporates, schemes; INS-01/02), IRDAI non-payable items and patient
/// policies with verification (INS-03). Policy numbers are always masked; revealing one is audited.
/// </summary>
internal static class PayerEndpoints
{
    private static readonly string[] PayerReaders =
    [
        BillingPermissions.PayersManage, BillingPermissions.PoliciesManage, BillingPermissions.PreAuthManage, BillingPermissions.ClaimsManage,
        BillingPermissions.OpCreate, BillingPermissions.IpManage, BillingPermissions.ChargesRead,
    ];

    private static readonly string[] PolicyReaders =
    [
        BillingPermissions.PoliciesManage, BillingPermissions.PreAuthManage, BillingPermissions.ClaimsManage, BillingPermissions.IpManage, BillingPermissions.OpCreate,
    ];

    public static void Map(IEndpointRouteBuilder app)
    {
        var payers = app.MapGroup("/api/v1/insurance/payers").WithTags("Insurance - Payers");
        payers.MapGet("/", ListPayers).RequireAnyPermission(PayerReaders);
        payers.MapGet("/{id:guid}", GetPayer).RequireAnyPermission(PayerReaders);
        payers.MapPost("/", CreatePayer).RequirePermission(BillingPermissions.PayersManage).WithValidation<SavePayerRequest>();
        payers.MapPut("/{id:guid}", UpdatePayer).RequirePermission(BillingPermissions.PayersManage).WithValidation<SavePayerRequest>();

        var nonPayables = app.MapGroup("/api/v1/insurance/non-payable-items").WithTags("Insurance - Payers");
        nonPayables.MapGet("/", ListNonPayables).RequireAnyPermission(BillingPermissions.PayersManage, BillingPermissions.ClaimsManage, BillingPermissions.IpManage);
        nonPayables.MapPost("/", AddNonPayable).RequirePermission(BillingPermissions.PayersManage).WithValidation<SaveNonPayableItemRequest>();
        nonPayables.MapDelete("/{id:guid}", DeleteNonPayable).RequirePermission(BillingPermissions.PayersManage);

        var policies = app.MapGroup("/api/v1/insurance/policies").WithTags("Insurance - Policies");
        policies.MapGet("/", ListPolicies).RequireAnyPermission(PolicyReaders);
        policies.MapGet("/{id:guid}", GetPolicy).RequireAnyPermission(PolicyReaders);
        policies.MapGet("/search", SearchPolicies).RequireAnyPermission(BillingPermissions.PoliciesManage, BillingPermissions.ClaimsManage);
        policies.MapGet("/{id:guid}/policy-number", RevealPolicyNumber).RequireAnyPermission(BillingPermissions.PoliciesManage, BillingPermissions.PreAuthManage, BillingPermissions.ClaimsManage);
        policies.MapPost("/", CreatePolicy).RequirePermission(BillingPermissions.PoliciesManage).WithValidation<SavePolicyRequest>();
        policies.MapPut("/{id:guid}", UpdatePolicy).RequirePermission(BillingPermissions.PoliciesManage).WithValidation<SavePolicyRequest>();
        policies.MapPost("/{id:guid}/verify", VerifyPolicy).RequirePermission(BillingPermissions.PoliciesManage).WithValidation<VerifyPolicyRequest>();
    }

    private static async Task<IResult> ListPayers(PayerCategory? category, bool? includeInactive, string? search, HospitalDbContext db, CancellationToken ct)
    {
        var payers = db.Set<Payer>().AsNoTracking();
        if (includeInactive != true)
        {
            payers = payers.Where(p => p.IsActive);
        }

        if (category is not null)
        {
            payers = payers.Where(p => p.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = search.Trim().ToLikePattern();
            payers = payers.Where(p => EF.Functions.ILike(p.Name, like) || EF.Functions.ILike(p.Code, like));
        }

        var rows = await payers.OrderBy(p => p.Name)
            .Select(p => new PayerListItem(
                p.Id, p.Code, p.Name, p.Category, p.ContactPerson, p.Phone, p.TariffPlanId,
                db.Set<TariffPlan>().Where(t => t.Id == p.TariffPlanId).Select(t => t.Name).FirstOrDefault(), p.CreditDays, p.IsActive))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> GetPayer(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var payer = await db.Set<Payer>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return payer is null ? Error.NotFound(nameof(Payer), id).ToProblem() : TypedResults.Ok(ToDetail(payer));
    }

    private static async Task<IResult> CreatePayer(SavePayerRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<Payer>().IgnoreQueryFilters().AnyAsync(p => p.Code == code, ct))
        {
            return Error.Conflict("insurance.payer_code_exists", $"A payer with code {code} already exists.").ToProblem();
        }

        if (request.TariffPlanId is { } planId && !await db.Set<TariffPlan>().AnyAsync(t => t.Id == planId, ct))
        {
            return Error.Field("tariffPlanId", "Choose a tariff plan.").ToProblem();
        }

        var payer = new Payer { Code = code };
        Apply(payer, request);
        db.Add(payer);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/insurance/payers/{payer.Id}", ToDetail(payer));
    }

    private static async Task<IResult> UpdatePayer(Guid id, SavePayerRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var payer = await db.Set<Payer>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (payer is null)
        {
            return Error.NotFound(nameof(Payer), id).ToProblem();
        }

        if (request.TariffPlanId is { } planId && !await db.Set<TariffPlan>().AnyAsync(t => t.Id == planId, ct))
        {
            return Error.Field("tariffPlanId", "Choose a tariff plan.").ToProblem();
        }

        db.SetExpectedVersion(payer, request.Version ?? 0);
        Apply(payer, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDetail(payer));
    }

    private static async Task<IResult> ListNonPayables(Guid? payerId, HospitalDbContext db, CancellationToken ct)
    {
        var rows = db.Set<NonPayableItem>().AsNoTracking();
        if (payerId is not null)
        {
            rows = rows.Where(n => n.PayerId == null || n.PayerId == payerId);
        }

        var items = await (
                from n in rows
                join s in db.Set<ServiceItem>() on n.ServiceItemId equals (Guid?)s.Id into services
                from s in services.DefaultIfEmpty()
                join p in db.Set<Payer>() on n.PayerId equals (Guid?)p.Id into payers
                from p in payers.DefaultIfEmpty()
                orderby n.CreatedAt
                select new NonPayableItemDto(
                    n.Id, n.PayerId, p == null ? null : p.Name, n.ServiceItemId, s == null ? null : s.Code, s == null ? null : s.Name, n.Category, n.Notes))
            .ToListAsync(ct);
        return TypedResults.Ok(items);
    }

    private static async Task<IResult> AddNonPayable(SaveNonPayableItemRequest request, HospitalDbContext db, CancellationToken ct)
    {
        if (request.ServiceItemId is { } serviceItemId && !await db.Set<ServiceItem>().AnyAsync(s => s.Id == serviceItemId, ct))
        {
            return Error.Field("serviceItemId", "Choose a service.").ToProblem();
        }

        var item = new NonPayableItem
        {
            PayerId = request.PayerId,
            ServiceItemId = request.ServiceItemId,
            Category = request.ServiceItemId is null ? request.Category : null,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };
        db.Add(item);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/insurance/non-payable-items/{item.Id}", new { id = item.Id });
    }

    private static async Task<IResult> DeleteNonPayable(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var item = await db.Set<NonPayableItem>().FirstOrDefaultAsync(n => n.Id == id, ct);
        if (item is null)
        {
            return Error.NotFound(nameof(NonPayableItem), id).ToProblem();
        }

        db.Remove(item);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListPolicies(Guid patientId, bool? includeInactive, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var policies = db.Set<PatientPolicy>().AsNoTracking().Where(p => p.PatientId == patientId);
        if (includeInactive != true)
        {
            policies = policies.Where(p => p.IsActive);
        }

        return TypedResults.Ok(await ToDtosAsync(db, await policies.OrderByDescending(p => p.ValidTo).ToListAsync(ct), time, ct));
    }

    private static async Task<IResult> GetPolicy(Guid id, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var policy = await db.Set<PatientPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return policy is null ? Error.NotFound(nameof(PatientPolicy), id).ToProblem() : TypedResults.Ok((await ToDtosAsync(db, [policy], time, ct))[0]);
    }

    /// <summary>Exact search by policy/card number through the blind index (the number itself is never queried in clear).</summary>
    private static async Task<IResult> SearchPolicies(string policyNumber, HospitalDbContext db, PolicyService policies, TimeProvider time, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(policyNumber) || policyNumber.Length > 64)
        {
            return Error.Field("policyNumber", "Enter the policy or card number.").ToProblem();
        }

        var index = policies.IndexFor(policyNumber);
        var found = await db.Set<PatientPolicy>().AsNoTracking().Where(p => p.PolicyNumberIndex == index).ToListAsync(ct);
        return TypedResults.Ok(await ToDtosAsync(db, found, time, ct));
    }

    private static async Task<IResult> RevealPolicyNumber(Guid id, PolicyService policies, CancellationToken ct) => (await policies.RevealAsync(id, ct)).ToOk();

    private static async Task<IResult> CreatePolicy(SavePolicyRequest request, PolicyService policies, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var created = await policies.CreateAsync(request, ct);
        if (created.IsFailure)
        {
            return created.Error!.ToProblem();
        }

        var policy = await db.Set<PatientPolicy>().AsNoTracking().FirstAsync(p => p.Id == created.Value, ct);
        return TypedResults.Created($"/api/v1/insurance/policies/{policy.Id}", (await ToDtosAsync(db, [policy], time, ct))[0]);
    }

    private static async Task<IResult> UpdatePolicy(Guid id, SavePolicyRequest request, PolicyService policies, CancellationToken ct) =>
        (await policies.UpdateAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> VerifyPolicy(Guid id, VerifyPolicyRequest request, PolicyService policies, CancellationToken ct) =>
        (await policies.VerifyAsync(id, request.Notes, ct)).ToNoContent();

    private static void Apply(Payer payer, SavePayerRequest request)
    {
        payer.Name = request.Name.Trim();
        payer.Category = request.Category;
        payer.ContactPerson = Clean(request.ContactPerson);
        payer.Phone = Phone.Normalize(request.Phone);
        payer.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
        payer.Address = Clean(request.Address);
        payer.Gstin = string.IsNullOrWhiteSpace(request.Gstin) ? null : request.Gstin.Trim().ToUpperInvariant();
        payer.TariffPlanId = request.TariffPlanId;
        payer.CreditDays = request.CreditDays;
        payer.DefaultCoPayPercent = request.DefaultCoPayPercent;
        payer.PortalUrl = Clean(request.PortalUrl);
        payer.IsActive = request.IsActive;
        payer.Notes = Clean(request.Notes);
    }

    private static PayerDetail ToDetail(Payer p) => new(
        p.Id, p.Code, p.Name, p.Category, p.ContactPerson, p.Phone, p.Email, p.Address, p.Gstin, p.TariffPlanId, p.CreditDays, p.DefaultCoPayPercent,
        p.PortalUrl, p.IsActive, p.Notes, p.Version);

    private static async Task<List<PolicyDto>> ToDtosAsync(HospitalDbContext db, IReadOnlyList<PatientPolicy> policies, TimeProvider time, CancellationToken ct)
    {
        var payerIds = policies.Select(p => p.PayerId).Concat(policies.Where(p => p.TpaId is not null).Select(p => p.TpaId!.Value)).Distinct().ToList();
        var names = await db.Set<Payer>().Where(p => payerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var today = BillingTime.Today(time);
        return policies.Select(p => new PolicyDto(
            p.Id, p.PatientId, p.PayerId, names.GetValueOrDefault(p.PayerId), p.TpaId, p.TpaId is { } tpa ? names.GetValueOrDefault(tpa) : null,
            p.PolicyNumberMasked, p.MemberId, p.PolicyHolderName, p.RelationToHolder, p.ValidFrom, p.ValidTo, p.SumInsured, p.CoPayPercent, p.IsActive,
            p.IsValidOn(today), p.VerifiedAt, p.VerificationNotes, p.CardFileId, p.Notes, p.Version)).ToList();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
