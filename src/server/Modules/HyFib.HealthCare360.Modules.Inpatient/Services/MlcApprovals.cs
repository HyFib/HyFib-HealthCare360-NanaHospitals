using HyFib.HealthCare360.Modules.Clinical;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.Inpatient.Services;

/// <summary>A stay's medico-legal standing: whether it is an MLC, and the MLC register number it is linked to.</summary>
public sealed record MlcStanding(bool IsMlc, string? MlcNumber)
{
    /// <summary>"MLC no. ER/26/0001" or "not a medico-legal case".</summary>
    public string Words => !IsMlc ? "not a medico-legal case" : MlcNumber is { Length: > 0 } number ? $"MLC no. {number}" : "MLC (no number yet)";
}

/// <summary>A stay's medico-legal standing as asked, and the standing it replaces.</summary>
public sealed record MlcChangePayload(Guid AdmissionId, MlcStanding Before, MlcStanding Asked);

/// <summary>A change to a stay's medico-legal flag waiting for a second person: the request, what it does, who asked and when.</summary>
public sealed record PendingMlcChange(Guid ApprovalId, string Summary, string RequestedBy, DateTimeOffset RequestedAt, MlcStanding Asked);

/// <summary>
/// When a stay's medico-legal flag changes at once and when it waits (maker-checker, slice 7). Marking a stay as an MLC, or
/// giving an MLC its register number, is made at once. Clearing the flag, or changing or removing the number it is linked
/// by, unlinks the stay from the MLC register: the police trail goes, and for the victim of a sexual offence or an acid
/// attack the free treatment the law requires (BNSS 2023, s.397) ends. That waits for a second person.
/// </summary>
public static class MlcChangeRule
{
    public static bool Waits(MlcStanding now, MlcStanding asked)
    {
        ArgumentNullException.ThrowIfNull(now);
        ArgumentNullException.ThrowIfNull(asked);
        if (!now.IsMlc)
        {
            return false;
        }

        return !asked.IsMlc || (now.MlcNumber is { Length: > 0 } number && !string.Equals(number, asked.MlcNumber, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// A stay's medico-legal flag cleared or unlinked: the admission desk asks, with a reason, and someone else who keeps the
/// MLC register (<see cref="ClinicalPermissions.MlcRegister"/>) approves. Until then the stay and its visit stay medico-legal,
/// and a victim's free treatment stays. Made only over the flag as it was asked (<c>approval.stale</c>), on a stay still
/// open, and audited with both names.
/// </summary>
public sealed class MlcApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "inpatient.mlc_clear";

    /// <summary>Refused: another change to the flag is waiting (the approvals' own code).</summary>
    public const string WaitingCode = "approval.already_requested";

    /// <summary>The change waiting on this stay's flag, if any.</summary>
    public async Task<PendingMlcChange?> PendingAsync(Guid admissionId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Admission) && r.SubjectId == admissionId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Summary, r.RequestedByName, r.RequestedAt, r.Payload })
            .FirstOrDefaultAsync(ct);
        return request is null
            ? null
            : new PendingMlcChange(request.Id, request.Summary, request.RequestedByName, request.RequestedAt, ApprovalPayload.Read<MlcChangePayload>(request.Payload).Asked);
    }

    /// <summary>Asks for the flag change, in the caller's transaction, once the rest of the stay is saved.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(Admission admission, MlcStanding before, MlcStanding asked, string reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admission);
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == admission.PatientId).Select(p => new { p.Uhid, p.FullName, p.IsVip }).FirstAsync(ct);
        var free = await db.Set<Visit>().Where(v => v.Id == admission.VisitId).Select(v => v.FreeTreatmentReason).FirstOrDefaultAsync(ct);
        List<ApprovalDetail> details =
        [
            new("Stay", $"{admission.AdmissionNumber} · {patient.Uhid} · {(patient.IsVip ? PatientNames.Restricted(patient.FullName) : patient.FullName)}"),
            new("Medico-legal", $"{before.Words} → {asked.Words}"),
            new("What it reaches", "The stay is no longer tied to the MLC register entry and its police intimation. Until it is approved, the stay stays medico-legal."),
        ];
        if (free is not null)
        {
            details.Add(new("Free treatment", $"The stay is treated free of cost by law ({free}); unlinked, it is billed as usual from then on, unless the visit it came from is free."));
        }

        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Admission), admission.Id, admission.Version, admission.FacilityId, admission.PatientId),
            new MlcChangePayload(admission.Id, before, asked),
            $"Medico-legal flag of {admission.AdmissionNumber}: {before.Words} → {asked.Words}",
            details,
            reason,
            ct);
    }

    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<MlcChangePayload>(request.Payload);
        var admission = await db.Set<Admission>().FirstOrDefaultAsync(a => a.Id == asked.AdmissionId, ct);
        if (admission is null)
        {
            return Error.Rule("admission.gone", "The admission no longer exists, so nothing was changed.");
        }

        if (!admission.IsActive)
        {
            return Error.Rule("admission.closed", $"{admission.AdmissionNumber} is closed, so nothing was changed.");
        }

        var now = new MlcStanding(admission.IsMlc, admission.MlcNumber);
        if (now != asked.Before)
        {
            return Error.Rule("approval.stale", $"{admission.AdmissionNumber}'s medico-legal flag changed after this was asked for (now {now.Words}), so nothing was changed. Ask again.");
        }

        (admission.IsMlc, admission.MlcNumber) = (asked.Asked.IsMlc, asked.Asked.MlcNumber);
        var visit = await db.Set<Visit>().FirstOrDefaultAsync(v => v.Id == admission.VisitId, ct);
        string? freeBefore = null;
        if (visit is not null)
        {
            freeBefore = visit.FreeTreatmentReason;
            (visit.IsMlc, visit.MlcNumber) = (admission.IsMlc, admission.MlcNumber);
            visit.FreeTreatmentReason = await FreeTreatment.ForVisitAsync(db, visit, ct);
        }

        var words = $"{asked.Before.Words} → {asked.Asked.Words}";
        audit.Add(new AuditEntry(AuditCategories.Clinical, "admission.mlc_cleared",
            Fit($"Medico-legal flag of {admission.AdmissionNumber}: {words}: {request.Reason} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            PatientId = admission.PatientId,
            EntityType = nameof(Admission),
            EntityId = admission.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new
            {
                before = asked.Before,
                after = asked.Asked,
                freeTreatmentBefore = freeBefore,
                freeTreatmentAfter = visit?.FreeTreatmentReason,
                approval = request.Id,
                requestedBy = request.RequestedBy,
                approvedBy = current.UserId,
            },
        });
        return words;
    }

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A stay's medico-legal flag cleared or unlinked: decided by someone who keeps the MLC register.</summary>
internal sealed class MlcClearApproval(MlcApprovals mlc) : IApprovalAction
{
    public string Kind => MlcApprovals.Kind;

    public string Title => "Medico-legal flag cleared";

    public string ApprovePermission => ClinicalPermissions.MlcRegister;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => mlc.ApplyAsync(request, ct);
}
