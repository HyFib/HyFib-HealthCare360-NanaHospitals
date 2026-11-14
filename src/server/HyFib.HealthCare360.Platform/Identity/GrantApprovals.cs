using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>Roles and facilities given to a user, and the user reactivated, as asked for.</summary>
/// <param name="AllFacilities">Every facility given, those added later included (<see cref="User.AllFacilities"/>).</param>
public sealed record UserGrantPayload(Guid UserId, IReadOnlyList<Guid> RoleIds, IReadOnlyList<Guid> FacilityIds, bool Activate, bool AllFacilities = false);

/// <summary>Permissions added to a role, as asked for.</summary>
public sealed record RoleGrantPayload(Guid RoleId, IReadOnlyList<string> Permissions);

/// <summary>Access asked for a user, waiting for a second administrator: what it gives, who asked and when.</summary>
public sealed record PendingUserGrant(
    Guid ApprovalId, IReadOnlyList<Guid> RoleIds, IReadOnlyList<Guid> FacilityIds, bool Activate, string RequestedBy, DateTimeOffset RequestedAt,
    bool AllFacilities = false);

/// <summary>Permissions asked for a role, waiting for a second administrator.</summary>
public sealed record PendingRoleGrant(Guid ApprovalId, IReadOnlyList<string> Permissions, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>What happens to access an administrator gives (<see cref="GrantApprovals.DecideAsync"/>).</summary>
public enum GrantDecision
{
    /// <summary>It waits for another person who may approve grants.</summary>
    Ask,

    /// <summary>Given at once: the hospital has never had two people who may approve grants (setting up).</summary>
    GiveBeforeSecondApprover,

    /// <summary>Given at once by a Super Administrator when nobody else may approve it (break-glass), audited as critical.</summary>
    GiveBreakGlass,
}

/// <summary>Whether this installation has ever had two people who may approve grants. Once it has, it always has.</summary>
internal sealed class GrantLatch
{
    public bool IsSet { get; set; }
}

/// <summary>
/// Access is given by two people. Whatever widens someone's access, roles or facilities added to a user (all facilities
/// among them), a user reactivated, permissions added to a role, waits for a second person who holds
/// <see cref="PlatformPermissions.GrantsApprove"/>.
/// What narrows it (roles, facilities or permissions taken away, a user deactivated) applies at once. A hospital setting up has
/// one such person: until it has had two, grants are given at once and audited so. Once it has had two (a one-way latch,
/// <see cref="LatchAction"/> in the audit trail), a grant nobody else can approve is refused, except to a Super
/// Administrator, whose grant is given at once as a break-glass and audited as critical. Nobody approves access given to
/// themselves, nor gives it to themselves alone.
/// </summary>
internal sealed class GrantApprovals(
    HospitalDbContext db, ICurrentUser current, IPermissionService permissions, PermissionRegistry registry, IAuditLogger audit, IApprovals approvals,
    GrantLatch latch, TimeProvider time)
{
    public const string UserGrantKind = "identity.user_grant";
    public const string RoleGrantKind = "identity.role_grant";

    /// <summary>The audit entry written once, when the hospital first has two people who may approve grants.</summary>
    public const string LatchAction = "access.two_approvers";

    /// <summary>The audit entry of access given without a second person.</summary>
    public const string AloneAction = "access.granted_alone";

    /// <summary>Refused: access given to oneself with nobody else to approve it, or approved by the person it is given to.</summary>
    public const string SelfGrantCode = "access.self_grant";

    /// <summary>Refused: nobody else may approve the grant, and the one giving it is not a Super Administrator.</summary>
    public const string NoSecondApproverCode = "access.no_second_approver";

    /// <summary>System health's warning once the hospital has had two people who may approve grants and has fewer now.</summary>
    public const string OneApproverCode = "access.one_approver";

    /// <summary>How long System health says a break-glass grant was made.</summary>
    public static readonly TimeSpan BreakGlassShown = TimeSpan.FromDays(1);

    private const string NoReason = "No reason given.";

    /// <summary>
    /// Whether access the signed-in administrator gives waits for a second person, is given at once, or is refused.
    /// <paramref name="self"/>: it widens their own access (their own account, or a role they hold).
    /// <paramref name="administrative"/>: the administrative permissions it gives, which only someone who holds them may
    /// approve (as only someone who holds them may give them).
    /// </summary>
    public async Task<Result<GrantDecision>> DecideAsync(bool self, IReadOnlyCollection<string> administrative, CancellationToken ct)
    {
        var maker = current.UserId ?? Guid.Empty;
        var holdings = await HoldingsAsync(db, ct);
        var latched = await LatchedAsync(Approvers(holdings, []).Count, ct);
        var makerHoldsAll = (await permissions.GetPermissionsAsync(maker, ct)).Contains(PermissionRegistry.Wildcard);
        return Decide(Approvers(holdings, administrative), maker, self, latched, makerHoldsAll);
    }

    /// <summary>
    /// The rule <see cref="DecideAsync"/> applies to what it found: <paramref name="mayApprove"/>, who may approve this grant
    /// (<see cref="Approvers"/>); <paramref name="maker"/>, who gives it; <paramref name="self"/>, whether it widens the
    /// maker's own access; <paramref name="latched"/>, whether the hospital has ever had two people who may approve grants;
    /// <paramref name="makerHoldsAll"/>, whether the maker holds all-access (a Super Administrator).
    /// </summary>
    internal static Result<GrantDecision> Decide(IReadOnlyCollection<Guid> mayApprove, Guid maker, bool self, bool latched, bool makerHoldsAll)
    {
        if (mayApprove.Any(a => a != maker))
        {
            return GrantDecision.Ask;
        }

        if (self)
        {
            return Error.Rule(SelfGrantCode,
                "Nobody else may approve access you give yourself. Ask another administrator who may approve grants to give it.");
        }

        if (!latched)
        {
            return GrantDecision.GiveBeforeSecondApprover;
        }

        return makerHoldsAll
            ? GrantDecision.GiveBreakGlass
            : Error.Rule(NoSecondApproverCode,
                "Nobody else may approve this grant now. Ask the Super Administrator to give it, or to let a second administrator approve grants.");
    }

    /// <summary>Audits access given without a second person, as <paramref name="decision"/> allowed it.</summary>
    public void AuditGivenAlone(GrantDecision decision, string entityType, Guid entityId, string what) =>
        audit.Add(new AuditEntry(AuditCategories.Admin, AloneAction, Fit(decision == GrantDecision.GiveBreakGlass
            ? $"Given by a Super Administrator with nobody else to approve it (break-glass): {what}"
            : $"Given before the hospital had a second person to approve grants: {what}", 1000))
        {
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Severity = decision == GrantDecision.GiveBreakGlass ? AuditSeverity.Critical : AuditSeverity.Warning,
            Details = new { breakGlass = decision == GrantDecision.GiveBreakGlass },
        });

    /// <summary>
    /// Audits a change to an account, a practitioner's identity or an administration desktop's enrolment made without a
    /// second person (<see cref="AccountApprovals"/>), as <paramref name="decision"/> allowed it: the same entry as access
    /// given alone, so System health counts a break-glass among them.
    /// </summary>
    public void AuditMadeAlone(GrantDecision decision, string entityType, Guid entityId, string what) =>
        audit.Add(new AuditEntry(AuditCategories.Admin, AloneAction, Fit(decision == GrantDecision.GiveBreakGlass
            ? $"Made by a Super Administrator with nobody else to approve it (break-glass): {what}"
            : $"Made before the hospital had a second person to approve it: {what}", 1000))
        {
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Severity = decision == GrantDecision.GiveBreakGlass ? AuditSeverity.Critical : AuditSeverity.Warning,
            Details = new { breakGlass = decision == GrantDecision.GiveBreakGlass },
        });

    /// <summary>
    /// Asks a second person to give <paramref name="user"/> these roles and facilities, or all facilities
    /// (<paramref name="allFacilities"/>), or to reactivate them.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> AskForUserAsync(
        User user, IReadOnlyList<Guid> roleIds, IReadOnlyList<Guid> facilityIds, bool allFacilities, bool activate, string? reason, CancellationToken ct)
    {
        var roleNames = await db.Set<Role>().IgnoreQueryFilters().Where(r => roleIds.Contains(r.Id)).OrderBy(r => r.Name).Select(r => r.Name).ToListAsync(ct);
        var facilityNames = await db.Set<Facility>().IgnoreQueryFilters().Where(f => facilityIds.Contains(f.Id)).OrderBy(f => f.Name).Select(f => f.Name).ToListAsync(ct);
        var heldIds = user.Roles.Select(r => r.RoleId).ToList();
        var held = await db.Set<Role>().IgnoreQueryFilters().Where(r => heldIds.Contains(r.Id)).OrderBy(r => r.Name).Select(r => r.Name).ToListAsync(ct);

        List<string> parts = [];
        List<ApprovalDetail> details = [new("User", $"{user.DisplayName} ({user.UserName})")];
        if (roleNames.Count > 0)
        {
            parts.Add($"roles {string.Join(", ", roleNames)}");
            details.Add(new("Roles to give", string.Join(", ", roleNames)));
        }

        if (allFacilities)
        {
            parts.Add("all facilities");
            details.Add(new("Facilities to give", "All facilities, those added later included"));
        }
        else if (facilityNames.Count > 0)
        {
            parts.Add($"facilities {string.Join(", ", facilityNames)}");
            details.Add(new("Facilities to give", string.Join(", ", facilityNames)));
        }

        if (activate)
        {
            parts.Add("reactivated");
            details.Add(new("Account", "Reactivated"));
        }

        details.Add(new("Roles held now", held.Count == 0 ? "None" : string.Join(", ", held)));
        var facility = current.FacilityId ?? user.DefaultFacilityId ?? facilityIds.FirstOrDefault();
        if (facility == Guid.Empty)
        {
            facility = await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        }

        return await approvals.RequestAsync(
            UserGrantKind,
            new ApprovalSubject(nameof(User), user.Id, user.Version, facility, null),
            new UserGrantPayload(user.Id, [.. roleIds.Distinct()], allFacilities ? [] : [.. facilityIds.Distinct()], activate, allFacilities),
            $"Access for {user.DisplayName}: {string.Join("; ", parts)}",
            details,
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason,
            ct);
    }

    /// <summary>Asks a second person to add these permissions to <paramref name="role"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskForRoleAsync(Role role, IReadOnlyList<string> added, string? reason, CancellationToken ct)
    {
        var holders = await db.Set<UserRole>().CountAsync(ur => ur.RoleId == role.Id, ct);
        var facility = current.FacilityId ?? await db.Set<Facility>().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstOrDefaultAsync(ct);
        return await approvals.RequestAsync(
            RoleGrantKind,
            new ApprovalSubject(nameof(Role), role.Id, role.Version, facility, null),
            new RoleGrantPayload(role.Id, [.. added.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]),
            $"Permissions for the role {role.Name}: {string.Join(", ", added.Order(StringComparer.Ordinal))}",
            [
                new("Role", role.Name),
                new("Permissions to add", string.Join(", ", added.Order(StringComparer.Ordinal).Select(Describe))),
                new("People holding the role", holders.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ],
            string.IsNullOrWhiteSpace(reason) ? NoReason : reason,
            ct);
    }

    /// <summary>The grant to this user waiting for approval, if any.</summary>
    public async Task<PendingUserGrant?> PendingForUserAsync(Guid userId, CancellationToken ct)
    {
        if (await PendingAsync(UserGrantKind, nameof(User), userId, ct) is not { } request)
        {
            return null;
        }

        var asked = ApprovalPayload.Read<UserGrantPayload>(request.Payload);
        return new PendingUserGrant(request.Id, asked.RoleIds, asked.FacilityIds, asked.Activate, request.RequestedByName, request.RequestedAt, asked.AllFacilities);
    }

    /// <summary>The permissions waiting to be added to this role, if any.</summary>
    public async Task<PendingRoleGrant?> PendingForRoleAsync(Guid roleId, CancellationToken ct)
    {
        if (await PendingAsync(RoleGrantKind, nameof(Role), roleId, ct) is not { } request)
        {
            return null;
        }

        return new PendingRoleGrant(request.Id, ApprovalPayload.Read<RoleGrantPayload>(request.Payload).Permissions, request.RequestedByName, request.RequestedAt);
    }

    /// <summary>
    /// Gives an approved grant to a user, in the approval's transaction. It gives what was asked if it still may be given, by
    /// the person approving it: the user is still a member of staff, still active unless the grant reactivates them, and not
    /// that person; the roles and facilities still exist; and the approver may manage the user and give those roles.
    /// </summary>
    internal async Task<Result<string>> ApplyUserGrantAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<UserGrantPayload>(request.Payload);
        if (current.UserId == asked.UserId)
        {
            return Error.Forbidden(SelfGrantCode, "You cannot approve access given to yourself. Another person who may approve grants must decide it.");
        }

        var user = await db.Set<User>().Include(u => u.Roles).Include(u => u.Facilities).AsSplitQuery().FirstOrDefaultAsync(u => u.Id == asked.UserId, ct);
        if (user is null || user.Type != UserType.Staff)
        {
            return Error.Rule("access.user_gone", "The user no longer exists, so nothing was given.");
        }

        if (!user.IsActive && !asked.Activate)
        {
            return Error.Rule("access.user_inactive", $"{user.UserName} was deactivated after this was asked for, so nothing was given.");
        }

        var mayManage = await AccessAdminEndpoints.EnsureMayManageAsync(user.Id, db, current, permissions, ct);
        if (mayManage.IsFailure)
        {
            return mayManage.Error!;
        }

        var roleCheck = await AccessAdminEndpoints.ValidateRoleAssignmentAsync(asked.RoleIds, db, current, permissions, ct);
        if (roleCheck.IsFailure)
        {
            return roleCheck.Error!.Type == ErrorType.Validation
                ? Error.Rule("access.role_gone", "A role asked for no longer exists, so nothing was given. Ask again.")
                : roleCheck.Error!;
        }

        var facilities = asked.FacilityIds.Distinct().ToList();
        if (await db.Set<Facility>().CountAsync(f => facilities.Contains(f.Id), ct) != facilities.Count)
        {
            return Error.Rule("access.facility_gone", "A facility asked for no longer exists, so nothing was given. Ask again.");
        }

        var roles = asked.RoleIds.Where(r => user.Roles.All(x => x.RoleId != r)).Distinct().ToList();
        var added = user.AllFacilities ? [] : facilities.Where(f => user.Facilities.All(x => x.FacilityId != f)).ToList();
        user.Roles.AddRange(roles.Select(r => new UserRole { UserId = user.Id, RoleId = r }));
        user.Facilities.AddRange(added.Select(f => new UserFacility { UserId = user.Id, FacilityId = f }));
        var allFacilities = asked.AllFacilities && !user.AllFacilities;
        if (asked.AllFacilities)
        {
            // Every facility, those added later included: the list it replaces goes.
            user.AllFacilities = true;
            user.Facilities.Clear();
        }

        var reactivated = asked.Activate && !user.IsActive;
        user.IsActive = user.IsActive || asked.Activate;
        audit.Add(new AuditEntry(AuditCategories.Admin, "user.access_granted",
            Fit($"Gave {user.UserName} {request.Summary} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { roles, facilities = added, allFacilities, reactivated, approval = request.Id, requestedBy = request.RequestedBy },
        });
        db.AfterCommit(() => permissions.Invalidate(user.Id));
        var facilitiesGiven = allFacilities ? "all facilities" : Count(added.Count, "facility", "facilities");
        return $"{Count(roles.Count, "role")}, {facilitiesGiven}{(reactivated ? ", reactivated" : string.Empty)}";
    }

    /// <summary>
    /// Adds an approved grant's permissions to its role, in the approval's transaction, if they still may be added by the
    /// person approving it: the role still exists and may be changed, and the approver holds any administrative one.
    /// </summary>
    internal async Task<Result<string>> ApplyRoleGrantAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<RoleGrantPayload>(request.Payload);
        var role = await db.Set<Role>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == asked.RoleId, ct);
        if (role is null)
        {
            return Error.Rule("access.role_gone", "The role no longer exists, so nothing was added.");
        }

        if (role.Name is SystemRoles.SuperAdmin or SystemRoles.Patient)
        {
            return Error.Rule("role.protected", $"The {role.Name} role cannot be changed.");
        }

        if (asked.Permissions.Any(p => p == PermissionRegistry.Wildcard || !registry.IsDefined(p)))
        {
            return Error.Rule("access.permission_gone", "A permission asked for no longer exists, so nothing was added. Ask again.");
        }

        var mayGrant = await AccessAdminEndpoints.EnsureMayGrantAsync(asked.Permissions, current, permissions, ct);
        if (mayGrant.IsFailure)
        {
            return mayGrant.Error!;
        }

        var added = asked.Permissions.Where(p => role.Permissions.All(x => x.Permission != p)).Distinct(StringComparer.Ordinal).ToList();
        role.Permissions.AddRange(added.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        role.UpdatedAt = time.GetUtcNow(); // a version bump even when only child rows change
        audit.Add(new AuditEntry(AuditCategories.Admin, "role.permissions_granted",
            Fit($"Added to the role {role.Name}: {string.Join(", ", added)} (asked for by {request.RequestedByName}, approved by {current.DisplayName ?? current.UserName})", 1000))
        {
            EntityType = nameof(Role),
            EntityId = role.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { added, approval = request.Id, requestedBy = request.RequestedBy },
        });
        db.AfterCommit(permissions.InvalidateAll);
        return $"{Count(added.Count, "permission")} added to {role.Name}";
    }

    /// <summary>
    /// System health's warnings about who approves grants, on a live server: a break-glass grant in the last day (critical, so
    /// IT on-call is told), and fewer than two people who may approve grants once the hospital has had two.
    /// </summary>
    public static async Task<IReadOnlyList<HealthWarning>> WarningsAsync(HospitalDbContext db, PlatformOptions options, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowsSimulators)
        {
            return [];
        }

        var since = now - BreakGlassShown;
        var breakGlass = await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Category == AuditCategories.Admin && e.Action == AloneAction && e.Severity == AuditSeverity.Critical && e.OccurredAt >= since)
            .CountAsync(ct);
        var latched = await db.Set<AuditEvent>().AsNoTracking().AnyAsync(e => e.Category == AuditCategories.Admin && e.Action == LatchAction, ct);
        return Warnings(breakGlass, latched, latched ? Approvers(await HoldingsAsync(db, ct), []).Count : 0);
    }

    /// <summary>
    /// The warnings <see cref="WarningsAsync"/> gives from what it found: <paramref name="breakGlass"/>, the break-glass grants
    /// in the last day; <paramref name="latched"/>, whether the hospital has ever had two people who may approve grants;
    /// <paramref name="approvers"/>, how many it has now.
    /// </summary>
    internal static IReadOnlyList<HealthWarning> Warnings(int breakGlass, bool latched, int approvers)
    {
        List<HealthWarning> warnings = [];
        if (breakGlass > 0)
        {
            warnings.Add(new HealthWarning(AloneAction, "critical",
                $"A Super Administrator gave access with nobody else to approve it {breakGlass} time(s) in the last day (break-glass). Check each in the audit trail ({AloneAction})."));
        }

        if (latched && approvers < 2)
        {
            warnings.Add(new HealthWarning(OneApproverCode, "warning",
                "Only one person may approve the access administrators give, so no grant can wait for a second one: give a second administrator the permission to approve grants (admin.grants.approve)."));
        }

        return warnings;
    }

    /// <summary>The administrative permissions these roles carry: only someone who holds them may give or approve them.</summary>
    public async Task<List<string>> AdministrativeAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken ct) =>
        roleIds.Count == 0
            ? []
            : (await db.Set<RolePermission>().Where(rp => roleIds.Contains(rp.RoleId)).Select(rp => rp.Permission).Distinct().ToListAsync(ct))
                .Where(PlatformPermissions.IsAdministrative).ToList();

    /// <summary>
    /// The administrative permissions an account holds through its roles, active or not: only someone who holds them all may
    /// change the account (<see cref="AccessAdminEndpoints.EnsureMayManageAsync"/>), or approve a change to it.
    /// </summary>
    public async Task<List<string>> AdministrativeOfUserAsync(Guid userId, CancellationToken ct) =>
        (await (from ur in db.Set<UserRole>()
                join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                where ur.UserId == userId
                select rp.Permission).Distinct().ToListAsync(ct))
            .Where(PlatformPermissions.IsAdministrative).ToList();

    /// <summary>The administrative permissions (all-access included) the active members of staff hold, through their roles.</summary>
    private static async Task<List<(Guid User, string Permission)>> HoldingsAsync(HospitalDbContext db, CancellationToken ct) =>
        [.. (await (from u in db.Set<User>()
                    where u.IsActive && u.Type == UserType.Staff
                    join ur in db.Set<UserRole>() on u.Id equals ur.UserId
                    join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                    where rp.Permission == PermissionRegistry.Wildcard || rp.Permission.StartsWith("admin.")
                    select new { u.Id, rp.Permission }).Distinct().ToListAsync(ct))
            .Select(h => (h.Id, h.Permission))];

    /// <summary>
    /// Who may approve a grant that gives <paramref name="required"/>: whoever holds all-access, or holds the permission to
    /// approve grants and every one of those administrative permissions.
    /// </summary>
    internal static List<Guid> Approvers(IEnumerable<(Guid User, string Permission)> holdings, IReadOnlyCollection<string> required) =>
        [.. holdings.GroupBy(h => h.User)
            .Where(g =>
            {
                var held = g.Select(h => h.Permission).ToHashSet(StringComparer.Ordinal);
                return held.Contains(PermissionRegistry.Wildcard) || (held.Contains(PlatformPermissions.GrantsApprove) && required.All(held.Contains));
            })
            .Select(g => g.Key)];

    /// <summary>Whether the hospital has ever had two people who may approve grants; records it the first time it has.</summary>
    private async Task<bool> LatchedAsync(int approvers, CancellationToken ct)
    {
        if (latch.IsSet)
        {
            return true;
        }

        if (await db.Set<AuditEvent>().AnyAsync(e => e.Category == AuditCategories.Admin && e.Action == LatchAction, ct))
        {
            latch.IsSet = true;
            return true;
        }

        if (approvers < 2)
        {
            return false;
        }

        audit.Add(new AuditEntry(AuditCategories.Admin, LatchAction,
            "Two people may now approve the access administrators give: from now on each grant waits for one of them.")
        {
            Severity = AuditSeverity.Warning,
        });
        latch.IsSet = true;
        return true;
    }

    private async Task<ApprovalRequest?> PendingAsync(string kind, string subjectType, Guid subjectId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == kind && r.SubjectType == subjectType && r.SubjectId == subjectId && r.Status == ApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefaultAsync(ct);
    }

    private string Describe(string permission) =>
        registry.All.FirstOrDefault(d => d.Name == permission) is { } definition ? $"{permission} ({definition.Description})" : permission;

    private static string Count(int count, string one, string? many = null) =>
        $"{count} {(count == 1 ? one : many ?? one + "s")}";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>Roles and facilities given to a user, or a user reactivated: one administrator asks, another approves.</summary>
internal sealed class UserGrantApproval(GrantApprovals grants) : IApprovalAction
{
    public string Kind => GrantApprovals.UserGrantKind;

    public string Title => "Access given to a user";

    public string ApprovePermission => PlatformPermissions.GrantsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => grants.ApplyUserGrantAsync(request, ct);
}

/// <summary>Permissions added to a role: one administrator asks, another approves.</summary>
internal sealed class RoleGrantApproval(GrantApprovals grants) : IApprovalAction
{
    public string Kind => GrantApprovals.RoleGrantKind;

    public string Title => "Permissions added to a role";

    public string ApprovePermission => PlatformPermissions.GrantsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => grants.ApplyRoleGrantAsync(request, ct);
}
