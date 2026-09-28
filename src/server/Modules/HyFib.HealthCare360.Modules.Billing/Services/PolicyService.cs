using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Patient policies and policy verification (INS-03). Policy numbers are encrypted with <see cref="IFieldProtector"/>,
/// searchable through a blind index and displayed masked; revealing one is an audited PHI access.
/// </summary>
public sealed class PolicyService(HospitalDbContext db, IFieldProtector protector, IAuditLogger audit, ICurrentUser currentUser, TimeProvider time)
{
    public async Task<Result<Guid>> CreateAsync(SavePolicyRequest request, CancellationToken ct)
    {
        var check = await ValidateReferencesAsync(request, ct);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var index = IndexFor(request.PolicyNumber!);
        if (await db.Set<PatientPolicy>().AnyAsync(p => p.PatientId == request.PatientId && p.PayerId == request.PayerId && p.PolicyNumberIndex == index && p.IsActive, ct))
        {
            return Error.Conflict("insurance.policy_exists", "This policy is already recorded for the patient.");
        }

        var policy = new PatientPolicy { PatientId = request.PatientId };
        SetPolicyNumber(policy, request.PolicyNumber!);
        Apply(policy, request);
        db.Add(policy);
        await db.SaveChangesAsync(ct);
        return policy.Id;
    }

    public async Task<Result> UpdateAsync(Guid id, SavePolicyRequest request, CancellationToken ct)
    {
        var policy = await db.Set<PatientPolicy>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return Error.NotFound(nameof(PatientPolicy), id);
        }

        if (policy.PatientId != request.PatientId)
        {
            return Error.Field("patientId", "A policy cannot be moved to another patient.");
        }

        var check = await ValidateReferencesAsync(request, ct);
        if (check.IsFailure)
        {
            return check;
        }

        db.SetExpectedVersion(policy, request.Version ?? 0);
        if (!string.IsNullOrWhiteSpace(request.PolicyNumber))
        {
            SetPolicyNumber(policy, request.PolicyNumber);
            policy.VerifiedAt = null;
            policy.VerifiedBy = null;
        }

        Apply(policy, request);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Records that the desk verified the policy with the insurer/TPA (eligibility, validity, balance sum insured).</summary>
    public async Task<Result> VerifyAsync(Guid id, string? notes, CancellationToken ct)
    {
        var policy = await db.Set<PatientPolicy>().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return Error.NotFound(nameof(PatientPolicy), id);
        }

        policy.MarkVerified(currentUser.UserId, notes, time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Decrypts the full policy number for filing on an insurer portal; the access is written to the audit trail.</summary>
    public async Task<Result<PolicyNumberDto>> RevealAsync(Guid id, CancellationToken ct)
    {
        var policy = await db.Set<PatientPolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (policy is null)
        {
            return Error.NotFound(nameof(PatientPolicy), id);
        }

        await audit.PatientRecordAccessedAsync(policy.PatientId, "Viewed insurance policy number", ct);
        return new PolicyNumberDto(policy.Id, protector.Unprotect(policy.PolicyNumberCipher) ?? string.Empty);
    }

    public string IndexFor(string policyNumber) => protector.BlindIndex(Masking.Normalize(policyNumber));

    private void SetPolicyNumber(PatientPolicy policy, string policyNumber)
    {
        var trimmed = policyNumber.Trim();
        policy.PolicyNumberCipher = protector.Protect(trimmed);
        policy.PolicyNumberIndex = IndexFor(trimmed);
        policy.PolicyNumberMasked = Masking.MaskIdentifier(trimmed);
    }

    private static void Apply(PatientPolicy policy, SavePolicyRequest request)
    {
        policy.PayerId = request.PayerId;
        policy.TpaId = request.TpaId;
        policy.MemberId = Clean(request.MemberId);
        policy.PolicyHolderName = Clean(request.PolicyHolderName);
        policy.RelationToHolder = Clean(request.RelationToHolder);
        policy.ValidFrom = request.ValidFrom;
        policy.ValidTo = request.ValidTo;
        policy.SumInsured = request.SumInsured;
        policy.CoPayPercent = request.CoPayPercent;
        policy.IsActive = request.IsActive;
        policy.CardFileId = request.CardFileId;
        policy.Notes = Clean(request.Notes);
    }

    private async Task<Result> ValidateReferencesAsync(SavePolicyRequest request, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().Where(p => p.Id == request.PatientId).Select(p => new { p.MergedIntoPatientId }).FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        if (patient.MergedIntoPatientId is not null)
        {
            return Error.Rule("patient.merged", "This record was merged into another UHID. Record the policy on the surviving record.");
        }

        var payer = await db.Set<Payer>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PayerId && p.IsActive, ct);
        if (payer is null || payer.Category == PayerCategory.Tpa)
        {
            return Error.Field("payerId", "Choose the insurer, corporate or scheme that covers the patient.");
        }

        if (request.TpaId is { } tpaId && !await db.Set<Payer>().AnyAsync(p => p.Id == tpaId && p.IsActive && p.Category == PayerCategory.Tpa, ct))
        {
            return Error.Field("tpaId", "Choose an active TPA.");
        }

        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
