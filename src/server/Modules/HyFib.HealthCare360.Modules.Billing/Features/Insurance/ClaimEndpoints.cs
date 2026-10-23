using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Insurance;

/// <summary>/api/v1/insurance/claims and /receivables — claims with coding, queries, settlements/deductions and ageing (INS-05..07, MB-03).</summary>
internal static class ClaimEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/insurance/claims").WithTags("Insurance - Claims");
        group.MapGet("/", List).RequirePermission(BillingPermissions.ClaimsManage);
        group.MapGet("/{id:guid}", Get).RequirePermission(BillingPermissions.ClaimsManage);
        group.MapPost("/", Create).RequirePermission(BillingPermissions.ClaimsManage).WithValidation<CreateClaimRequest>();
        group.MapPut("/{id:guid}/coding", UpdateCoding).RequirePermission(BillingPermissions.ClaimsManage).WithValidation<UpdateClaimCodingRequest>();
        group.MapPost("/{id:guid}/query", RaiseQuery).RequirePermission(BillingPermissions.ClaimsManage).WithValidation<ClaimTextRequest>();
        group.MapPost("/{id:guid}/answer", AnswerQuery).RequirePermission(BillingPermissions.ClaimsManage).WithValidation<ClaimTextRequest>();
        group.MapPost("/{id:guid}/approve", Approve).RequirePermission(BillingPermissions.ClaimsManage).WithValidation<ApproveClaimRequest>();
        group.MapPost("/{id:guid}/settle", Settle).RequirePermission(BillingPermissions.ClaimsManage).WithValidation<SettleClaimRequest>();
        group.MapPost("/{id:guid}/reject", Reject).RequirePermission(BillingPermissions.ClaimsManage).WithValidation<RejectClaimRequest>();

        app.MapGet("/api/v1/insurance/receivables/ageing", Ageing).WithTags("Insurance - Claims").RequirePermission(BillingPermissions.ClaimsManage);
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, ClaimStatus? status, Guid? payerId, Guid? patientId, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var claims = db.Set<InsuranceClaim>().AsNoTracking();
        if (status is not null)
        {
            claims = claims.Where(c => c.Status == status);
        }

        if (payerId is not null)
        {
            claims = claims.Where(c => c.PayerId == payerId);
        }

        if (patientId is not null)
        {
            claims = claims.Where(c => c.PatientId == patientId);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            claims = claims.Where(c => c.ClaimNumber == number || c.PayerClaimReference == term);
        }

        var result = await (
                from c in claims.OrderByDescending(c => c.SubmittedAt)
                join b in db.Set<Bill>() on c.BillId equals b.Id
                join p in db.Set<Payer>() on c.PayerId equals p.Id
                select new { Claim = c, b.BillNumber, PayerName = p.Name })
            .ToPagedResultAsync(page, ct);
        var patients = await PatientQueries.SummariesAsync(db, result.Items.Select(r => r.Claim.PatientId), BillingTime.Today(time), fullContacts: false, ct);
        return TypedResults.Ok(result.Map(r =>
        {
            var c = r.Claim;
            var patient = patients.GetValueOrDefault(c.PatientId);
            return new ClaimListItem(
                c.Id, c.ClaimNumber, c.BillId, r.BillNumber, c.PatientId, patient?.Uhid, patient?.FullName, c.PayerId, r.PayerName, c.ClaimedAmount,
                c.ApprovedAmount, c.SettledAmount, c.DeductionAmount, c.Status, c.SubmittedAt);
        }));
    }

    private static async Task<IResult> Get(
        Guid id, HospitalDbContext db, ClaimService claims, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var c = await db.Set<InsuranceClaim>().AsNoTracking().Include(x => x.Settlements).Include(x => x.Deductions).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return Error.NotFound(nameof(InsuranceClaim), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(c.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var patient = (await PatientQueries.SummariesAsync(db, [c.PatientId], BillingTime.Today(time), fullContacts: false, ct)).GetValueOrDefault(c.PatientId);
        var bill = await db.Set<Bill>().Where(b => b.Id == c.BillId).Select(b => new { b.BillNumber, b.AdmissionId, b.VisitId }).FirstOrDefaultAsync(ct);
        var payerName = await db.Set<Payer>().Where(p => p.Id == c.PayerId).Select(p => p.Name).FirstOrDefaultAsync(ct);
        var verified = await claims.VerifiedCodingAsync(c.PatientId, bill?.AdmissionId, bill?.VisitId, ct);
        await audit.PatientRecordAccessedAsync(c.PatientId, $"Viewed insurance claim {c.ClaimNumber}", ct);

        return TypedResults.Ok(new ClaimDetail(
            c.Id, c.ClaimNumber, c.BillId, bill?.BillNumber, c.PatientId, patient?.Uhid, patient?.FullName, c.PayerId, payerName, c.TpaId, c.PolicyId,
            c.PreAuthorizationId, c.ClaimedAmount, c.DiagnosisCodes, c.ProcedureCodes, c.DocumentFileIds, c.Status, c.SubmittedAt, c.PayerClaimReference,
            c.QueryText, c.QueryResponse, c.ApprovedAmount, c.SettledAmount, c.DeductionAmount, c.Outstanding, c.UtrNumber, c.RejectionReason,
            c.Settlements.OrderBy(s => s.RecordedAt).Select(s => new ClaimSettlementDto(s.Id, s.Amount, s.DeductionAmount, s.UtrNumber, s.Reference, s.SettledOn, s.ReceiptId)).ToList(),
            c.Deductions.Select(d => new ClaimDeductionDto(d.Id, d.SettlementId, d.Reason, d.Amount)).ToList(),
            verified,
            c.Version));
    }

    private static async Task<IResult> Create(CreateClaimRequest request, ClaimService claims, CancellationToken ct)
    {
        var created = await claims.CreateAsync(request, ct);
        return created.IsSuccess ? TypedResults.Created($"/api/v1/insurance/claims/{created.Value}", new { id = created.Value }) : created.Error!.ToProblem();
    }

    private static async Task<IResult> UpdateCoding(Guid id, UpdateClaimCodingRequest request, ClaimService claims, CancellationToken ct) =>
        (await claims.UpdateCodingAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> RaiseQuery(Guid id, ClaimTextRequest request, ClaimService claims, CancellationToken ct) =>
        (await claims.RaiseQueryAsync(id, request.Text, ct)).ToNoContent();

    private static async Task<IResult> AnswerQuery(Guid id, ClaimTextRequest request, ClaimService claims, CancellationToken ct) =>
        (await claims.AnswerQueryAsync(id, request.Text, ct)).ToNoContent();

    private static async Task<IResult> Approve(Guid id, ApproveClaimRequest request, ClaimService claims, CancellationToken ct) =>
        (await claims.ApproveAsync(id, request.ApprovedAmount, ct)).ToNoContent();

    private static async Task<IResult> Settle(Guid id, SettleClaimRequest request, ClaimService claims, CancellationToken ct) =>
        (await claims.SettleAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> Reject(Guid id, RejectClaimRequest request, ClaimService claims, CancellationToken ct) =>
        (await claims.RejectAsync(id, request.Reason, ct)).ToNoContent();

    private static async Task<IResult> Ageing(DateOnly? asOf, ClaimService claims, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(await claims.AgeingAsync(asOf ?? BillingTime.Today(time), ct));
}
