using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Insurance;

/// <summary>/api/v1/insurance/preauths — cashless pre-authorisation: initial, queries, approval, enhancements, final (INS-04).</summary>
internal static class PreAuthEndpoints
{
    private static readonly string[] Readers = [BillingPermissions.PreAuthManage, BillingPermissions.ClaimsManage, BillingPermissions.IpManage];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/insurance/preauths").WithTags("Insurance - Pre-authorisation");
        group.MapGet("/", List).RequireAnyPermission(Readers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(Readers);
        group.MapPost("/", Create).RequirePermission(BillingPermissions.PreAuthManage).WithValidation<SavePreAuthRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(BillingPermissions.PreAuthManage).WithValidation<SavePreAuthRequest>();
        group.MapPost("/{id:guid}/submit", Submit).RequirePermission(BillingPermissions.PreAuthManage);
        group.MapPost("/{id:guid}/query", RaiseQuery).RequirePermission(BillingPermissions.PreAuthManage).WithValidation<PreAuthTextRequest>();
        group.MapPost("/{id:guid}/answer", AnswerQuery).RequirePermission(BillingPermissions.PreAuthManage).WithValidation<PreAuthTextRequest>();
        group.MapPost("/{id:guid}/approve", Approve).RequirePermission(BillingPermissions.PreAuthManage).WithValidation<ApprovePreAuthRequest>();
        group.MapPost("/{id:guid}/reject", Reject).RequirePermission(BillingPermissions.PreAuthManage).WithValidation<RejectPreAuthRequest>();
        group.MapPost("/{id:guid}/enhancements", RequestEnhancement).RequirePermission(BillingPermissions.PreAuthManage).WithValidation<RequestEnhancementRequest>();
        group.MapPost("/{id:guid}/enhancements/{enhancementId:guid}/respond", RespondToEnhancement)
            .RequirePermission(BillingPermissions.PreAuthManage)
            .WithValidation<RespondEnhancementRequest>();
        group.MapPost("/{id:guid}/final-approval", FinalApproval).RequirePermission(BillingPermissions.PreAuthManage).WithValidation<FinalApprovalRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, PreAuthStatus? status, Guid? patientId, Guid? admissionId, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var rows = db.Set<PreAuthorization>().AsNoTracking().Include(p => p.Enhancements).AsQueryable();
        if (status is not null)
        {
            rows = rows.Where(p => p.Status == status);
        }

        if (patientId is not null)
        {
            rows = rows.Where(p => p.PatientId == patientId);
        }

        if (admissionId is not null)
        {
            rows = rows.Where(p => p.AdmissionId == admissionId);
        }

        if (page.SearchTerm is { } term)
        {
            var number = term.ToUpperInvariant();
            rows = rows.Where(p => p.Number == number);
        }

        var result = await rows.OrderByDescending(p => p.CreatedAt).ToPagedResultAsync(page, ct);
        var patients = await PatientQueries.SummariesAsync(db, result.Items.Select(p => p.PatientId), BillingTime.Today(time), ct);
        var payerIds = result.Items.Select(p => p.PayerId).Distinct().ToList();
        var payers = await db.Set<Payer>().Where(p => payerIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return TypedResults.Ok(result.Map(p =>
        {
            var patient = patients.GetValueOrDefault(p.PatientId);
            return new PreAuthListItem(
                p.Id, p.Number, p.PatientId, patient?.Uhid, patient?.FullName, p.AdmissionId, p.PayerId, payers.GetValueOrDefault(p.PayerId), p.RequestedAmount,
                p.EffectiveApprovedAmount, p.Status, p.CreatedAt, p.SubmittedAt);
        }));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PatientAccessGuard guard, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var p = await db.Set<PreAuthorization>().AsNoTracking().Include(x => x.Enhancements).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return Error.NotFound(nameof(PreAuthorization), id).ToProblem();
        }

        var access = await guard.EnsureCanOpenAsync(p.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var patient = (await PatientQueries.SummariesAsync(db, [p.PatientId], BillingTime.Today(time), ct)).GetValueOrDefault(p.PatientId);
        var payerIds = new List<Guid> { p.PayerId };
        if (p.TpaId is { } tpaId)
        {
            payerIds.Add(tpaId);
        }

        var names = await db.Set<Payer>().Where(x => payerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var masked = await db.Set<PatientPolicy>().Where(x => x.Id == p.PolicyId).Select(x => x.PolicyNumberMasked).FirstOrDefaultAsync(ct);
        await audit.PatientRecordAccessedAsync(p.PatientId, $"Viewed pre-authorisation {p.Number}", ct);

        return TypedResults.Ok(new PreAuthDetail(
            p.Id, p.Number, p.PatientId, patient?.Uhid, patient?.FullName, p.AdmissionId, p.VisitId, p.PolicyId, masked, p.PayerId, names.GetValueOrDefault(p.PayerId),
            p.TpaId, p.TpaId is { } tpa ? names.GetValueOrDefault(tpa) : null, p.RequestedAmount, p.DiagnosisCodes, p.ProcedureCodes, p.ClinicalSummary,
            p.ExpectedStayDays, p.DocumentFileIds, p.Status, p.ApprovedAmount, p.ApprovalReference, p.FinalApprovedAmount, p.EffectiveApprovedAmount,
            p.QueryText, p.QueryResponse, p.RejectionReason, p.SubmittedAt, p.ApprovedAt, p.RejectedAt,
            p.Enhancements.OrderBy(e => e.RequestedAt)
                .Select(e => new EnhancementDto(e.Id, e.RequestedAmount, e.Reason, e.Status, e.ApprovedAmount, e.Reference, e.RequestedAt, e.RespondedAt))
                .ToList(),
            p.Version));
    }

    private static async Task<IResult> Create(SavePreAuthRequest request, PreAuthService preAuths, CancellationToken ct)
    {
        var created = await preAuths.CreateAsync(request, ct);
        return created.IsSuccess ? TypedResults.Created($"/api/v1/insurance/preauths/{created.Value}", new { id = created.Value }) : created.Error!.ToProblem();
    }

    private static async Task<IResult> Update(Guid id, SavePreAuthRequest request, PreAuthService preAuths, CancellationToken ct) =>
        (await preAuths.UpdateAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> Submit(Guid id, PreAuthService preAuths, CancellationToken ct) => (await preAuths.SubmitAsync(id, ct)).ToNoContent();

    private static async Task<IResult> RaiseQuery(Guid id, PreAuthTextRequest request, PreAuthService preAuths, CancellationToken ct) =>
        (await preAuths.RaiseQueryAsync(id, request.Text, ct)).ToNoContent();

    private static async Task<IResult> AnswerQuery(Guid id, PreAuthTextRequest request, PreAuthService preAuths, CancellationToken ct) =>
        (await preAuths.AnswerQueryAsync(id, request.Text, ct)).ToNoContent();

    private static async Task<IResult> Approve(Guid id, ApprovePreAuthRequest request, PreAuthService preAuths, CancellationToken ct) =>
        (await preAuths.ApproveAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> Reject(Guid id, RejectPreAuthRequest request, PreAuthService preAuths, CancellationToken ct) =>
        (await preAuths.RejectAsync(id, request.Reason, ct)).ToNoContent();

    private static async Task<IResult> RequestEnhancement(Guid id, RequestEnhancementRequest request, PreAuthService preAuths, CancellationToken ct) =>
        (await preAuths.RequestEnhancementAsync(id, request, ct)).ToNoContent();

    private static async Task<IResult> RespondToEnhancement(Guid id, Guid enhancementId, RespondEnhancementRequest request, PreAuthService preAuths, CancellationToken ct) =>
        (await preAuths.RespondToEnhancementAsync(id, enhancementId, request, ct)).ToNoContent();

    private static async Task<IResult> FinalApproval(Guid id, FinalApprovalRequest request, PreAuthService preAuths, CancellationToken ct) =>
        (await preAuths.RecordFinalApprovalAsync(id, request, ct)).ToNoContent();
}
