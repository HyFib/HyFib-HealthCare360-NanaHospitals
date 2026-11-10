using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// Cashless pre-authorisation workflow (INS-04): draft → submitted → query/answer → approved (or partially) / rejected,
/// enhancements during the stay and the final approval at discharge. The effective approved amount caps the payer share
/// the next time the final bill is prepared. An amount the insurer approved (the approval, an enhancement, the final
/// approval) moves part of the bill from the patient to the insurer, so it is recorded only once a second insurance desk
/// user has checked it against the insurer's letter (MAKER-CHECKER.md, slice 12); until then that part stays the patient's.
/// A rejection, which keeps the bill with the patient, is recorded at once.
/// </summary>
public sealed class PreAuthService(
    HospitalDbContext db,
    INumberSeries numbers,
    IApprovals approvals,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IProcedureCatalogue procedureCatalogue,
    TimeProvider time)
{
    /// <summary>A pre-authorised amount recorded by one insurance desk user, waiting for another to check it.</summary>
    public const string AmountKind = "insurance.preauth_amount";

    /// <summary>When an amount on each of these pre-authorisations was asked for and still waits.</summary>
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> AmountsAskedAsync(IReadOnlyCollection<Guid> preAuthIds, CancellationToken ct) =>
        approvals.WaitingSinceAsync(AmountKind, nameof(PreAuthorization), preAuthIds, ct);

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

        var procedures = await CodedProceduresAsync(request.Procedures, ct);
        if (procedures.IsFailure)
        {
            return procedures.Error!;
        }

        await using var transaction = await db.BeginOwnedTransactionAsync(ct);
        var preAuth = new PreAuthorization
        {
            FacilityId = facilityId,
            Number = await numbers.NextAsync(BillingNumberSeries.PreAuthorisation, facilityId, ct),
            PatientId = policy.PatientId,
            PolicyId = policy.Id,
            PayerId = policy.PayerId,
            TpaId = policy.TpaId,
            Status = PreAuthStatus.Draft,
        };
        Apply(preAuth, request, procedures.Value);
        db.Add(preAuth);
        if (procedures.Value.Count > 0)
        {
            AuditProcedures(preAuth);
        }

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

        var procedures = await CodedProceduresAsync(request.Procedures, ct);
        if (procedures.IsFailure)
        {
            return procedures;
        }

        db.SetExpectedVersion(preAuth, request.Version ?? 0);
        var before = Signature(preAuth.Procedures);
        db.RemoveRange(preAuth.Procedures);
        preAuth.Procedures.Clear();
        Apply(preAuth, request, procedures.Value);
        if (Signature(preAuth.Procedures) != before)
        {
            AuditProcedures(preAuth);
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public Task<Result> SubmitAsync(Guid id, CancellationToken ct) => ChangeAsync(id, (p, now) => p.Submit(now), ct);

    public Task<Result> RaiseQueryAsync(Guid id, string text, CancellationToken ct) => ChangeAsync(id, (p, now) => p.RaiseQuery(text, now), ct);

    public Task<Result> AnswerQueryAsync(Guid id, string text, CancellationToken ct) => ChangeAsync(id, (p, now) => p.AnswerQuery(text, now), ct);

    /// <summary>The insurer's approval: asked for, and recorded once a second insurance desk user checks it.</summary>
    public Task<Result<ApprovalRequestDto>> ApproveAsync(Guid id, ApprovePreAuthRequest request, CancellationToken ct) =>
        AskAmountAsync(id, request.Version, new PreAuthAmountPayload(id, PreAuthAmountKind.Approval, null, Money.Round(request.ApprovedAmount), request.Reference), ct);

    public Task<Result> RejectAsync(Guid id, string reason, CancellationToken ct) =>
        ChangeAsync(id, (p, now) => p.Reject(reason, now), ct, "insurance.preauth.rejected");

    public Task<Result> RequestEnhancementAsync(Guid id, RequestEnhancementRequest request, CancellationToken ct) =>
        ChangeAsync(id, (p, now) => p.RequestEnhancement(request.Amount, request.Reason, now), ct);

    /// <summary>
    /// The insurer's answer to an enhancement: a refusal at once (the patient keeps paying that part), an approval only once
    /// a second insurance desk user checks it.
    /// </summary>
    public async Task<Result<ApprovalRequestDto?>> RespondToEnhancementAsync(Guid id, Guid enhancementId, RespondEnhancementRequest request, CancellationToken ct)
    {
        if (request.Approved)
        {
            var asked = await AskAmountAsync(
                id, request.Version, new PreAuthAmountPayload(id, PreAuthAmountKind.Enhancement, enhancementId, Money.Round(request.ApprovedAmount), request.Reference), ct);
            return asked.IsSuccess ? asked.Value : asked.Error!;
        }

        var refused = await ChangeAsync(id, (p, now) => p.RespondToEnhancement(enhancementId, false, 0m, request.Reference, now), ct, "insurance.preauth.enhancement", request.Version);
        return refused.IsSuccess ? Result<ApprovalRequestDto?>.Ok(null) : refused.Error!;
    }

    /// <summary>The insurer's final approval against the final bill: asked for, and recorded once a second insurance desk user checks it.</summary>
    public Task<Result<ApprovalRequestDto>> RecordFinalApprovalAsync(Guid id, FinalApprovalRequest request, CancellationToken ct) =>
        AskAmountAsync(id, request.Version, new PreAuthAmountPayload(id, PreAuthAmountKind.Final, null, Money.Round(request.Amount), request.Reference), ct);

    /// <summary>
    /// Asks a second insurance desk user to check an amount the insurer approved. The amount is tried on a copy of the
    /// pre-authorisation first, so a request the record would refuse (not submitted, already answered) is refused now;
    /// nothing on the pre-authorisation or the bill changes until it is approved.
    /// </summary>
    private async Task<Result<ApprovalRequestDto>> AskAmountAsync(Guid id, uint seenVersion, PreAuthAmountPayload payload, CancellationToken ct)
    {
        var copy = await db.Set<PreAuthorization>().AsNoTracking().Include(p => p.Enhancements).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (copy is null)
        {
            return Error.NotFound(nameof(PreAuthorization), id);
        }

        if (copy.Version != seenVersion)
        {
            return Error.ConcurrencyConflict;
        }

        var before = copy.IsApproved ? copy.EffectiveApprovedAmount : 0m;
        var tried = Apply(copy, payload, time.GetUtcNow());
        if (tried.IsFailure)
        {
            return tried.Error!;
        }

        var payer = await db.Set<Payer>().Where(p => p.Id == copy.PayerId).Select(p => p.Name).FirstOrDefaultAsync(ct);
        var patient = await db.Set<Patient>().Where(p => p.Id == copy.PatientId).Select(p => new { p.FullName, p.Uhid }).FirstOrDefaultAsync(ct);
        var what = payload.Kind switch
        {
            PreAuthAmountKind.Enhancement => "enhancement approved",
            PreAuthAmountKind.Final => "final approval",
            _ => "approval",
        };
        List<ApprovalDetail> details =
        [
            new("Pre-authorisation", $"{copy.Number}, {payer ?? "the insurer"}, for {(patient is null ? "the patient" : $"{patient.FullName} ({patient.Uhid})")}"),
            new("Insurer's " + what, $"₹{Money.FormatInr(payload.Amount)}" + (string.IsNullOrWhiteSpace(payload.Reference) ? string.Empty : $", reference {payload.Reference.Trim()}")),
            new("Insurer pays up to", $"₹{Money.FormatInr(before)} → ₹{Money.FormatInr(copy.EffectiveApprovedAmount)}: that much of the bill moves from the patient to the insurer"),
            new("Check", "Against the insurer's or TPA's approval letter: the amount, the reference and the patient"),
        ];
        return await approvals.RequestAsync(
            AmountKind,
            new ApprovalSubject(nameof(PreAuthorization), copy.Id, copy.Version, copy.FacilityId, copy.PatientId),
            payload,
            $"Record {copy.Number}'s {what} of ₹{Money.FormatInr(payload.Amount)}",
            details,
            $"Insurer's {what} as recorded by the desk",
            ct);
    }

    /// <summary>
    /// Records an amount a second insurance desk user checked, in the approval's transaction (the approval saves): only on
    /// the pre-authorisation as it was asked about (<c>approval.stale</c> when it changed since).
    /// </summary>
    internal async Task<Result<string>> AmountApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PreAuthAmountPayload>(request.Payload);
        var preAuth = await LoadAsync(asked.PreAuthorizationId, ct);
        if (preAuth is null)
        {
            return Error.NotFound(nameof(PreAuthorization), asked.PreAuthorizationId);
        }

        if (preAuth.Version != request.SubjectVersion)
        {
            return Error.Rule("approval.stale",
                $"{preAuth.Number} changed after this was asked for (a query, a rejection or another amount was recorded), so the amount was not recorded. Ask again, for the pre-authorisation as it is now.");
        }

        var applied = Apply(preAuth, asked, time.GetUtcNow());
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var checker = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        var action = asked.Kind switch
        {
            PreAuthAmountKind.Enhancement => "insurance.preauth.enhancement",
            PreAuthAmountKind.Final => "insurance.preauth.final_approval",
            _ => "insurance.preauth.approved",
        };
        audit.Add(new AuditEntry(AuditCategories.Data, action,
            $"{preAuth.Number}: {preAuth.Status}, effective approval ₹{Money.FormatInr(preAuth.EffectiveApprovedAmount)} (asked for by {request.RequestedByName}, checked by {checker})")
        {
            PatientId = preAuth.PatientId,
            EntityType = nameof(PreAuthorization),
            EntityId = preAuth.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { asked.Amount, kind = asked.Kind.ToString(), approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return $"{preAuth.Number}: the insurer pays up to ₹{Money.FormatInr(preAuth.EffectiveApprovedAmount)}";
    }

    private static Result Apply(PreAuthorization preAuth, PreAuthAmountPayload asked, DateTimeOffset now) => asked.Kind switch
    {
        PreAuthAmountKind.Enhancement => preAuth.RespondToEnhancement(asked.EnhancementId ?? Guid.Empty, true, asked.Amount, asked.Reference, now),
        PreAuthAmountKind.Final => preAuth.RecordFinalApproval(asked.Amount, asked.Reference, now),
        _ => preAuth.Approve(asked.Amount, asked.Reference, now),
    };

    private async Task<Result> ChangeAsync(
        Guid id, Func<PreAuthorization, DateTimeOffset, Result> change, CancellationToken ct, string? auditAction = null, uint? seenVersion = null)
    {
        var preAuth = await LoadAsync(id, ct);
        if (preAuth is null)
        {
            return Error.NotFound(nameof(PreAuthorization), id);
        }

        if (seenVersion is { } version)
        {
            db.SetExpectedVersion(preAuth, version);
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
        db.Set<PreAuthorization>().Include(p => p.Enhancements).Include(p => p.Procedures).FirstOrDefaultAsync(p => p.Id == id, ct);

    /// <summary>
    /// Procedures the desk may code, from PM-JAY's HBP master (active procedures, with the scheme's base rate) or the
    /// hospital's ICD-10-PCS catalogue, by code prefix or words of the name.
    /// </summary>
    public async Task<IReadOnlyList<PreAuthProcedureOption>> ProcedureOptionsAsync(PreAuthProcedureSystem system, string? search, CancellationToken ct)
    {
        if (system == PreAuthProcedureSystem.Hbp)
        {
            var term = (search ?? string.Empty).Trim();
            var query = db.Set<HbpProcedure>().AsNoTracking().Where(p => p.IsActive);
            if (term.Length > 0)
            {
                var code = term.ToUpperInvariant();
                var like = term.ToLikePattern();
                query = query.Where(p => p.Code.StartsWith(code) || EF.Functions.ILike(p.ProcedureName, like, QueryExtensions.LikeEscape)
                    || (p.PackageName != null && EF.Functions.ILike(p.PackageName, like, QueryExtensions.LikeEscape)));
            }

            return await query.OrderBy(p => p.Code).Take(25)
                .Select(p => new PreAuthProcedureOption(PreAuthProcedureSystem.Hbp, p.Code, p.ProcedureName, p.Rate)).ToListAsync(ct);
        }

        return [.. (await procedureCatalogue.SearchAsync(search, 25, ct))
            .Select(p => new PreAuthProcedureOption(PreAuthProcedureSystem.Icd10Pcs, p.Code, p.Title, null))];
    }

    /// <summary>
    /// The planned procedures checked against the masters: each HBP code must be an active procedure of the imported HBP
    /// master, each ICD-10-PCS code an active code of the hospital's catalogue. The name each line keeps is the master's.
    /// </summary>
    private async Task<Result<List<PreAuthProcedure>>> CodedProceduresAsync(IReadOnlyList<PreAuthProcedureLine>? lines, CancellationToken ct)
    {
        if (lines is null || lines.Count == 0)
        {
            return new List<PreAuthProcedure>();
        }

        var normalized = lines.Select(l => l with { Code = l.Code.Trim().ToUpperInvariant() }).ToList();
        var hbpCodes = normalized.Where(l => l.System == PreAuthProcedureSystem.Hbp).Select(l => l.Code).Distinct().ToList();
        var hbp = hbpCodes.Count == 0
            ? new Dictionary<string, (string Name, bool IsActive)>()
            : await db.Set<HbpProcedure>().AsNoTracking().Where(p => hbpCodes.Contains(p.Code))
                .ToDictionaryAsync(p => p.Code, p => (Name: p.ProcedureName, p.IsActive), StringComparer.Ordinal, ct);
        var pcsCodes = normalized.Where(l => l.System == PreAuthProcedureSystem.Icd10Pcs).Select(l => l.Code).Distinct().ToList();
        var pcs = await procedureCatalogue.FindAsync(pcsCodes, ct);

        var coded = new List<PreAuthProcedure>();
        for (var i = 0; i < normalized.Count; i++)
        {
            var line = normalized[i];
            var field = $"procedures[{i}].code";
            string name;
            if (line.System == PreAuthProcedureSystem.Hbp)
            {
                if (!hbp.TryGetValue(line.Code, out var procedure))
                {
                    return Error.Field(field, $"{line.Code} is not in the imported PM-JAY HBP master.");
                }

                if (!procedure.IsActive)
                {
                    return Error.Field(field, $"{line.Code} was retired from the HBP master; choose the procedure that replaced it.");
                }

                name = procedure.Name;
            }
            else
            {
                if (!pcs.TryGetValue(line.Code, out var procedure))
                {
                    return Error.Field(field, $"{line.Code} is not an ICD-10-PCS code of the hospital's procedure catalogue.");
                }

                if (!procedure.IsActive)
                {
                    return Error.Field(field, $"{line.Code} is switched off in the procedure catalogue.");
                }

                name = procedure.Title;
            }

            coded.Add(new PreAuthProcedure
            {
                LineNumber = i + 1,
                System = line.System,
                Code = line.Code,
                Name = name.Length > 500 ? name[..500] : name,
                Quantity = line.Quantity,
                Amount = Money.Round(line.Amount),
            });
        }

        return coded;
    }

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

    private static string Signature(IEnumerable<PreAuthProcedure> lines) =>
        string.Join('|', lines.OrderBy(l => l.LineNumber).Select(l => $"{l.System}:{l.Code}:{l.Quantity}:{l.Amount:0.00}"));

    /// <summary>The planned procedures as coded, in the audit trail (the lines themselves are replaced on each save).</summary>
    private void AuditProcedures(PreAuthorization preAuth) =>
        audit.Add(new AuditEntry(
            AuditCategories.Data,
            "insurance.preauth.procedures",
            $"{preAuth.Number}: planned procedures {preAuth.ProcedureText(500) ?? "cleared"}")
        {
            PatientId = preAuth.PatientId,
            EntityType = nameof(PreAuthorization),
            EntityId = preAuth.Id.ToString(),
            Details = new
            {
                procedures = preAuth.Procedures.OrderBy(l => l.LineNumber)
                    .Select(l => new { system = l.System.ToString(), l.Code, l.Quantity, l.Amount }).ToList(),
            },
        });

    private static void Apply(PreAuthorization preAuth, SavePreAuthRequest request, List<PreAuthProcedure> procedures)
    {
        preAuth.AdmissionId = request.AdmissionId;
        preAuth.VisitId = request.VisitId;
        preAuth.RequestedAmount = Money.Round(request.RequestedAmount);
        preAuth.DiagnosisCodes = Clean(request.DiagnosisCodes);
        preAuth.Procedures.AddRange(procedures);
        preAuth.ClinicalSummary = Clean(request.ClinicalSummary);
        preAuth.ExpectedStayDays = request.ExpectedStayDays;
        preAuth.DocumentFileIds = request.DocumentFileIds?.Distinct().ToList() ?? [];
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum PreAuthAmountKind
{
    /// <summary>The insurer's approval of the request.</summary>
    Approval = 0,

    /// <summary>An enhancement the insurer approved during the stay.</summary>
    Enhancement,

    /// <summary>The insurer's final approval against the final bill.</summary>
    Final,
}

/// <summary>An amount the insurer approved, as the desk recorded it: which pre-authorisation, which kind, how much, the insurer's reference.</summary>
public sealed record PreAuthAmountPayload(Guid PreAuthorizationId, PreAuthAmountKind Kind, Guid? EnhancementId, decimal Amount, string? Reference);

/// <summary>
/// A pre-authorised amount (MAKER-CHECKER.md, slice 12): one insurance desk user records what the insurer approved
/// (<c>insurance.preauth.manage</c>); another insurance desk user checks it against the insurer's letter.
/// </summary>
internal sealed class PreAuthAmountApproval(PreAuthService preAuths) : IApprovalAction
{
    public string Kind => PreAuthService.AmountKind;

    public string Title => "Pre-authorised amount";

    public string ApprovePermission => BillingPermissions.PreAuthManage;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => preAuths.AmountApprovedAsync(request, ct);
}
