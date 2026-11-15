using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// A temporary password for an account with no mobile number or e-mail address, as asked for: sent to the holder's own,
/// given with the request, which become the account's once it is approved.
/// </summary>
/// <param name="Stamp">The account's password and contact details when it was asked (<see cref="AccountApprovals"/>).</param>
/// <param name="Phone">The holder's mobile number to send it to; null in a request from before it was sent (nothing is made).</param>
/// <param name="Email">The holder's e-mail address to send it to.</param>
public sealed record PasswordResetPayload(Guid UserId, string Stamp, string? Phone = null, string? Email = null);

/// <summary>An account's two-step verification reset, as asked for.</summary>
/// <param name="Stamp">The account's two-step verification when it was asked.</param>
public sealed record MfaResetPayload(Guid UserId, string Stamp);

/// <summary>Another member of staff's e-mail address and mobile number, the ones the change replaces and the ones asked for.</summary>
public sealed record ContactChangePayload(Guid UserId, string? EmailBefore, string? PhoneBefore, string? Email, string? Phone);

/// <summary>An account linked to a practitioner record, the record it was linked to before (none for a first link) and the one asked for.</summary>
public sealed record PractitionerLinkPayload(Guid UserId, Guid? Before, Guid Asked);

/// <summary>A change to an account waiting for a second administrator: its request, what it changes, who asked and when.</summary>
public sealed record PendingAccountChange(Guid ApprovalId, string Kind, string Summary, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// Nobody takes over a colleague's account alone (MAKER-CHECKER.md, slice 4). One administrator who holds
/// <see cref="PlatformPermissions.UsersManage"/> could otherwise change another user's e-mail address, reset their
/// password (and be shown it) and their two-step verification, and sign in as them; or link an account to another doctor's
/// practitioner record, and sign their notes, prescriptions and ABDM records as that doctor. So:
/// <list type="bullet">
/// <item>a temporary password goes to the account's own mobile number and e-mail address, never to an administrator, the
/// one who approves included (<see cref="CredentialNotices.QueueTemporaryPassword"/>); for an account with neither, the
/// administrator gives the holder's own, and the reset waits for a second administrator who checks them: approved, they
/// become the account's and the password goes there (<see cref="PasswordResetKind"/>);</item>
/// <item>a two-step verification reset (<see cref="MfaResetKind"/>), another user's e-mail address or mobile number
/// (<see cref="ContactChangeKind"/>, the old ones told once it changes) and an account linked or re-linked to a practitioner
/// (<see cref="PractitionerLinkKind"/>) wait for a second administrator.</item>
/// </list>
/// The checker holds <see cref="PlatformPermissions.GrantsApprove"/>, as for access given (<see cref="GrantApprovals"/>), and
/// the same rule decides whether a change waits: a hospital setting up, which has never had two people who may approve,
/// makes it at once, audited; once it has had two, a change nobody else may approve is refused, but for a Super
/// Administrator's break-glass. Nobody approves a change to their own account, and the approver must be allowed to manage
/// the account. A change is made only over the account as it was asked for (<c>approval.stale</c>): for the fields it is
/// about, not the row's version, which a sign-in attempt changes too, so a user locked out and trying again does not undo
/// the reset asked for them.
/// </summary>
internal sealed class AccountApprovals(
    HospitalDbContext db,
    ICurrentUser current,
    IPermissionService permissions,
    IAuditLogger audit,
    IApprovals approvals,
    GrantApprovals grants,
    INotificationService notifications,
    PasswordHashing hashing,
    ISessionValidator sessions,
    IOptions<PlatformOptions> options,
    TimeProvider time)
{
    public const string PasswordResetKind = "identity.password_reset";
    public const string MfaResetKind = "identity.mfa_reset";
    public const string ContactChangeKind = "identity.contact_change";
    public const string PractitionerLinkKind = "identity.practitioner_link";

    /// <summary>Refused: the approver is the account's holder.</summary>
    public const string OwnAccountCode = "approval.own_account";

    private const string NoReason = "No reason given.";

    private static readonly string[] Kinds = [PasswordResetKind, MfaResetKind, ContactChangeKind, PractitionerLinkKind];

    /// <summary>
    /// Whether a change to the account <paramref name="userId"/> waits for a second administrator, is made at once, or is
    /// refused: as for access given, with the account's administrative permissions, which only someone who holds them may
    /// approve. A change to one's own account waits for someone else, and is refused when nobody else may approve it.
    /// </summary>
    public async Task<Result<GrantDecision>> DecideAsync(Guid userId, CancellationToken ct) =>
        await grants.DecideAsync(self: userId == current.UserId, await grants.AdministrativeOfUserAsync(userId, ct), ct);

    /// <summary>
    /// Asks a second administrator to send <paramref name="user"/>, who has no mobile number or e-mail address, a temporary
    /// password at the holder's own <paramref name="phone"/> and <paramref name="email"/>. The approver sees them in full, to
    /// check they are the holder's: they become the account's.
    /// </summary>
    public Task<Result<ApprovalRequestDto>> AskPasswordResetAsync(User user, string? phone, string? email, CancellationToken ct) =>
        RequestAsync(
            PasswordResetKind,
            user,
            new PasswordResetPayload(user.Id, PasswordStamp(user), phone, email),
            $"Temporary password for {user.DisplayName} ({user.UserName}), sent to the {Destination(phone, email)} given for them",
            [
                new("User", $"{user.DisplayName} ({user.UserName})"),
                new("Mobile number and e-mail address now", "None"),
                new("Send it to", string.Join("; ", new[] { phone is null ? null : $"mobile {phone}", email is null ? null : $"e-mail {email}" }.OfType<string>())),
                new("What approving does", "These become the account's mobile number and e-mail address, and a temporary password is sent there: nobody is shown it. Check they are the holder's own. Their sessions end, and their PIN and quick-unlock devices are removed."),
            ],
            ct);

    /// <summary>Asks a second administrator to reset <paramref name="user"/>'s two-step verification.</summary>
    public Task<Result<ApprovalRequestDto>> AskMfaResetAsync(User user, CancellationToken ct) =>
        RequestAsync(
            MfaResetKind,
            user,
            new MfaResetPayload(user.Id, MfaStamp(user)),
            $"Two-step verification reset for {user.DisplayName} ({user.UserName})",
            [
                new("User", $"{user.DisplayName} ({user.UserName})"),
                new("Two-step verification now", user.MfaEnabled ? "On" : "Off"),
                new("What approving does", "They sign in with their password alone and set up two-step verification again. Their sessions end."),
            ],
            ct);

    /// <summary>Asks a second administrator to change another member of staff's e-mail address or mobile number.</summary>
    public Task<Result<ApprovalRequestDto>> AskContactChangeAsync(User user, string? email, string? phone, string? reason, CancellationToken ct)
    {
        List<ApprovalDetail> details = [new("User", $"{user.DisplayName} ({user.UserName})")];
        List<string> parts = [];
        if (!string.Equals(email, user.Email, StringComparison.Ordinal))
        {
            details.Add(new("E-mail address", $"{user.Email ?? "none"} → {email ?? "none"}"));
            parts.Add($"e-mail {email ?? "removed"}");
        }

        if (!string.Equals(phone, user.Phone, StringComparison.Ordinal))
        {
            details.Add(new("Mobile number", $"{user.Phone ?? "none"} → {phone ?? "none"}"));
            parts.Add($"mobile {phone ?? "removed"}");
        }

        details.Add(new("What it reaches", "Temporary passwords from a reset go to these. The old ones are told once it changes."));
        return RequestAsync(
            ContactChangeKind,
            user,
            new ContactChangePayload(user.Id, user.Email, user.Phone, email, phone),
            $"Contact details of {user.DisplayName} ({user.UserName}): {string.Join("; ", parts)}",
            details,
            ct,
            reason);
    }

    /// <summary>Asks a second administrator to link <paramref name="user"/> to the practitioner <paramref name="practitionerId"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskPractitionerLinkAsync(User user, Guid practitionerId, string? reason, CancellationToken ct)
    {
        var asked = await DescribePractitionerAsync(practitionerId, ct);
        var before = user.PractitionerId is { } linked ? await DescribePractitionerAsync(linked, ct) : null;
        var others = await db.Set<User>().AsNoTracking()
            .Where(u => u.PractitionerId == practitionerId && u.Id != user.Id)
            .OrderBy(u => u.DisplayName).Select(u => u.DisplayName + " (" + u.UserName + ")").ToListAsync(ct);
        return await RequestAsync(
            PractitionerLinkKind,
            user,
            new PractitionerLinkPayload(user.Id, user.PractitionerId, practitionerId),
            before is null
                ? $"Link {user.DisplayName} ({user.UserName}) to {asked.Name}"
                : $"Link {user.DisplayName} ({user.UserName}) to {asked.Name}, not {before.Name}",
            [
                new("User", $"{user.DisplayName} ({user.UserName})"),
                new("Linked now", before?.Words ?? "No practitioner"),
                new("Asked for", asked.Words),
                new("Other accounts linked to it", others.Count == 0 ? "None" : string.Join(", ", others)),
                new("What it reaches", $"The account signs notes, prescriptions, discharge summaries and ABDM records as {asked.Name}, and their queue and fees follow it."),
            ],
            ct,
            reason);
    }

    /// <summary>The changes to this account waiting for a second administrator.</summary>
    public async Task<IReadOnlyList<PendingAccountChange>> PendingForUserAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => Kinds.Contains(r.Kind) && r.SubjectType == nameof(User) && r.SubjectId == userId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new PendingAccountChange(r.Id, r.Kind, r.Summary, r.RequestedByName, r.RequestedAt))
            .ToListAsync(ct);
    }

    /// <summary>The contact details asked for this account and waiting, if any.</summary>
    public async Task<ContactChangePayload?> PendingContactAsync(Guid userId, CancellationToken ct) =>
        await PendingAsync(ContactChangeKind, userId, ct) is { } request ? ApprovalPayload.Read<ContactChangePayload>(request.Payload) : null;

    /// <summary>The practitioner link asked for this account and waiting, if any.</summary>
    public async Task<PractitionerLinkPayload?> PendingLinkAsync(Guid userId, CancellationToken ct) =>
        await PendingAsync(PractitionerLinkKind, userId, ct) is { } request ? ApprovalPayload.Read<PractitionerLinkPayload>(request.Payload) : null;

    /// <summary>Whether <paramref name="practitionerId"/> names a practitioner in use, who may be linked to an account.</summary>
    public Task<bool> LinkableAsync(Guid practitionerId, CancellationToken ct) =>
        db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId && p.IsActive, ct);

    /// <summary>
    /// Makes an approved temporary password, in the approval's transaction, for the account as it was asked: still with no
    /// mobile number or e-mail address (one added since gets it instead: ask again), and the same password. The mobile number
    /// and e-mail address given with the request become the account's, and the password goes there; the approver is told
    /// only where, as an administrator's reset tells them (<see cref="CredentialNotices.QueueTemporaryPassword"/>).
    /// </summary>
    internal async Task<Result<string>> ApplyPasswordResetAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PasswordResetPayload>(request.Payload);
        if (asked.Phone is null && asked.Email is null)
        {
            // Asked for before the password was always sent, to be handed over by whoever approved it: that is not done.
            return Error.Rule("account.reset_destination",
                "This was asked for to hand the password over in person, which is no longer done, so nothing was changed. Reset the password again, with the holder's own mobile number or e-mail address.");
        }

        var target = await TargetAsync(asked.UserId, ct);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var user = target.Value;
        if (PasswordStamp(user) != asked.Stamp)
        {
            return Stale(user, "its password, mobile number or e-mail address");
        }

        var now = time.GetUtcNow();
        user.Phone = asked.Phone;
        user.Email = asked.Email;
        var (password, ended) = await ResetPasswordAsync(db, user, hashing, options, now, ct);
        var sentTo = CredentialNotices.QueueTemporaryPassword(notifications, user, password, user.TemporaryPasswordExpiresAt!.Value, now);
        audit.Add(new AuditEntry(AuditCategories.Security, "user.password_reset",
            Fit($"Password reset for {user.UserName}, asked for by {request.RequestedByName}, approved by {ApproverName()}: the temporary password sent to their {string.Join(" and ", sentTo)}, given with the request and now the account's", 1000))
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, requestedBy = request.RequestedBy, sentTo },
        });
        CredentialNotices.QueueEmail(notifications, user, CredentialNotices.PasswordReset, now);
        CredentialNotices.AddInApp(db, user, CredentialNotices.PasswordReset, now);
        db.AfterCommit(() => Invalidate(sessions, ended));
        return $"temporary password sent to {user.UserName}'s {string.Join(" and ", sentTo)} (given with the request, now the account's); nobody was shown it";
    }

    /// <summary>Resets an approved account's two-step verification, in the approval's transaction, if it is as it was asked.</summary>
    internal async Task<Result<string>> ApplyMfaResetAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<MfaResetPayload>(request.Payload);
        var target = await TargetAsync(asked.UserId, ct);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var user = target.Value;
        if (MfaStamp(user) != asked.Stamp)
        {
            return Stale(user, "its two-step verification");
        }

        var now = time.GetUtcNow();
        var ended = await ResetMfaAsync(db, user, now, ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "user.mfa_reset",
            Fit($"Two-step verification reset for {user.UserName}, asked for by {request.RequestedByName}, approved by {ApproverName()}", 1000))
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, requestedBy = request.RequestedBy },
        });
        CredentialNotices.QueueEmail(notifications, user, CredentialNotices.MfaReset, now);
        CredentialNotices.AddInApp(db, user, CredentialNotices.MfaReset, now);
        db.AfterCommit(() => Invalidate(sessions, ended));
        return $"two-step verification reset for {user.UserName}";
    }

    /// <summary>
    /// Changes an approved account's e-mail address and mobile number, in the approval's transaction, if they are still the
    /// ones the request replaces; then tells the old ones.
    /// </summary>
    internal async Task<Result<string>> ApplyContactChangeAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<ContactChangePayload>(request.Payload);
        var target = await TargetAsync(asked.UserId, ct);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var user = target.Value;
        if (!string.Equals(user.Email, asked.EmailBefore, StringComparison.Ordinal) || !string.Equals(user.Phone, asked.PhoneBefore, StringComparison.Ordinal))
        {
            return Stale(user, "its mobile number or e-mail address");
        }

        var now = time.GetUtcNow();
        user.Email = asked.Email;
        user.Phone = asked.Phone;
        audit.Add(new AuditEntry(AuditCategories.Security, "user.contact_changed",
            Fit($"Contact details of {user.UserName} changed, asked for by {request.RequestedByName}, approved by {ApproverName()}", 1000))
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, requestedBy = request.RequestedBy, emailChanged = asked.Email != asked.EmailBefore, phoneChanged = asked.Phone != asked.PhoneBefore },
        });
        CredentialNotices.QueueContactChanged(notifications, user, asked.EmailBefore, asked.PhoneBefore, now);
        CredentialNotices.AddInApp(db, user, CredentialNotices.ContactChanged, now);
        return $"contact details of {user.UserName} changed; the old ones were told";
    }

    /// <summary>
    /// Links an approved account to its practitioner, in the approval's transaction, if it is still linked as it was when
    /// asked and the practitioner is still in use.
    /// </summary>
    internal async Task<Result<string>> ApplyPractitionerLinkAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<PractitionerLinkPayload>(request.Payload);
        var target = await TargetAsync(asked.UserId, ct);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var user = target.Value;
        if (user.PractitionerId != asked.Before)
        {
            return Stale(user, "the practitioner it is linked to");
        }

        var practitioner = await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == asked.Asked && p.IsActive).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
        if (practitioner is null)
        {
            return Error.Rule("account.practitioner_gone", "The practitioner asked for is no longer in use, so the account was not linked to them.");
        }

        user.PractitionerId = asked.Asked;
        audit.Add(new AuditEntry(AuditCategories.Security, "user.practitioner_linked",
            Fit($"{user.UserName} linked to {practitioner}, asked for by {request.RequestedByName}, approved by {ApproverName()}", 1000))
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { approval = request.Id, requestedBy = request.RequestedBy, before = asked.Before, practitioner = asked.Asked },
        });
        return $"{user.UserName} linked to {practitioner}";
    }

    /// <summary>
    /// Gives the account a new temporary password, in <paramref name="db"/> and unsaved, as an administrator's reset does:
    /// it must be changed at the next sign-in and stops working after <see cref="SecurityOptions.TemporaryPasswordHours"/>;
    /// the lock, the PIN and the quick-unlock devices go, and the sessions end. Returns the password and the sessions ended:
    /// the caller saves, then tells every server (<see cref="Invalidate"/>).
    /// </summary>
    internal static async Task<(string Password, List<UserSession> Ended)> ResetPasswordAsync(
        HospitalDbContext db, User user, PasswordHashing hashing, IOptions<PlatformOptions> options, DateTimeOffset now, CancellationToken ct)
    {
        var password = AccessAdminEndpoints.GenerateTemporaryPassword();
        user.PasswordHash = hashing.Hash(password);
        user.MustChangePassword = true;
        user.TemporaryPasswordExpiresAt = now + TimeSpan.FromHours(Math.Max(1, options.Value.Security.TemporaryPasswordHours));
        user.PasswordBreachedAt = null;
        user.FailedLoginCount = 0;
        user.LastFailedLoginAt = null;
        user.LockoutEndsAt = null;

        // The lock-screen PIN goes too: whoever may know the old password may know it. Until the account's holder sets a
        // new one, their screen unlocks with the password. So do the devices set up to unlock it, which were set up with
        // the old password: a lost laptop among them unlocks nothing.
        user.PinHash = null;
        db.RemoveRange(await db.Set<WebAuthnCredential>().Where(c => c.UserId == user.Id).ToListAsync(ct));
        return (password, await RevokeAllSessionsAsync(db, user.Id, "password_reset", now, ct));
    }

    /// <summary>Turns the account's two-step verification off, in <paramref name="db"/> and unsaved, and ends its sessions (returned).</summary>
    internal static async Task<List<UserSession>> ResetMfaAsync(HospitalDbContext db, User user, DateTimeOffset now, CancellationToken ct)
    {
        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaLastTimeStep = null;

        // A lost or stolen authenticator: whoever holds it keeps no session either.
        return await RevokeAllSessionsAsync(db, user.Id, "mfa_reset", now, ct);
    }

    /// <summary>
    /// Ends the user's open sessions in <paramref name="db"/>, unsaved, and returns them: the caller saves, then tells every
    /// server with <see cref="Invalidate"/>.
    /// </summary>
    internal static async Task<List<UserSession>> RevokeAllSessionsAsync(HospitalDbContext db, Guid userId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.Set<UserSession>().Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in active)
        {
            session.RevokedAt = now;
            session.RevokedReason = reason;
        }

        return active;
    }

    /// <summary>
    /// Once their end is saved, never before: another server told sooner could read a session as still active and keep that
    /// for the 30 seconds it caches it (SCAL-01).
    /// </summary>
    internal static void Invalidate(ISessionValidator validator, IEnumerable<UserSession> ended)
    {
        foreach (var session in ended)
        {
            validator.Invalidate(session.Id);
        }
    }

    /// <summary>
    /// What a waiting temporary password is asked over: the password and where one could be sent. A new password, or a
    /// mobile number or e-mail address added, makes the request stale.
    /// </summary>
    internal static string PasswordStamp(User user) => Stamp(user.PasswordHash, user.Email, user.Phone);

    /// <summary>What a waiting two-step verification reset is asked over: whether it is on, and its secret.</summary>
    internal static string MfaStamp(User user) => Stamp(user.MfaEnabled ? "on" : "off", user.MfaSecret);

    private static string Stamp(params string?[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values.Select(v => v ?? "\0")))));

    private async Task<Result<ApprovalRequestDto>> RequestAsync<TPayload>(
        string kind, User user, TPayload payload, string summary, IReadOnlyList<ApprovalDetail> details, CancellationToken ct, string? reason = null)
    {
        var facility = current.FacilityId ?? user.DefaultFacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        return await approvals.RequestAsync(
            kind,
            new ApprovalSubject(nameof(User), user.Id, user.Version, facility, null),
            payload,
            Fit(summary, 300),
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason.Trim(),
            ct);
    }

    /// <summary>
    /// The account an approval changes, if the approver may change it: not their own, still a member of staff, and holding
    /// no administrative permission the approver lacks.
    /// </summary>
    private async Task<Result<User>> TargetAsync(Guid userId, CancellationToken ct)
    {
        if (current.UserId == userId)
        {
            return Error.Forbidden(OwnAccountCode, "You cannot approve a change to your own account. Another administrator who may approve it must decide it.");
        }

        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == userId && u.Type == UserType.Staff, ct);
        if (user is null)
        {
            return Error.Rule("account.user_gone", "The user no longer exists, so nothing was changed.");
        }

        var mayManage = await AccessAdminEndpoints.EnsureMayManageAsync(user.Id, db, current, permissions, ct);
        return mayManage.IsFailure ? mayManage.Error! : user;
    }

    private async Task<ApprovalRequest?> PendingAsync(string kind, Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == kind && r.SubjectType == nameof(User) && r.SubjectId == userId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>A practitioner as the approver needs to tell them apart: name, code, kind and registration.</summary>
    private async Task<PractitionerWords> DescribePractitionerAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Set<Practitioner>().IgnoreQueryFilters([HospitalDbContext.SoftDeleteFilter]).AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.DisplayName, x.Code, x.Kind, x.RegistrationCouncil, x.RegistrationNumber })
            .FirstOrDefaultAsync(ct);
        if (p is null)
        {
            return new PractitionerWords("a practitioner no longer listed", "A practitioner no longer listed");
        }

        var registration = p.RegistrationNumber is null ? "no registration number" : $"registration {(p.RegistrationCouncil is null ? string.Empty : p.RegistrationCouncil + " ")}{p.RegistrationNumber}";
        return new PractitionerWords(p.DisplayName, $"{p.DisplayName} ({p.Code}), {p.Kind}, {registration}");
    }

    private static Error Stale(User user, string what) =>
        Error.Rule("approval.stale", $"{user.DisplayName}'s account changed after this was asked for ({what}), so nothing was changed. Ask again, for the account as it is now.");

    private string ApproverName() => current.DisplayName ?? current.UserName ?? "another administrator";

    /// <summary>Where a temporary password is to go, in words: "mobile number", "e-mail address" or both.</summary>
    private static string Destination(string? phone, string? email) =>
        phone is not null && email is not null ? "mobile number and e-mail address" : phone is not null ? "mobile number" : "e-mail address";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private sealed record PractitionerWords(string Name, string Words);
}

/// <summary>
/// A temporary password for an account with no mobile number or e-mail address: one administrator asks, giving the holder's
/// own, and another checks and approves them; the password goes there, and nobody is shown it.
/// </summary>
internal sealed class PasswordResetApproval(AccountApprovals accounts) : IApprovalAction
{
    public string Kind => AccountApprovals.PasswordResetKind;

    public string Title => "Temporary password for an account without contact details";

    public string ApprovePermission => PlatformPermissions.GrantsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => accounts.ApplyPasswordResetAsync(request, ct);
}

/// <summary>An account's two-step verification reset: one administrator asks, another approves.</summary>
internal sealed class MfaResetApproval(AccountApprovals accounts) : IApprovalAction
{
    public string Kind => AccountApprovals.MfaResetKind;

    public string Title => "Two-step verification reset";

    public string ApprovePermission => PlatformPermissions.GrantsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => accounts.ApplyMfaResetAsync(request, ct);
}

/// <summary>Another member of staff's e-mail address or mobile number: one administrator asks, another approves.</summary>
internal sealed class ContactChangeApproval(AccountApprovals accounts) : IApprovalAction
{
    public string Kind => AccountApprovals.ContactChangeKind;

    public string Title => "Contact details of an account";

    public string ApprovePermission => PlatformPermissions.GrantsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => accounts.ApplyContactChangeAsync(request, ct);
}

/// <summary>An account linked to a practitioner record, which it then signs as: one administrator asks, another approves.</summary>
internal sealed class PractitionerLinkApproval(AccountApprovals accounts) : IApprovalAction
{
    public string Kind => AccountApprovals.PractitionerLinkKind;

    public string Title => "Account linked to a practitioner";

    public string ApprovePermission => PlatformPermissions.GrantsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => accounts.ApplyPractitionerLinkAsync(request, ct);
}
