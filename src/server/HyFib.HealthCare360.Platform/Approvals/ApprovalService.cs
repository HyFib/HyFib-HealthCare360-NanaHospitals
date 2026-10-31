using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Approvals;

/// <summary>
/// One kind of change that needs a second person. The module that owns the record implements it: it names who may
/// approve, and makes the change once it is approved, checking again everything it checked when the change was asked for
/// (the record's version included: <see cref="ApprovalRequest.SubjectVersion"/>). It makes the change in the approval's
/// transaction and does not commit it.
/// </summary>
public interface IApprovalAction
{
    /// <summary>A stable key, for example <c>billing.credit_note</c>.</summary>
    string Kind { get; }

    /// <summary>The kind in words, for the checker's list.</summary>
    string Title { get; }

    /// <summary>The permission a checker needs. The maker's permission is the one on the endpoint that asks.</summary>
    string ApprovePermission { get; }

    /// <summary>Makes the approved change; returns what it made (a number to show), or why it can no longer be made.</summary>
    Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>The record a change is asked for, as it was when it was asked.</summary>
public sealed record ApprovalSubject(string Type, Guid Id, uint? Version, Guid FacilityId, Guid? PatientId);

/// <summary>One thing the checker needs to see, for example ("Balance due", "₹4,500.00").</summary>
public sealed record ApprovalDetail(string Label, string Value);

public sealed record ApprovalRequestDto(
    Guid Id, string Kind, string Title, string Summary, IReadOnlyList<ApprovalDetail> Details, string Reason, ApprovalStatus Status, bool IsExpired,
    string SubjectType, Guid SubjectId, Guid? PatientId, Guid RequestedBy, string RequestedByName, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt,
    Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote, string? Outcome, bool CanDecide, bool CanWithdraw, uint Version);

/// <summary>What a module calls to ask for a second person's approval instead of making a change at once.</summary>
public interface IApprovals
{
    /// <summary>
    /// Records the change as asked for and tells those who may approve it; nothing is changed until one of them, another
    /// person, approves it. A second request of the same kind for the same record is refused while the first waits.
    /// </summary>
    Task<Result<ApprovalRequestDto>> RequestAsync<TPayload>(
        string kind, ApprovalSubject subject, TPayload payload, string summary, IReadOnlyList<ApprovalDetail> details, string reason, CancellationToken ct);
}

public enum ApprovalView
{
    /// <summary>Requests waiting for the signed-in user's decision.</summary>
    ToDecide = 0,

    /// <summary>Requests the signed-in user asked for.</summary>
    Mine,

    /// <summary>Requests about one record, that the user asked for or may decide.</summary>
    Subject,
}

/// <summary>Asks for approvals, and lists, approves, rejects and withdraws them (the approvals inbox).</summary>
/// <remarks>
/// The actions are resolved when first needed rather than injected: the module service that asks for an approval
/// (<see cref="IApprovals"/>) is usually the one its action calls to make the change, and injecting both ways would be a cycle.
/// </remarks>
internal sealed class ApprovalService(
    HospitalDbContext db,
    IServiceProvider services,
    IPermissionService permissions,
    ICurrentUser currentUser,
    IAuditLogger audit,
    INotificationService notifications,
    TimeProvider time) : IApprovals
{
    private const string Category = "approvals";

    /// <summary>The record, or the request itself, changed while the approval was being made: nothing was saved.</summary>
    private static readonly Error Concurrent =
        Error.Conflict("approval.conflict", "The record changed while this was being approved, so nothing was changed. Open the request again and decide it.");

    private IReadOnlyList<IApprovalAction>? actions;

    private IReadOnlyList<IApprovalAction> Actions => actions ??= [.. services.GetServices<IApprovalAction>()];

    public async Task<Result<ApprovalRequestDto>> RequestAsync<TPayload>(
        string kind, ApprovalSubject subject, TPayload payload, string summary, IReadOnlyList<ApprovalDetail> details, string reason, CancellationToken ct)
    {
        if (StaffUser() is not { } maker)
        {
            return Error.Forbidden("approval.staff_only", "Only staff can ask for a change.");
        }

        var action = Action(kind) ?? throw new InvalidOperationException($"No approval action is registered for '{kind}'.");
        var waiting = await db.Set<ApprovalRequest>().AnyAsync(
            r => r.Kind == kind && r.SubjectType == subject.Type && r.SubjectId == subject.Id && r.Status == ApprovalStatus.Pending && r.ExpiresAt > time.GetUtcNow(), ct);
        if (waiting)
        {
            return Error.Conflict("approval.already_requested", $"A request of this kind for this {Words(subject.Type)} is already waiting for approval. Withdraw it first, or wait for its decision.");
        }

        var now = time.GetUtcNow();
        var text = ApprovalPayload.Write(payload);
        var request = new ApprovalRequest
        {
            FacilityId = subject.FacilityId,
            Kind = kind,
            Title = action.Title,
            Summary = Fit(summary, 300),
            SubjectType = subject.Type,
            SubjectId = subject.Id,
            SubjectVersion = subject.Version,
            PatientId = subject.PatientId,
            Details = ApprovalPayload.Write(details),
            Payload = text,
            PayloadHash = ApprovalPayload.Hash(text),
            Reason = Fit(reason.Trim(), 500),
            RequestedBy = maker,
            RequestedByName = Name(),
            RequestedAt = now,
            ExpiresAt = now + ApprovalRequest.Lifetime,
        };
        db.Add(request);
        audit.Add(Entry(request, "approval.requested", $"{request.Title} asked for: {request.Summary} ({request.Reason})", AuditSeverity.Info));
        await db.SaveChangesAsync(ct);

        await notifications.NotifyPermissionHoldersAsync(action.ApprovePermission, request.FacilityId, $"{request.Title} waiting for approval",
            $"{request.Summary}. Asked for by {request.RequestedByName}.", Link(request.Id), Category, cancellationToken: ct);
        return await ToDtoAsync(request, ct);
    }

    public async Task<Result<ApprovalRequestDto>> ApproveAsync(Guid id, string? note, uint version, CancellationToken ct)
    {
        var loaded = await DecisionAsync(id, version, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (request, action, checker) = loaded.Value;
        var now = time.GetUtcNow();
        Error? failure;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                var applied = await action.ApplyAsync(request, ct);
                if (applied.IsSuccess)
                {
                    request.MarkApproved(checker, Name(), note, applied.Value, now);
                    audit.Add(Entry(request, "approval.approved",
                        $"{request.Title} approved by {Name()}, asked for by {request.RequestedByName}: {request.Summary} — {applied.Value}", AuditSeverity.Warning));
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    await notifications.NotifyUsersAsync([request.RequestedBy], $"{request.Title} approved", $"{request.Summary}: {applied.Value}.", Link(request.Id), Category,
                        cancellationToken: ct);
                    return await ToDtoAsync(request, ct);
                }

                failure = applied.Error!;
            }
            catch (DbUpdateConcurrencyException)
            {
                failure = Concurrent;
            }
        }

        // Nothing the action staged is kept. A refusal that is about the checker (a record they may not open), or a save
        // that lost a race with another change, leaves the request waiting; any other means the change can no longer be
        // made as asked.
        db.ChangeTracker.Clear();
        if (failure.Type is ErrorType.Forbidden or ErrorType.Unauthorized || failure.Code == Concurrent.Code)
        {
            return failure;
        }

        var failed = await db.Set<ApprovalRequest>().FirstAsync(r => r.Id == id, ct);
        failed.MarkFailed(checker, Name(), note, Fit(failure.Message, 500), now);
        audit.Add(Entry(failed, "approval.failed",
            $"{failed.Title} approved by {Name()} but not made: {failure.Message} ({failed.Summary})", AuditSeverity.Warning));
        await db.SaveChangesAsync(ct);
        await notifications.NotifyUsersAsync([failed.RequestedBy], $"{failed.Title} could not be made", $"{failed.Summary}: {failure.Message}", Link(failed.Id), Category,
            "warning", ct);
        return failure;
    }

    public async Task<Result<ApprovalRequestDto>> RejectAsync(Guid id, string reason, uint version, CancellationToken ct)
    {
        var loaded = await DecisionAsync(id, version, ct);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (request, _, checker) = loaded.Value;
        request.MarkRejected(checker, Name(), reason, time.GetUtcNow());
        audit.Add(Entry(request, "approval.rejected", $"{request.Title} rejected by {Name()}: {request.Summary} — {reason.Trim()}", AuditSeverity.Info));
        await db.SaveChangesAsync(ct);
        await notifications.NotifyUsersAsync([request.RequestedBy], $"{request.Title} rejected", $"{request.Summary}: {reason.Trim()}", Link(request.Id), Category, cancellationToken: ct);
        return await ToDtoAsync(request, ct);
    }

    public async Task<Result<ApprovalRequestDto>> WithdrawAsync(Guid id, uint version, CancellationToken ct)
    {
        if (StaffUser() is not { } user)
        {
            return Error.Forbidden("approval.staff_only", "Only staff can withdraw a request.");
        }

        var request = await db.Set<ApprovalRequest>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
        {
            return Error.NotFound("Approval request", id);
        }

        db.SetExpectedVersion(request, version);
        var withdrawn = request.Withdraw(user, time.GetUtcNow());
        if (withdrawn.IsFailure)
        {
            return withdrawn.Error!;
        }

        audit.Add(Entry(request, "approval.withdrawn", $"{request.Title} withdrawn by {request.RequestedByName}: {request.Summary}", AuditSeverity.Info));
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(request, ct);
    }

    public async Task<Result<ApprovalRequestDto>> GetAsync(Guid id, CancellationToken ct)
    {
        if (StaffUser() is not { } user)
        {
            return Error.Forbidden("approval.staff_only", "Only staff can see approval requests.");
        }

        var request = await db.Set<ApprovalRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null || (request.RequestedBy != user && !(await DecidableKindsAsync(user, ct)).Contains(request.Kind)))
        {
            return Error.NotFound("Approval request", id);
        }

        return await ToDtoAsync(request, ct);
    }

    public async Task<Result<PagedResult<ApprovalRequestDto>>> ListAsync(
        ApprovalView view, ApprovalStatus? status, string? subjectType, Guid? subjectId, PageRequest page, CancellationToken ct)
    {
        if (StaffUser() is not { } user)
        {
            return Error.Forbidden("approval.staff_only", "Only staff can see approval requests.");
        }

        var kinds = await DecidableKindsAsync(user, ct);
        var decidable = kinds.ToArray();
        var now = time.GetUtcNow();
        var requests = db.Set<ApprovalRequest>().AsNoTracking();
        requests = view switch
        {
            ApprovalView.Mine => requests.Where(r => r.RequestedBy == user),
            ApprovalView.Subject when subjectType is not null && subjectId is not null =>
                requests.Where(r => r.SubjectType == subjectType && r.SubjectId == subjectId && (r.RequestedBy == user || decidable.Contains(r.Kind))),
            ApprovalView.Subject => requests.Where(_ => false),
            _ => requests.Where(r => r.Status == ApprovalStatus.Pending && r.ExpiresAt > now && r.RequestedBy != user && decidable.Contains(r.Kind)),
        };

        if (status is not null)
        {
            requests = requests.Where(r => r.Status == status);
        }

        var ordered = view == ApprovalView.ToDecide ? requests.OrderBy(r => r.RequestedAt) : requests.OrderByDescending(r => r.RequestedAt);
        var paged = await ordered.ToPagedResultAsync(page, ct);
        return new PagedResult<ApprovalRequestDto>(paged.Items.Select(r => ToDto(r, user, kinds, now)).ToList(), paged.Page, paged.PageSize, paged.TotalCount);
    }

    private async Task<Result<(ApprovalRequest Request, IApprovalAction Action, Guid Checker)>> DecisionAsync(Guid id, uint version, CancellationToken ct)
    {
        if (StaffUser() is not { } checker)
        {
            return Error.Forbidden("approval.staff_only", "Only staff can decide a request.");
        }

        var request = await db.Set<ApprovalRequest>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null || Action(request.Kind) is not { } action)
        {
            return Error.NotFound("Approval request", id);
        }

        if (!await permissions.HasPermissionAsync(checker, action.ApprovePermission, ct))
        {
            return Error.Forbidden("approval.not_permitted", $"You cannot decide this request: it needs the permission to approve {request.Title.ToLowerInvariant()}.");
        }

        db.SetExpectedVersion(request, version);
        var decidable = request.CanBeDecidedBy(checker, time.GetUtcNow());
        if (decidable.IsFailure)
        {
            if (decidable.Error!.Code == "approval.altered")
            {
                await audit.WriteAsync(Entry(request, "approval.altered", $"{request.Title} refused: its payload does not match the hash it was asked with ({request.Summary})",
                    AuditSeverity.Critical), ct);
            }

            return decidable.Error!;
        }

        return (request, action, checker);
    }

    private async Task<IReadOnlySet<string>> DecidableKindsAsync(Guid user, CancellationToken ct)
    {
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in Actions)
        {
            if (await permissions.HasPermissionAsync(user, action.ApprovePermission, ct))
            {
                kinds.Add(action.Kind);
            }
        }

        return kinds;
    }

    private async Task<ApprovalRequestDto> ToDtoAsync(ApprovalRequest request, CancellationToken ct) =>
        ToDto(request, currentUser.UserId ?? Guid.Empty, await DecidableKindsAsync(currentUser.UserId ?? Guid.Empty, ct), time.GetUtcNow());

    private static ApprovalRequestDto ToDto(ApprovalRequest r, Guid user, IReadOnlySet<string> kinds, DateTimeOffset now)
    {
        var expired = r.IsExpired(now);
        var pending = r.Status == ApprovalStatus.Pending && !expired;
        return new ApprovalRequestDto(
            r.Id, r.Kind, r.Title, r.Summary, ApprovalPayload.Read<List<ApprovalDetail>>(r.Details), r.Reason, r.Status, expired, r.SubjectType, r.SubjectId, r.PatientId,
            r.RequestedBy, r.RequestedByName, r.RequestedAt, r.ExpiresAt, r.DecidedBy, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.Outcome,
            CanDecide: pending && r.RequestedBy != user && kinds.Contains(r.Kind),
            CanWithdraw: r.Status == ApprovalStatus.Pending && r.RequestedBy == user,
            r.Version);
    }

    private AuditEntry Entry(ApprovalRequest request, string action, string summary, AuditSeverity severity) =>
        new(AuditCategories.Data, action, Fit(summary, 1000))
        {
            EntityType = nameof(ApprovalRequest),
            EntityId = request.Id.ToString(),
            PatientId = request.PatientId,
            Severity = severity,
            Details = new { request.Kind, request.SubjectType, request.SubjectId, request.SubjectVersion, request.PayloadHash, request.RequestedBy, maker = request.RequestedByName },
        };

    private IApprovalAction? Action(string kind) => Actions.FirstOrDefault(a => string.Equals(a.Kind, kind, StringComparison.Ordinal));

    private Guid? StaffUser() => currentUser.IsPatient ? null : currentUser.UserId;

    private string Name() => currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";

    private static string Link(Guid id) => $"/approvals/{id}";

    private static string Words(string type) => string.Concat(type.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}
