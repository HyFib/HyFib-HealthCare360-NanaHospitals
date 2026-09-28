using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Cashless pre-authorisation workflow (INS-04): draft → submitted → query/answer → approved (or partially) / rejected,
/// enhancements during the stay and the final approval at discharge. The effective approved amount caps the payer share
/// the next time the final bill is prepared.
/// </summary>
public sealed class PreAuthService(HospitalDbContext db, INumberSeries numbers, IAuditLogger audit, ICurrentUser currentUser, TimeProvider time)
{
    public const string Series = "insurance.preauth";
    public const string Format = "{FAC}PA{YY}{SEQ:6}";

    public async Task<Result<Guid>> CreateAsync(SavePreAuthRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired;
        }

        var policy = await db.Set<PatientPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PolicyId, ct);
        if (policy is null || !policy.IsActive)
        {
            return Error.Field("policyId", "Choose an active policy of the patient.");
        }

        var links = await ValidateLinksAsync(policy.PatientId, request.AdmissionId, request.VisitId, ct);
        if (links.IsFailure)
        {
            return links.Error!;
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var preAuth = new PreAuthorization
        {
            FacilityId = facilityId,
            Number = await numbers.NextAsync(Series, facilityId, Format, NumberResetPolicy.Yearly, ct),
            PatientId = policy.PatientId,
            PolicyId = policy.Id,
            PayerId = policy.PayerId,
            TpaId = policy.TpaId,
            Status = PreAuthStatus.Draft,
        };
        Apply(preAuth, request);
        db.Add(preAuth);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return preAuth.Id;
    }

    /// <summary>Edits a draft, or adds documents/codes while answering a query.</summary>
    public async Task<Result> UpdateAsync(Guid id, SavePreAuthRequest request, CancellationToken ct)
    {
        var preAuth = await LoadAsync(id, ct);
        if (preAuth is null)
        {
            return Error.NotFound(nameof(PreAuthorization), id);
        }

        if (preAuth.Status is not (PreAuthStatus.Draft or PreAuthStatus.Query))
        {
            return Error.Rule("insurance.preauth_locked", "Only drafts, or requests with an open query, can be edited.");
        }

        if (request.PolicyId != preAuth.PolicyId)
        {
            return Error.Field("policyId", "Create a new pre-authorisation to use a different policy.");
        }

        var links = await ValidateLinksAsync(preAuth.PatientId, request.AdmissionId, request.VisitId, ct);
        if (links.IsFailure)
        {
            return links;
        }

        db.SetExpectedVersion(preAuth, request.Version ?? 0);
        Apply(preAuth, request);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result> SubmitAsync(Guid id, CancellationToken ct) => ChangeAsync(id, (p, now) => p.Submit(now), ct);

    public Task<Result> RaiseQueryAsync(Guid id, string text, CancellationToken ct) => ChangeAsync(id, (p, now) => p.RaiseQuery(text, now), ct);

    public Task<Result> AnswerQueryAsync(Guid id, string text, CancellationToken ct) => ChangeAsync(id, (p, now) => p.AnswerQuery(text, now), ct);

    public Task<Result> ApproveAsync(Guid id, ApprovePreAuthRequest request, CancellationToken ct) =>
        ChangeAsync(id, (p, now) => p.Approve(request.ApprovedAmount, request.Reference, now), ct, "insurance.preauth.approved");

    public Task<Result> RejectAsync(Guid id, string reason, CancellationToken ct) =>
        ChangeAsync(id, (p, now) => p.Reject(reason, now), ct, "insurance.preauth.rejected");

    public Task<Result> RequestEnhancementAsync(Guid id, RequestEnhancementRequest request, CancellationToken ct) =>
        ChangeAsync(id, (p, now) => p.RequestEnhancement(request.Amount, request.Reason, now), ct);

    public Task<Result> RespondToEnhancementAsync(Guid id, Guid enhancementId, RespondEnhancementRequest request, CancellationToken ct) =>
        ChangeAsync(id, (p, now) => p.RespondToEnhancement(enhancementId, request.Approved, request.ApprovedAmount, request.Reference, now), ct, "insurance.preauth.enhancement");

    public Task<Result> RecordFinalApprovalAsync(Guid id, FinalApprovalRequest request, CancellationToken ct) =>
        ChangeAsync(id, (p, now) => p.RecordFinalApproval(request.Amount, request.Reference, now), ct, "insurance.preauth.final_approval");

    private async Task<Result> ChangeAsync(Guid id, Func<PreAuthorization, DateTimeOffset, Result> change, CancellationToken ct, string? auditAction = null)
    {
        var preAuth = await LoadAsync(id, ct);
        if (preAuth is null)
        {
            return Error.NotFound(nameof(PreAuthorization), id);
        }

        var result = change(preAuth, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        if (auditAction is not null)
        {
            audit.Add(new AuditEntry(AuditCategories.Data, auditAction, $"{preAuth.Number}: {preAuth.Status}, effective approval ₹{Money.FormatInr(preAuth.EffectiveApprovedAmount)}")
            {
                PatientId = preAuth.PatientId,
                EntityType = nameof(PreAuthorization),
                EntityId = preAuth.Id.ToString(),
            });
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private Task<PreAuthorization?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Set<PreAuthorization>().Include(p => p.Enhancements).FirstOrDefaultAsync(p => p.Id == id, ct);

    private async Task<Result> ValidateLinksAsync(Guid patientId, Guid? admissionId, Guid? visitId, CancellationToken ct)
    {
        if (admissionId is { } admission
            && await db.Set<IpAccount>().Where(a => a.AdmissionId == admission).Select(a => (Guid?)a.PatientId).FirstOrDefaultAsync(ct) is { } admittedPatient
            && admittedPatient != patientId)
        {
            return Error.Field("admissionId", "The admission belongs to another patient.");
        }

        if (visitId is { } visit
            && await db.Set<Visit>().Where(v => v.Id == visit).Select(v => (Guid?)v.PatientId).FirstOrDefaultAsync(ct) is var visitPatient
            && visitPatient != patientId)
        {
            return Error.Field("visitId", visitPatient is null ? "The visit was not found." : "The visit belongs to another patient.");
        }

        return Result.Success();
    }

    private static void Apply(PreAuthorization preAuth, SavePreAuthRequest request)
    {
        preAuth.AdmissionId = request.AdmissionId;
        preAuth.VisitId = request.VisitId;
        preAuth.RequestedAmount = Money.Round(request.RequestedAmount);
        preAuth.DiagnosisCodes = Clean(request.DiagnosisCodes);
        preAuth.ProcedureCodes = Clean(request.ProcedureCodes);
        preAuth.ClinicalSummary = Clean(request.ClinicalSummary);
        preAuth.ExpectedStayDays = request.ExpectedStayDays;
        preAuth.DocumentFileIds = request.DocumentFileIds?.Distinct().ToList() ?? [];
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
