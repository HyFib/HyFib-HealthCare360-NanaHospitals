using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Organization;

/// <summary>A doctor's own consultation fees: for a new visit and for a follow-up (0: the consultation service's price applies).</summary>
public sealed record PractitionerFees(decimal NewVisit, decimal FollowUp)
{
    public static readonly PractitionerFees None = new(0m, 0m);

    public static PractitionerFees Of(Practitioner practitioner)
    {
        ArgumentNullException.ThrowIfNull(practitioner);
        return new(practitioner.ConsultationFeeNew, practitioner.ConsultationFeeFollowUp);
    }

    public void ApplyTo(Practitioner practitioner)
    {
        ArgumentNullException.ThrowIfNull(practitioner);
        practitioner.ConsultationFeeNew = NewVisit;
        practitioner.ConsultationFeeFollowUp = FollowUp;
    }
}

/// <summary>A practitioner's fees as asked, and the fees they replace (fees changed meanwhile are not overwritten).</summary>
public sealed record PractitionerFeePayload(Guid PractitionerId, PractitionerFees Before, PractitionerFees Asked);

/// <summary>A practitioner's fees waiting for a second person: the request, what is asked, who asked and when.</summary>
public sealed record PendingPractitionerFee(Guid ApprovalId, PractitionerFees Asked, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// A doctor's own consultation fee is charged to a self-pay patient instead of the consultation service's approved price
/// (the billing module's <c>PriceResolver.ConsultationOverride</c>; a payer's negotiated tariff still wins), and is the
/// fee an appointment is booked at. So it changes only as a service's price does (MAKER-CHECKER.md, slice 6): saving a
/// practitioner saves the rest of the record at once and asks for the fees, which stay as they are, and are charged as
/// they are, until someone else who holds <see cref="PlatformPermissions.PricesApprove"/> approves; never the doctor whose
/// fee it is. A new practitioner starts with no fee of their own (the service price applies) until theirs is approved.
/// The approver reads the fees before and after in words, and the change is made only over the fees it replaces
/// (<c>approval.stale</c>).
/// </summary>
public sealed class PractitionerFeeApprovals(HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, TimeProvider time)
{
    public const string Kind = "masters.practitioner_fee";

    /// <summary>Refused at approval: the doctor whose fee it is does not approve it.</summary>
    public const string OwnFeeCode = "masters.practitioner_fee_own";

    /// <summary>Refused on save: another change to the fees is waiting for approval (the approvals' own code).</summary>
    public const string WaitingCode = "approval.already_requested";

    private const string NoReason = "No reason given.";

    /// <summary>The fees waiting for approval for this practitioner, if any.</summary>
    public async Task<PendingPractitionerFee?> PendingAsync(Guid practitionerId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var request = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Practitioner) && r.SubjectId == practitionerId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Payload, r.RequestedByName, r.RequestedAt })
            .FirstOrDefaultAsync(ct);
        return request is null
            ? null
            : new PendingPractitionerFee(request.Id, ApprovalPayload.Read<PractitionerFeePayload>(request.Payload).Asked, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>Asks a second person to set <paramref name="practitioner"/>'s fees, in the caller's transaction, once the rest is saved.</summary>
    public async Task<Result<ApprovalRequestDto>> AskAsync(Practitioner practitioner, PractitionerFees before, PractitionerFees asked, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(practitioner);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(asked);
        List<ApprovalDetail> details = [new("Practitioner", $"{practitioner.DisplayName} ({practitioner.Code})")];
        if (before.NewVisit != asked.NewVisit)
        {
            details.Add(new("Consultation fee, new visit", $"{Words(before.NewVisit)} → {Words(asked.NewVisit)}"));
        }

        if (before.FollowUp != asked.FollowUp)
        {
            details.Add(new("Consultation fee, follow-up", $"{Words(before.FollowUp)} → {Words(asked.FollowUp)}"));
        }

        details.Add(new("What it reaches",
            "What a self-pay patient is charged for a consultation with this doctor, instead of the consultation service's approved price, and the fee appointments are booked at. A payer's negotiated tariff still wins."));
        var facility = current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Practitioner), practitioner.Id, practitioner.Version, facility, null),
            new PractitionerFeePayload(practitioner.Id, before, asked),
            Fit($"Consultation fee of {practitioner.DisplayName}: {Describe(before, asked)}", 300),
            details,
            NoReason,
            ct);
    }

    /// <summary>
    /// Sets approved fees, in the approval's transaction, if the practitioner still has the fees the request replaces, and
    /// never by the doctor whose fee it is.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PractitionerFeePayload>(request.Payload);
        if (current.PractitionerId == asked.PractitionerId)
        {
            return Error.Forbidden(OwnFeeCode, "This is your own consultation fee, so someone else who approves prices decides it.");
        }

        var practitioner = await db.Set<Practitioner>().FirstOrDefaultAsync(p => p.Id == asked.PractitionerId, ct);
        if (practitioner is null)
        {
            return Error.Rule("practitioner.gone", "The practitioner no longer exists, so nothing was changed.");
        }

        // Only the fees are compared: a department or a phone number changed meanwhile does not stand in the way.
        var before = PractitionerFees.Of(practitioner);
        if (before != asked.Before)
        {
            return Error.Rule("approval.stale",
                $"{practitioner.DisplayName}'s consultation fee changed after this was asked for ({Describe(asked.Before, before)}), so nothing was changed. Ask again.");
        }

        asked.Asked.ApplyTo(practitioner);
        audit.Add(new AuditEntry(AuditCategories.Admin, "practitioner.fee_changed",
            Fit($"Consultation fee of {practitioner.DisplayName}: {Describe(before, asked.Asked)} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(Practitioner),
            EntityId = practitioner.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { before, after = asked.Asked, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = current.UserId },
        });
        return Describe(before, asked.Asked);
    }

    /// <summary>What changes from <paramref name="from"/> to <paramref name="to"/>, in words.</summary>
    public static string Describe(PractitionerFees from, PractitionerFees to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        List<string> parts = [];
        if (from.NewVisit != to.NewVisit)
        {
            parts.Add($"new visit {Words(from.NewVisit)} → {Words(to.NewVisit)}");
        }

        if (from.FollowUp != to.FollowUp)
        {
            parts.Add($"follow-up {Words(from.FollowUp)} → {Words(to.FollowUp)}");
        }

        return parts.Count == 0 ? "unchanged" : string.Join("; ", parts);
    }

    private static string Words(decimal fee) => fee <= 0m ? "none (the service price)" : "₹" + Money.FormatInr(fee);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A doctor's consultation fee: one person asks, another who may approve prices decides (maker-checker).</summary>
internal sealed class PractitionerFeeApproval(PractitionerFeeApprovals fees) : IApprovalAction
{
    public string Kind => PractitionerFeeApprovals.Kind;

    public string Title => "Doctor's consultation fee";

    public string ApprovePermission => PlatformPermissions.PricesApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => fees.ApplyAsync(request, ct);
}
