using FluentValidation;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Identity;

public sealed record UserListItem(Guid Id, string UserName, string DisplayName, string? Email, string? Phone, bool IsActive, bool IsLocked, bool MfaEnabled, DateTimeOffset? LastLoginAt, IReadOnlyList<string> Roles);

public sealed record UserDetail(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    string? Phone,
    bool IsActive,
    bool MfaEnabled,
    bool MustChangePassword,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? LockoutEndsAt,
    Guid? PractitionerId,
    Guid? DefaultFacilityId,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid> FacilityIds,
    uint Version,
    PendingUserGrant? PendingGrant = null);

/// <param name="Reason">Why the user needs the roles and facilities, for the administrator who approves them (#155).</param>
public sealed record CreateUserRequest(
    string UserName,
    string DisplayName,
    string? Email,
    string? Phone,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid>? FacilityIds,
    Guid? DefaultFacilityId,
    Guid? PractitionerId,
    string? TemporaryPassword,
    string? Reason = null);

public sealed record UpdateUserRequest(
    string DisplayName,
    string? Email,
    string? Phone,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid>? FacilityIds,
    Guid? DefaultFacilityId,
    Guid? PractitionerId,
    bool IsActive,
    uint Version,
    string? Reason = null);

/// <param name="ExpiresAt">When the temporary password stops working, unless the user has replaced it by then.</param>
/// <param name="ApprovalId">The request for the user's roles and facilities, when they wait for a second administrator.</param>
public sealed record TemporaryPasswordResponse(Guid UserId, string TemporaryPassword, DateTimeOffset ExpiresAt, Guid? ApprovalId = null);

public sealed record RoleListItem(Guid Id, string Name, string? Description, bool IsSystem, int PermissionCount, int UserCount);

public sealed record RoleDetail(
    Guid Id, string Name, string? Description, bool IsSystem, IReadOnlyList<string> Permissions, uint Version, PendingRoleGrant? PendingGrant = null);

/// <param name="Reason">Why the role needs the permissions added, for the administrator who approves them (#155).</param>
public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions, uint? Version, string? Reason = null);

public sealed record PermissionItem(string Name, string Description);

public sealed record PermissionGroup(string Group, IReadOnlyList<PermissionItem> Permissions);

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.UserName).NotEmpty().Matches("^[a-zA-Z0-9._-]{3,64}$")
            .WithMessage("Use 3–64 letters, digits, dots, dashes or underscores.");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.RoleIds).NotEmpty().WithMessage("Assign at least one role.");
        RuleFor(x => x.TemporaryPassword).MaximumLength(PasswordPolicy.MaxLength).WithMessage(IdentityRules.PasswordTooLongMessage);
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.RoleIds).NotEmpty().WithMessage("Assign at least one role.");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class SaveRoleRequestValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(64).Matches("^[A-Za-z][A-Za-z0-9 _-]*$");
        RuleFor(x => x.Description).MaximumLength(256);
        RuleFor(x => x.Permissions).NotNull();
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

/// <summary>User, role and permission administration (SEC-03). Every change is audited and invalidates permission caches.</summary>
internal static class AccessAdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/v1/admin/users").WithTags("Administration");

        users.MapGet("/", ListUsers).RequirePermission(PlatformPermissions.UsersRead);
        users.MapGet("/{id:guid}", GetUser).RequirePermission(PlatformPermissions.UsersRead);
        users.MapPost("/", CreateUser).RequirePermission(PlatformPermissions.UsersManage).WithValidation<CreateUserRequest>();
        users.MapPut("/{id:guid}", UpdateUser).RequirePermission(PlatformPermissions.UsersManage).WithValidation<UpdateUserRequest>();
        users.MapPost("/{id:guid}/reset-password", ResetPassword).RequirePermission(PlatformPermissions.UsersManage);
        users.MapPost("/{id:guid}/unlock", UnlockUser).RequirePermission(PlatformPermissions.UsersManage);
        users.MapPost("/{id:guid}/reset-mfa", ResetMfa).RequirePermission(PlatformPermissions.UsersManage);
        users.MapPost("/{id:guid}/revoke-sessions", RevokeSessions).RequirePermission(PlatformPermissions.UsersManage);

        var roles = app.MapGroup("/api/v1/admin/roles").WithTags("Administration");
        roles.MapGet("/", ListRoles).RequirePermission(PlatformPermissions.UsersRead);
        roles.MapGet("/{id:guid}", GetRole).RequirePermission(PlatformPermissions.UsersRead);
        roles.MapPost("/", CreateRole).RequirePermission(PlatformPermissions.RolesManage).WithValidation<SaveRoleRequest>();
        roles.MapPut("/{id:guid}", UpdateRole).RequirePermission(PlatformPermissions.RolesManage).WithValidation<SaveRoleRequest>().RequireVersion();
        roles.MapDelete("/{id:guid}", DeleteRole).RequirePermission(PlatformPermissions.RolesManage);

        app.MapGet("/api/v1/admin/permissions", (PermissionRegistry registry) =>
                TypedResults.Ok(registry.All
                    .GroupBy(p => p.Group)
                    .OrderBy(g => g.Key)
                    .Select(g => new PermissionGroup(g.Key, g.OrderBy(p => p.Name).Select(p => new PermissionItem(p.Name, p.Description)).ToList()))
                    .ToList()))
            .WithTags("Administration")
            .RequirePermission(PlatformPermissions.UsersRead);
    }

    private static async Task<IResult> ListUsers([AsParameters] PageQuery query, bool? active, Guid? roleId, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var now = time.GetUtcNow();
        var users = db.Set<User>().Where(u => u.Type == UserType.Staff);
        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            users = users.Where(u => EF.Functions.ILike(u.UserName, like, QueryExtensions.LikeEscape) || EF.Functions.ILike(u.DisplayName, like, QueryExtensions.LikeEscape) || (u.Phone != null && u.Phone.Contains(term)));
        }

        if (active is { } isActive)
        {
            users = users.Where(u => u.IsActive == isActive);
        }

        if (roleId is { } rid)
        {
            users = users.Where(u => u.Roles.Any(r => r.RoleId == rid));
        }

        var result = await users
            .OrderBy(u => u.DisplayName)
            .Select(u => new UserListItem(
                u.Id, u.UserName, u.DisplayName, u.Email, u.Phone, u.IsActive, u.LockoutEndsAt != null && u.LockoutEndsAt > now, u.MfaEnabled, u.LastLoginAt,
                u.Roles.Select(r => r.Role!.Name).ToList()))
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetUser(Guid id, HospitalDbContext db, GrantApprovals grants, CancellationToken ct)
    {
        var user = await db.Set<User>().Include(u => u.Roles).Include(u => u.Facilities).AsSplitQuery().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null
            ? Error.NotFound(nameof(User), id).ToProblem()
            : TypedResults.Ok(ToDetail(user) with { PendingGrant = await grants.PendingForUserAsync(user.Id, ct) });
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest request,
        HospitalDbContext db,
        PasswordPolicy policy,
        PasswordHashing hashing,
        IAuditLogger audit,
        ICurrentUser current,
        IPermissionService permissions,
        IOptions<PlatformOptions> options,
        GrantApprovals grants,
        TimeProvider time,
        CancellationToken ct)
    {
        var normalized = User.Normalize(request.UserName);
        if (await db.Set<User>().IgnoreQueryFilters().AnyAsync(u => u.NormalizedUserName == normalized, ct))
        {
            return Error.Conflict("user.duplicate", "That username is already taken.").ToProblem();
        }

        var roleCheck = await ValidateRoleAssignmentAsync(request.RoleIds, db, current, permissions, ct);
        if (roleCheck.IsFailure)
        {
            return roleCheck.Error!.ToProblem();
        }

        var password = string.IsNullOrWhiteSpace(request.TemporaryPassword) ? GenerateTemporaryPassword() : request.TemporaryPassword;
        var errors = policy.Validate(password, request.UserName, request.DisplayName, await AuthService.FacilityNamesAsync(db, ct));
        if (errors.Count > 0)
        {
            return Error.Validation("user.weak_password", errors[0], new Dictionary<string, string[]> { ["temporaryPassword"] = errors.ToArray() }).ToProblem();
        }

        // A new user's roles and facilities are access given: they wait for a second person who may approve grants (#155).
        var decision = await grants.DecideAsync(self: false, await grants.AdministrativeAsync([.. request.RoleIds.Distinct()], ct), ct);
        if (decision.IsFailure)
        {
            return decision.Error!.ToProblem();
        }

        var roleIds = request.RoleIds.Distinct().ToList();
        var facilityIds = (request.FacilityIds ?? []).Distinct().ToList();
        var ask = decision.Value == GrantDecision.Ask;
        var user = new User
        {
            UserName = request.UserName.Trim(),
            NormalizedUserName = normalized,
            DisplayName = request.DisplayName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            Phone = Phone.Normalize(request.Phone),
            Type = UserType.Staff,
            PasswordHash = hashing.Hash(password),
            MustChangePassword = true,
            TemporaryPasswordExpiresAt = TemporaryPasswordExpiry(options, time),
            PractitionerId = request.PractitionerId,
            DefaultFacilityId = request.DefaultFacilityId,
        };
        if (!ask)
        {
            user.Roles.AddRange(roleIds.Select(r => new UserRole { UserId = user.Id, RoleId = r }));
            user.Facilities.AddRange(facilityIds.Select(f => new UserFacility { UserId = user.Id, FacilityId = f }));
        }

        db.Add(user);
        var names = await GrantNamesAsync(db, roleIds, facilityIds, ct);
        audit.Add(new AuditEntry(AuditCategories.Admin, "user.created", $"Created user {user.UserName}")
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Details = new
            {
                roles = names.Roles(user.Roles.Select(r => r.RoleId)),
                facilities = names.Facilities(user.Facilities.Select(f => f.FacilityId)),
                asked = ask ? new { roles = names.Roles(roleIds), facilities = names.Facilities(facilityIds) } : null,
            },
        });
        if (!ask)
        {
            grants.AuditGivenAlone(decision.Value, nameof(User), user.Id, $"{user.UserName}, created with the roles {string.Join(", ", names.Roles(roleIds).Select(r => r.Name))}");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        Guid? approvalId = null;
        if (ask)
        {
            var asked = await grants.AskForUserAsync(user, roleIds, facilityIds, activate: false, request.Reason, ct);
            if (asked.IsFailure)
            {
                return asked.Error!.ToProblem();
            }

            approvalId = asked.Value.Id;
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/v1/admin/users/{user.Id}", new TemporaryPasswordResponse(user.Id, password, user.TemporaryPasswordExpiresAt!.Value, approvalId));
    }

    private static async Task<IResult> UpdateUser(
        Guid id,
        UpdateUserRequest request,
        HospitalDbContext db,
        IAuditLogger audit,
        ICurrentUser current,
        IPermissionService permissions,
        ISessionValidator sessions,
        GrantApprovals grants,
        TimeProvider time,
        CancellationToken ct)
    {
        var user = await db.Set<User>().Include(u => u.Roles).Include(u => u.Facilities).AsSplitQuery().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Error.NotFound(nameof(User), id).ToProblem();
        }

        if (user.Id == current.UserId && !request.IsActive)
        {
            return Error.Rule("user.self_deactivate", "You cannot deactivate your own account.").ToProblem();
        }

        var mayManage = await EnsureMayManageAsync(user.Id, db, current, permissions, ct);
        if (mayManage.IsFailure)
        {
            return mayManage.Error!.ToProblem();
        }

        var roleCheck = await ValidateRoleAssignmentAsync(request.RoleIds, db, current, permissions, ct);
        if (roleCheck.IsFailure)
        {
            return roleCheck.Error!.ToProblem();
        }

        db.SetExpectedVersion(user, request.Version);
        user.DisplayName = request.DisplayName.Trim();
        user.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        user.Phone = Phone.Normalize(request.Phone);
        user.PractitionerId = request.PractitionerId;
        user.DefaultFacilityId = request.DefaultFacilityId;

        // What narrows access applies at once: roles and facilities taken away, the account deactivated.
        var wasActive = user.IsActive;
        user.IsActive = wasActive && request.IsActive;
        var rolesBefore = user.Roles.Select(r => r.RoleId).ToList();
        var roleIds = request.RoleIds.Distinct().ToHashSet();
        user.Roles.RemoveAll(r => !roleIds.Contains(r.RoleId));
        var facilitiesBefore = user.Facilities.Select(f => f.FacilityId).ToList();
        var facilityIds = (request.FacilityIds ?? []).Distinct().ToHashSet();
        user.Facilities.RemoveAll(f => !facilityIds.Contains(f.FacilityId));

        // What widens it (roles and facilities added, the account reactivated) waits for a second person who may approve
        // grants (#155). What already waits is not asked for again.
        var pending = await grants.PendingForUserAsync(user.Id, ct);
        var addRoles = roleIds.Where(r => !rolesBefore.Contains(r) && pending?.RoleIds.Contains(r) != true).ToList();
        var addFacilities = facilityIds.Where(f => !facilitiesBefore.Contains(f) && pending?.FacilityIds.Contains(f) != true).ToList();
        var activate = !wasActive && request.IsActive && pending?.Activate != true;
        var decision = GrantDecision.Ask;
        var widens = addRoles.Count > 0 || addFacilities.Count > 0 || activate;
        if (widens)
        {
            // Reactivating gives back what the account's roles hold.
            var administrative = await grants.AdministrativeAsync(activate ? [.. addRoles, .. user.Roles.Select(r => r.RoleId)] : addRoles, ct);
            var decided = await grants.DecideAsync(self: user.Id == current.UserId, administrative, ct);
            if (decided.IsFailure)
            {
                return decided.Error!.ToProblem();
            }

            decision = decided.Value;
        }

        var giveNow = widens && decision != GrantDecision.Ask;
        if (giveNow)
        {
            user.Roles.AddRange(addRoles.Select(r => new UserRole { UserId = user.Id, RoleId = r }));
            user.Facilities.AddRange(addFacilities.Select(f => new UserFacility { UserId = user.Id, FacilityId = f }));
            user.IsActive = user.IsActive || activate;
        }

        var ended = wasActive && !user.IsActive
            ? await RevokeAllSessionsAsync(db, user.Id, "user_deactivated", time.GetUtcNow(), ct)
            : [];

        // Which roles and facilities the change gave, asked for and took away, by name, in the transaction that makes it.
        var rolesNow = user.Roles.Select(r => r.RoleId).ToList();
        var facilitiesNow = user.Facilities.Select(f => f.FacilityId).ToList();
        var names = await GrantNamesAsync(db, rolesBefore.Concat(roleIds), facilitiesBefore.Concat(facilityIds), ct);
        var asking = widens && !giveNow;
        audit.Add(new AuditEntry(AuditCategories.Admin, "user.updated", $"Updated user {user.UserName}")
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Details = new
            {
                roles = new { granted = names.Roles(rolesNow.Except(rolesBefore)), withdrawn = names.Roles(rolesBefore.Except(rolesNow)) },
                facilities = new { granted = names.Facilities(facilitiesNow.Except(facilitiesBefore)), withdrawn = names.Facilities(facilitiesBefore.Except(facilitiesNow)) },
                asked = asking ? new { roles = names.Roles(addRoles), facilities = names.Facilities(addFacilities), reactivate = activate } : null,
            },
        });
        if (giveNow)
        {
            grants.AuditGivenAlone(decision, nameof(User), user.Id,
                $"{user.UserName}: roles {string.Join(", ", names.Roles(addRoles).Select(r => r.Name))}; {addFacilities.Count} facilities{(activate ? "; reactivated" : string.Empty)}");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        ApprovalRequestDto? asked = null;
        if (asking)
        {
            var requested = await grants.AskForUserAsync(user, addRoles, addFacilities, activate, request.Reason, ct);
            if (requested.IsFailure)
            {
                return requested.Error!.ToProblem();
            }

            asked = requested.Value;
        }

        await transaction.CommitAsync(ct);
        Invalidate(sessions, ended);
        permissions.Invalidate(user.Id);
        return asked is null ? TypedResults.NoContent() : TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked);
    }

    private static async Task<IResult> ResetPassword(
        Guid id,
        HospitalDbContext db,
        PasswordHashing hashing,
        IAuditLogger audit,
        ISessionValidator sessions,
        ICurrentUser current,
        IPermissionService permissions,
        INotificationService notifications,
        IOptions<PlatformOptions> options,
        TimeProvider time,
        CancellationToken ct)
    {
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == id && u.Type == UserType.Staff, ct);
        if (user is null)
        {
            return Error.NotFound(nameof(User), id).ToProblem();
        }

        var mayManage = await EnsureMayManageAsync(user.Id, db, current, permissions, ct);
        if (mayManage.IsFailure)
        {
            return mayManage.Error!.ToProblem();
        }

        var now = time.GetUtcNow();
        var password = GenerateTemporaryPassword();
        user.PasswordHash = hashing.Hash(password);
        user.MustChangePassword = true;
        user.TemporaryPasswordExpiresAt = TemporaryPasswordExpiry(options, time);
        user.PasswordBreachedAt = null;
        user.FailedLoginCount = 0;
        user.LastFailedLoginAt = null;
        user.LockoutEndsAt = null;

        // The lock-screen PIN goes too: whoever may know the old password may know it. Until the account's holder sets a
        // new one, their screen unlocks with the password. So do the devices set up to unlock it, which were set up with
        // the old password: a lost laptop among them unlocks nothing.
        user.PinHash = null;
        db.RemoveRange(await db.Set<WebAuthnCredential>().Where(c => c.UserId == user.Id).ToListAsync(ct));
        var ended = await RevokeAllSessionsAsync(db, user.Id, "password_reset", now, ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "user.password_reset", $"Password reset for {user.UserName}")
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        CredentialNotices.QueueEmail(notifications, user, CredentialNotices.PasswordReset, now);
        await db.SaveChangesAsync(ct);
        Invalidate(sessions, ended);
        await CredentialNotices.SendInAppAsync(notifications, user, CredentialNotices.PasswordReset, ct);
        return TypedResults.Ok(new TemporaryPasswordResponse(user.Id, password, user.TemporaryPasswordExpiresAt.Value));
    }

    private static async Task<IResult> UnlockUser(Guid id, HospitalDbContext db, IAuditLogger audit, ICurrentUser current, IPermissionService permissions, CancellationToken ct)
    {
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Error.NotFound(nameof(User), id).ToProblem();
        }

        var mayManage = await EnsureMayManageAsync(user.Id, db, current, permissions, ct);
        if (mayManage.IsFailure)
        {
            return mayManage.Error!.ToProblem();
        }

        user.FailedLoginCount = 0;
        user.LastFailedLoginAt = null;
        user.LockoutEndsAt = null;
        audit.Add(new AuditEntry(AuditCategories.Security, "user.unlocked", $"Unlocked {user.UserName}") { EntityType = nameof(User), EntityId = user.Id.ToString() });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ResetMfa(
        Guid id,
        HospitalDbContext db,
        IAuditLogger audit,
        ISessionValidator sessions,
        ICurrentUser current,
        IPermissionService permissions,
        INotificationService notifications,
        TimeProvider time,
        CancellationToken ct)
    {
        var user = await db.Set<User>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return Error.NotFound(nameof(User), id).ToProblem();
        }

        var mayManage = await EnsureMayManageAsync(user.Id, db, current, permissions, ct);
        if (mayManage.IsFailure)
        {
            return mayManage.Error!.ToProblem();
        }

        var now = time.GetUtcNow();
        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaLastTimeStep = null;
        // A lost or stolen authenticator: whoever holds it keeps no session either.
        var ended = await RevokeAllSessionsAsync(db, user.Id, "mfa_reset", now, ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "user.mfa_reset", $"Two-step verification reset for {user.UserName}")
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        CredentialNotices.QueueEmail(notifications, user, CredentialNotices.MfaReset, now);
        await db.SaveChangesAsync(ct);
        Invalidate(sessions, ended);
        await CredentialNotices.SendInAppAsync(notifications, user, CredentialNotices.MfaReset, ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RevokeSessions(
        Guid id,
        HospitalDbContext db,
        IAuditLogger audit,
        ISessionValidator sessions,
        ICurrentUser current,
        IPermissionService permissions,
        TimeProvider time,
        CancellationToken ct)
    {
        var mayManage = await EnsureMayManageAsync(id, db, current, permissions, ct);
        if (mayManage.IsFailure)
        {
            return mayManage.Error!.ToProblem();
        }

        var ended = await RevokeAllSessionsAsync(db, id, "revoked_by_admin", time.GetUtcNow(), ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "user.sessions_revoked", "All sessions revoked") { EntityType = nameof(User), EntityId = id.ToString() });
        await db.SaveChangesAsync(ct);
        Invalidate(sessions, ended);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListRoles(HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<Role>()
            .OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name)
            .Select(r => new RoleListItem(r.Id, r.Name, r.Description, r.IsSystem, r.Permissions.Count, db.Set<UserRole>().Count(ur => ur.RoleId == r.Id)))
            .ToListAsync(ct));

    private static async Task<IResult> GetRole(Guid id, HospitalDbContext db, GrantApprovals grants, CancellationToken ct)
    {
        var role = await db.Set<Role>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
        return role is null
            ? Error.NotFound(nameof(Role), id).ToProblem()
            : TypedResults.Ok(new RoleDetail(role.Id, role.Name, role.Description, role.IsSystem, role.Permissions.Select(p => p.Permission).OrderBy(p => p).ToList(), role.Version,
                await grants.PendingForRoleAsync(role.Id, ct)));
    }

    private static async Task<IResult> CreateRole(
        SaveRoleRequest request,
        HospitalDbContext db,
        PermissionRegistry registry,
        IAuditLogger audit,
        ICurrentUser current,
        IPermissionService permissions,
        GrantApprovals grants,
        CancellationToken ct)
    {
        if (await db.Set<Role>().AnyAsync(r => r.Name == request.Name.Trim(), ct))
        {
            return Error.Conflict("role.duplicate", "A role with that name already exists.").ToProblem();
        }

        var invalid = InvalidPermissions(request.Permissions, registry);
        if (invalid is not null)
        {
            return invalid.ToProblem();
        }

        var mayGrant = await EnsureMayGrantAsync(request.Permissions, current, permissions, ct);
        if (mayGrant.IsFailure)
        {
            return mayGrant.Error!.ToProblem();
        }

        // A new role's permissions are access given to whoever will hold it: they wait for a second person (#155).
        var wanted = request.Permissions.Distinct(StringComparer.Ordinal).ToList();
        var decision = GrantDecision.Ask;
        if (wanted.Count > 0)
        {
            var decided = await grants.DecideAsync(self: false, [.. wanted.Where(PlatformPermissions.IsAdministrative)], ct);
            if (decided.IsFailure)
            {
                return decided.Error!.ToProblem();
            }

            decision = decided.Value;
        }

        var ask = wanted.Count > 0 && decision == GrantDecision.Ask;
        var role = new Role { Name = request.Name.Trim(), Description = request.Description?.Trim() };
        if (!ask)
        {
            role.Permissions.AddRange(wanted.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        }

        db.Add(role);
        audit.Add(new AuditEntry(AuditCategories.Admin, "role.created", $"Created role {role.Name}")
        {
            EntityType = nameof(Role),
            EntityId = role.Id.ToString(),
            Details = new { permissions = ask ? [] : wanted, asked = ask ? wanted : [] },
        });
        if (!ask && wanted.Count > 0)
        {
            grants.AuditGivenAlone(decision, nameof(Role), role.Id, $"the role {role.Name}, created with {string.Join(", ", wanted)}");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        PendingRoleGrant? pending = null;
        if (ask)
        {
            var asked = await grants.AskForRoleAsync(role, wanted, request.Reason, ct);
            if (asked.IsFailure)
            {
                return asked.Error!.ToProblem();
            }

            pending = new PendingRoleGrant(asked.Value.Id, wanted, asked.Value.RequestedByName, asked.Value.RequestedAt);
        }

        await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/v1/admin/roles/{role.Id}",
            new RoleDetail(role.Id, role.Name, role.Description, false, [.. role.Permissions.Select(p => p.Permission).Order(StringComparer.Ordinal)], role.Version, pending));
    }

    private static async Task<IResult> UpdateRole(
        Guid id,
        SaveRoleRequest request,
        HospitalDbContext db,
        PermissionRegistry registry,
        IPermissionService permissions,
        IAuditLogger audit,
        ICurrentUser current,
        GrantApprovals grants,
        TimeProvider time,
        CancellationToken ct)
    {
        var role = await db.Set<Role>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return Error.NotFound(nameof(Role), id).ToProblem();
        }

        if (role.Name == SystemRoles.SuperAdmin)
        {
            return Error.Rule("role.protected", "The Super Administrator role cannot be changed.").ToProblem();
        }

        // Every portal account holds this role: a staff permission added to it would reach every patient.
        if (role.Name == SystemRoles.Patient)
        {
            return Error.Rule("role.protected", "The Patient role cannot be changed.").ToProblem();
        }

        var invalid = InvalidPermissions(request.Permissions, registry);
        if (invalid is not null)
        {
            return invalid.ToProblem();
        }

        // Adding a permission to a role is granting it to everyone who holds the role, the editor included.
        var mayGrant = await EnsureMayGrantAsync(request.Permissions.Where(p => role.Permissions.All(x => x.Permission != p)), current, permissions, ct);
        if (mayGrant.IsFailure)
        {
            return mayGrant.Error!.ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(role, version);
        }

        if (!role.IsSystem)
        {
            role.Name = request.Name.Trim();
        }

        role.Description = request.Description?.Trim();
        var wanted = request.Permissions.Distinct().ToHashSet(StringComparer.Ordinal);

        // Permissions taken away apply at once; those added wait for a second person who may approve grants (#155), unless
        // they already wait. Adding to a role you hold is giving them to yourself.
        var removed = role.Permissions.Where(p => !wanted.Contains(p.Permission)).Select(p => p.Permission).ToList();
        role.Permissions.RemoveAll(p => !wanted.Contains(p.Permission));
        var pending = await grants.PendingForRoleAsync(role.Id, ct);
        var adding = wanted.Where(p => role.Permissions.All(x => x.Permission != p) && pending?.Permissions.Contains(p) != true).Order(StringComparer.Ordinal).ToList();
        var decision = GrantDecision.Ask;
        if (adding.Count > 0)
        {
            var self = current.UserId is { } me && await db.Set<UserRole>().AnyAsync(ur => ur.UserId == me && ur.RoleId == role.Id, ct);
            var decided = await grants.DecideAsync(self, [.. adding.Where(PlatformPermissions.IsAdministrative)], ct);
            if (decided.IsFailure)
            {
                return decided.Error!.ToProblem();
            }

            decision = decided.Value;
        }

        var giveNow = adding.Count > 0 && decision != GrantDecision.Ask;
        List<string> added = giveNow ? adding : [];
        role.Permissions.AddRange(added.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        role.UpdatedAt = time.GetUtcNow(); // force a version bump even when only child rows change

        audit.Add(new AuditEntry(AuditCategories.Admin, "role.permissions_changed", $"Updated role {role.Name}")
        {
            EntityType = nameof(Role),
            EntityId = role.Id.ToString(),
            Details = new { added, removed, asked = giveNow ? [] : adding },
            Severity = AuditSeverity.Warning,
        });
        if (giveNow)
        {
            grants.AuditGivenAlone(decision, nameof(Role), role.Id, $"the role {role.Name}: {string.Join(", ", added)}");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        ApprovalRequestDto? asked = null;
        if (adding.Count > 0 && !giveNow)
        {
            var requested = await grants.AskForRoleAsync(role, adding, request.Reason, ct);
            if (requested.IsFailure)
            {
                return requested.Error!.ToProblem();
            }

            asked = requested.Value;
        }

        await transaction.CommitAsync(ct);
        permissions.InvalidateAll();
        return asked is null ? TypedResults.NoContent() : TypedResults.Accepted($"/api/v1/approvals/{asked.Id}", asked);
    }

    private static async Task<IResult> DeleteRole(Guid id, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var role = await db.Set<Role>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null)
        {
            return Error.NotFound(nameof(Role), id).ToProblem();
        }

        if (role.IsSystem)
        {
            return Error.Rule("role.protected", "Built-in roles cannot be deleted.").ToProblem();
        }

        if (await db.Set<UserRole>().AnyAsync(ur => ur.RoleId == id, ct))
        {
            return Error.Rule("role.in_use", "Remove this role from all users before deleting it.").ToProblem();
        }

        db.Remove(role);
        audit.Add(new AuditEntry(AuditCategories.Admin, "role.deleted", $"Deleted role {role.Name}") { EntityType = nameof(Role), EntityId = role.Id.ToString() });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static Error? InvalidPermissions(IReadOnlyList<string> permissions, PermissionRegistry registry)
    {
        if (permissions.Contains(PermissionRegistry.Wildcard))
        {
            return Error.Field("permissions", "The all-access permission is reserved for the Super Administrator role.");
        }

        var unknown = permissions.Where(p => !registry.IsDefined(p)).ToList();
        return unknown.Count == 0 ? null : Error.Field("permissions", $"Unknown permission(s): {string.Join(", ", unknown)}");
    }

    /// <summary>
    /// A role that carries administrative permissions can only be given by someone who holds them (and only a Super
    /// Administrator grants that role). Clinical and business roles are the user administrators' everyday work.
    /// </summary>
    internal static async Task<Result> ValidateRoleAssignmentAsync(IReadOnlyList<Guid> roleIds, HospitalDbContext db, ICurrentUser current, IPermissionService permissions, CancellationToken ct)
    {
        var roles = await db.Set<Role>().Where(r => roleIds.Contains(r.Id)).Select(r => r.Name).ToListAsync(ct);
        if (roles.Count != roleIds.Distinct().Count())
        {
            return Error.Field("roleIds", "One or more roles do not exist.");
        }

        if (roles.Contains(SystemRoles.Patient))
        {
            return Error.Field("roleIds", "The Patient role is reserved for portal accounts.");
        }

        var granted = await db.Set<RolePermission>().Where(rp => roleIds.Contains(rp.RoleId)).Select(rp => rp.Permission).Distinct().ToListAsync(ct);
        var mayGrant = await EnsureMayGrantAsync(granted, current, permissions, ct);
        if (mayGrant.IsFailure && roles.Contains(SystemRoles.SuperAdmin))
        {
            return Error.Forbidden("role.escalation", "Only a Super Administrator can grant the Super Administrator role.");
        }

        return mayGrant;
    }

    /// <summary>Nobody gives out an administrative permission they do not hold themselves.</summary>
    internal static async Task<Result> EnsureMayGrantAsync(IEnumerable<string> permissionsToGrant, ICurrentUser current, IPermissionService permissions, CancellationToken ct)
    {
        var held = await CallerPermissionsAsync(current, permissions, ct);
        if (held.Contains(PermissionRegistry.Wildcard))
        {
            return Result.Success();
        }

        var missing = permissionsToGrant.Where(PlatformPermissions.IsAdministrative).Where(p => !held.Contains(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0
            ? Result.Success()
            : Error.Forbidden("role.escalation", $"Only an administrator who holds these permissions can give them: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// Changing another account (its roles, status, password, two-step verification or sessions) needs every
    /// administrative permission that account holds. Otherwise a password reset would hand the administrator control
    /// they were never given: a hospital administrator could take over a Super Administrator.
    /// </summary>
    internal static async Task<Result> EnsureMayManageAsync(Guid targetId, HospitalDbContext db, ICurrentUser current, IPermissionService permissions, CancellationToken ct)
    {
        var held = await CallerPermissionsAsync(current, permissions, ct);
        if (held.Contains(PermissionRegistry.Wildcard))
        {
            return Result.Success();
        }

        // From the account's roles, active or not: a deactivated Super Administrator is still one.
        var target = await (from ur in db.Set<UserRole>()
                            join rp in db.Set<RolePermission>() on ur.RoleId equals rp.RoleId
                            where ur.UserId == targetId
                            select rp.Permission).Distinct().ToListAsync(ct);
        return target.Where(PlatformPermissions.IsAdministrative).All(held.Contains)
            ? Result.Success()
            : Error.Forbidden("user.more_privileged", "This account has administrative permissions you do not hold, so only an administrator who holds them can change it.");
    }

    private static async Task<IReadOnlySet<string>> CallerPermissionsAsync(ICurrentUser current, IPermissionService permissions, CancellationToken ct) =>
        current.UserId is { } callerId ? await permissions.GetPermissionsAsync(callerId, ct) : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Ends the user's open sessions in <paramref name="db"/>, unsaved, and returns them: the caller saves, then tells every
    /// server with <see cref="Invalidate"/>.
    /// </summary>
    private static async Task<List<UserSession>> RevokeAllSessionsAsync(HospitalDbContext db, Guid userId, string reason, DateTimeOffset now, CancellationToken ct)
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
    private static void Invalidate(ISessionValidator validator, IEnumerable<UserSession> ended)
    {
        foreach (var session in ended)
        {
            validator.Invalidate(session.Id);
        }
    }

    /// <summary>The names of these roles and facilities, deleted ones included, for an audit entry to name what it gave or took.</summary>
    private static async Task<GrantNames> GrantNamesAsync(HospitalDbContext db, IEnumerable<Guid> roleIds, IEnumerable<Guid> facilityIds, CancellationToken ct)
    {
        var roles = roleIds.Distinct().ToList();
        var facilities = facilityIds.Distinct().ToList();
        return new GrantNames(
            await db.Set<Role>().IgnoreQueryFilters().Where(r => roles.Contains(r.Id)).Select(r => new { r.Id, r.Name }).ToDictionaryAsync(r => r.Id, r => r.Name, ct),
            await db.Set<Facility>().IgnoreQueryFilters().Where(f => facilities.Contains(f.Id)).Select(f => new { f.Id, f.Name }).ToDictionaryAsync(f => f.Id, f => f.Name, ct));
    }

    private static UserDetail ToDetail(User user) => new(
        user.Id, user.UserName, user.DisplayName, user.Email, user.Phone, user.IsActive, user.MfaEnabled, user.MustChangePassword,
        user.LastLoginAt, user.LockoutEndsAt, user.PractitionerId, user.DefaultFacilityId,
        user.Roles.Select(r => r.RoleId).ToList(), user.Facilities.Select(f => f.FacilityId).ToList(), user.Version);

    /// <summary>When a temporary password given out now stops working (ASVS V6.4.1).</summary>
    private static DateTimeOffset TemporaryPasswordExpiry(IOptions<PlatformOptions> options, TimeProvider time) =>
        time.GetUtcNow() + TimeSpan.FromHours(Math.Max(1, options.Value.Security.TemporaryPasswordHours));

    internal static string GenerateTemporaryPassword() =>
        $"{SecretTokens.NewReadableCode(4)}-{SecretTokens.NewReadableCode(4)}-{SecretTokens.NewReadableCode(4)}";

    /// <summary>Roles and facilities as a user's audit entries list them: by id and name, in order of name.</summary>
    private sealed record GrantNames(IReadOnlyDictionary<Guid, string> RoleNames, IReadOnlyDictionary<Guid, string> FacilityNames)
    {
        public List<Grant> Roles(IEnumerable<Guid> ids) => Named(ids, RoleNames);

        public List<Grant> Facilities(IEnumerable<Guid> ids) => Named(ids, FacilityNames);

        private static List<Grant> Named(IEnumerable<Guid> ids, IReadOnlyDictionary<Guid, string> names) =>
            [.. ids.Distinct().Select(id => new Grant(id, names.GetValueOrDefault(id))).OrderBy(g => g.Name, StringComparer.Ordinal).ThenBy(g => g.Id)];
    }

    private sealed record Grant(Guid Id, string? Name);
}
