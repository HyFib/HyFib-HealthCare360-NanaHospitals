using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.BloodBank;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Services;

/// <summary>The classification asked for, and why it is lowered.</summary>
public sealed record ReactionClassificationPayload(
    Guid ReactionId, ReactionType Type, ReactionSeverity Severity, ReactionImputability Imputability, string Reason);

/// <summary>A classification made at once (<see cref="Made"/>), or asked for because it lowers the reaction (<see cref="Asked"/>).</summary>
public sealed record ReactionClassificationOutcome(bool Made, ApprovalRequestDto? Asked);

/// <summary>
/// The blood bank's classification of a transfusion reaction while it is investigated (BB-07, haemovigilance). Raising
/// the severity or imputability, or a first assessment, is made at once; lowering either, which can take the reaction
/// out of haemovigilance reporting, waits for the blood bank medical officer (<see cref="SpecialtyCarePermissions.BloodBankReactionsApprove"/>),
/// never the officer who asked (maker-checker, slice 15). A hospital lets the investigating officer lower it alone only by
/// switching <see cref="DowngradeApprovalSetting"/> off, which waits for a second administrator; it is then audited at Warning.
/// </summary>
public sealed class ReactionClassificationService(
    HospitalDbContext db, IApprovals approvals, ISettingsService settings, IAuditLogger audit, ICurrentUser currentUser, TimeProvider time)
{
    public const string LowerKind = "bloodbank.reaction_lower";

    /// <summary>Facility setting (bool, on unless false): lowering a reaction waits for the medical officer.</summary>
    public const string DowngradeApprovalSetting = "bloodbank.reactionDowngradeApproval";

    /// <summary>Whether <paramref name="facilityId"/> asks for the medical officer (<see cref="DowngradeApprovalSetting"/>).</summary>
    public async Task<bool> ApprovalRequiredAsync(Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<bool?>(DowngradeApprovalSetting, facilityId, ct) != false;

    public async Task<Result<ReactionClassificationOutcome>> ClassifyAsync(Guid reactionId, ClassifyReactionRequest request, CancellationToken ct)
    {
        var reaction = await db.Set<TransfusionReaction>().FirstOrDefaultAsync(r => r.Id == reactionId, ct);
        if (reaction is null)
        {
            return Error.NotFound(nameof(TransfusionReaction), reactionId);
        }

        db.SetExpectedVersion(reaction, request.Version);
        var lowered = ReactionDowngrade.Lowers(reaction.Severity, reaction.Imputability, request.Severity, request.Imputability);
        if (lowered.Count == 0)
        {
            var made = reaction.Classify(request.Type, request.Severity, request.Imputability);
            if (made.IsFailure)
            {
                return made.Error!;
            }

            await db.SaveChangesAsync(ct);
            return new ReactionClassificationOutcome(true, null);
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Field("reason", "Say why the reaction is lowered: the investigation's findings that support it.");
        }

        if (reaction.Status == ReactionStatus.Closed)
        {
            return Error.Rule("bloodbank.reaction_closed", "This reaction report is closed.");
        }

        var name = currentUser.DisplayName ?? currentUser.UserName ?? "Staff";
        if (!await ApprovalRequiredAsync(reaction.FacilityId, ct))
        {
            reaction.Classify(request.Type, request.Severity, request.Imputability);
            reaction.MarkLowered(request.Reason, name, null);
            Audit(reaction, $"Transfusion reaction on unit {reaction.UnitNumber} lowered by {name} alone (the medical officer is not required here): {string.Join("; ", lowered)} — {request.Reason.Trim()}", null);
            await db.SaveChangesAsync(ct);
            return new ReactionClassificationOutcome(true, null);
        }

        var reportable = ReactionDowngrade.Reportable(reaction.Imputability) && !ReactionDowngrade.Reportable(request.Imputability);
        List<ApprovalDetail> details =
        [
            new("Unit", reaction.UnitNumber),
            new("Reported", $"{reaction.Severity} by {reaction.ReportedByName ?? "the ward"}, onset {HyFib.HealthCare360.Platform.Documents.DocumentFormat.DateTime(reaction.OnsetAt)}"),
            new("Lowered", string.Join("; ", lowered)),
            new("Type", request.Type.ToString()),
            new("Haemovigilance", reportable ? "No longer reported to the Haemovigilance Programme of India" : "Still reported"),
            new("Reason", request.Reason.Trim()),
            new("Who decides", "The blood bank medical officer, never the officer who asked."),
        ];
        var asked = await approvals.RequestAsync(LowerKind,
            new ApprovalSubject(nameof(TransfusionReaction), reaction.Id, reaction.Version, reaction.FacilityId, reaction.PatientId),
            new ReactionClassificationPayload(reaction.Id, request.Type, request.Severity, request.Imputability, request.Reason.Trim()),
            $"Lower the transfusion reaction on unit {reaction.UnitNumber}: {string.Join("; ", lowered)}", details, request.Reason, ct);
        return asked.IsFailure ? asked.Error! : new ReactionClassificationOutcome(false, asked.Value);
    }

    /// <summary>Lowers an approved reaction in the approval's transaction: only the reaction as it was asked for, still open.</summary>
    internal async Task<Result<string>> LowerApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ReactionClassificationPayload>(approval.Payload);
        var reaction = await db.Set<TransfusionReaction>().FirstOrDefaultAsync(r => r.Id == asked.ReactionId, ct);
        if (reaction is null)
        {
            return Error.NotFound(nameof(TransfusionReaction), asked.ReactionId);
        }

        if (reaction.Version != approval.SubjectVersion || reaction.Status == ReactionStatus.Closed)
        {
            return Error.Rule("approval.stale", $"The reaction on unit {reaction.UnitNumber} changed after this was asked for, so nothing was changed. Ask again.");
        }

        var lowered = ReactionDowngrade.Lowers(reaction.Severity, reaction.Imputability, asked.Severity, asked.Imputability);
        var made = reaction.Classify(asked.Type, asked.Severity, asked.Imputability);
        if (made.IsFailure)
        {
            return made.Error!;
        }

        var name = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        reaction.MarkLowered(asked.Reason, name, approval.RequestedByName);
        var outcome = $"Transfusion reaction on unit {reaction.UnitNumber} lowered: {string.Join("; ", lowered)}";
        Audit(reaction, $"{outcome}. Asked for by {approval.RequestedByName}, approved by {name}: {asked.Reason}", approval);
        return outcome;
    }

    private void Audit(TransfusionReaction reaction, string summary, ApprovalRequest? approval) =>
        audit.Add(new AuditEntry(AuditCategories.Data, "bloodbank.reaction_lowered", summary.Length <= 1000 ? summary : summary[..999] + "…")
        {
            PatientId = reaction.PatientId,
            EntityType = nameof(TransfusionReaction),
            EntityId = reaction.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { reaction.UnitNumber, reaction.Severity, reaction.Imputability, approval = approval?.Id, requestedBy = approval?.RequestedBy, approvedBy = currentUser.UserId, at = time.GetUtcNow() },
        });
}

/// <summary>A transfusion reaction lowered: the investigating officer asks, the blood bank medical officer approves.</summary>
internal sealed class ReactionLowerApproval(ReactionClassificationService classifications) : IApprovalAction
{
    public string Kind => ReactionClassificationService.LowerKind;

    public string Title => "Transfusion reaction lowered";

    public string ApprovePermission => SpecialtyCarePermissions.BloodBankReactionsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => classifications.LowerApprovedAsync(request, ct);
}
