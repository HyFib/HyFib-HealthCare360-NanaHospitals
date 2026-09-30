using FluentValidation;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

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
    uint Version);

public sealed record CreateUserRequest(
    string UserName,
    string DisplayName,
    string? Email,
    string? Phone,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid>? FacilityIds,
    Guid? DefaultFacilityId,
    Guid? PractitionerId,
    string? TemporaryPassword);

public sealed record UpdateUserRequest(
    string DisplayName,
    string? Email,
    string? Phone,
    IReadOnlyList<Guid> RoleIds,
    IReadOnlyList<Guid>? FacilityIds,
    Guid? DefaultFacilityId,
    Guid? PractitionerId,
    bool IsActive,
    uint Version);

public sealed record TemporaryPasswordResponse(Guid UserId, string TemporaryPassword);

public sealed record RoleListItem(Guid Id, string Name, string? Description, bool IsSystem, int PermissionCount, int UserCount);

public sealed record RoleDetail(Guid Id, string Name, string? Description, bool IsSystem, IReadOnlyList<string> Permissions, uint Version);

public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions, uint? Version);

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
    }
}

public sealed class SaveRoleRequestValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(64).Matches("^[A-Za-z][A-Za-z0-9 _-]*$");
        RuleFor(x => x.Description).MaximumLength(256);
        RuleFor(x => x.Permissions).NotNull();
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
        roles.MapPut("/{id:guid}", UpdateRole).RequirePermission(PlatformPermissions.RolesManage).WithValidation<SaveRoleRequest>();
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
            users = users.Where(u => EF.Functions.ILike(u.UserName, like) || EF.Functions.ILike(u.DisplayName, like) || (u.Phone != null && u.Phone.Contains(term)));
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

    private static async Task<IResult> GetUser(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var user = await db.Set<User>().Include(u => u.Roles).Include(u => u.Facilities).AsSplitQuery().FirstOrDefaultAsync(u => u.Id == id, ct);
        return user is null ? Error.NotFound(nameof(User), id).ToProblem() : TypedResults.Ok(ToDetail(user));
    }

    private static async Task<IResult> CreateUser(
        CreateUserRequest request,
        HospitalDbContext db,
        PasswordPolicy policy,
        PasswordHashing hashing,
        IAuditLogger audit,
        ICurrentUser current,
        IPermissionService permissions,
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
        var errors = policy.Validate(password, request.UserName, request.DisplayName);
        if (errors.Count > 0)
        {
            return Error.Validation("user.weak_password", errors[0], new Dictionary<string, string[]> { ["temporaryPassword"] = errors.ToArray() }).ToProblem();
        }

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
            PractitionerId = request.PractitionerId,
            DefaultFacilityId = request.DefaultFacilityId,
        };
        user.Roles.AddRange(request.RoleIds.Distinct().Select(r => new UserRole { UserId = user.Id, RoleId = r }));
        user.Facilities.AddRange((request.FacilityIds ?? []).Distinct().Select(f => new UserFacility { UserId = user.Id, FacilityId = f }));

        db.Add(user);
        audit.Add(new AuditEntry(AuditCategories.Admin, "user.created", $"Created user {user.UserName}") { EntityType = nameof(User), EntityId = user.Id.ToString() });
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/admin/users/{user.Id}", new TemporaryPasswordResponse(user.Id, password));
    }

    private static async Task<IResult> UpdateUser(
        Guid id,
        UpdateUserRequest request,
        HospitalDbContext db,
        IAuditLogger audit,
        ICurrentUser current,
        IPermissionService permissions,
        ISessionValidator sessions,
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

        var wasActive = user.IsActive;
        user.IsActive = request.IsActive;

        var roleIds = request.RoleIds.Distinct().ToHashSet();
        user.Roles.RemoveAll(r => !roleIds.Contains(r.RoleId));
        user.Roles.AddRange(roleIds.Where(r => user.Roles.All(x => x.RoleId != r)).Select(r => new UserRole { UserId = user.Id, RoleId = r }));

        var facilityIds = (request.FacilityIds ?? []).Distinct().ToHashSet();
        user.Facilities.RemoveAll(f => !facilityIds.Contains(f.FacilityId));
        user.Facilities.AddRange(facilityIds.Where(f => user.Facilities.All(x => x.FacilityId != f)).Select(f => new UserFacility { UserId = user.Id, FacilityId = f }));

        if (wasActive && !user.IsActive)
        {
            await RevokeAllSessionsAsync(db, sessions, user.Id, "user_deactivated", time.GetUtcNow(), ct);
        }

        audit.Add(new AuditEntry(AuditCategories.Admin, "user.updated", $"Updated user {user.UserName}") { EntityType = nameof(User), EntityId = user.Id.ToString() });
        await db.SaveChangesAsync(ct);
        permissions.Invalidate(user.Id);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ResetPassword(
        Guid id,
        HospitalDbContext db,
        PasswordHashing hashing,
        IAuditLogger audit,
        ISessionValidator sessions,
        ICurrentUser current,
        IPermissionService permissions,
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

        var password = GenerateTemporaryPassword();
        user.PasswordHash = hashing.Hash(password);
        user.MustChangePassword = true;
        user.FailedLoginCount = 0;
        user.LockoutEndsAt = null;
        await RevokeAllSessionsAsync(db, sessions, user.Id, "password_reset", time.GetUtcNow(), ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "user.password_reset", $"Password reset for {user.UserName}")
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new TemporaryPasswordResponse(user.Id, password));
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

        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaLastTimeStep = null;
        // A lost or stolen authenticator: whoever holds it keeps no session either.
        await RevokeAllSessionsAsync(db, sessions, user.Id, "mfa_reset", time.GetUtcNow(), ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "user.mfa_reset", $"Two-step verification reset for {user.UserName}")
        {
            EntityType = nameof(User),
            EntityId = user.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
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

        await RevokeAllSessionsAsync(db, sessions, id, "revoked_by_admin", time.GetUtcNow(), ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "user.sessions_revoked", "All sessions revoked") { EntityType = nameof(User), EntityId = id.ToString() });
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> ListRoles(HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await db.Set<Role>()
            .OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name)
            .Select(r => new RoleListItem(r.Id, r.Name, r.Description, r.IsSystem, r.Permissions.Count, db.Set<UserRole>().Count(ur => ur.RoleId == r.Id)))
            .ToListAsync(ct));

    private static async Task<IResult> GetRole(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var role = await db.Set<Role>().Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Id == id, ct);
        return role is null
            ? Error.NotFound(nameof(Role), id).ToProblem()
            : TypedResults.Ok(new RoleDetail(role.Id, role.Name, role.Description, role.IsSystem, role.Permissions.Select(p => p.Permission).OrderBy(p => p).ToList(), role.Version));
    }

    private static async Task<IResult> CreateRole(
        SaveRoleRequest request,
        HospitalDbContext db,
        PermissionRegistry registry,
        IAuditLogger audit,
        ICurrentUser current,
        IPermissionService permissions,
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

        var role = new Role { Name = request.Name.Trim(), Description = request.Description?.Trim() };
        role.Permissions.AddRange(request.Permissions.Distinct().Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        db.Add(role);
        audit.Add(new AuditEntry(AuditCategories.Admin, "role.created", $"Created role {role.Name}") { EntityType = nameof(Role), EntityId = role.Id.ToString(), Details = request.Permissions });
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/admin/roles/{role.Id}", new RoleDetail(role.Id, role.Name, role.Description, false, request.Permissions.ToList(), role.Version));
    }

    private static async Task<IResult> UpdateRole(
        Guid id,
        SaveRoleRequest request,
        HospitalDbContext db,
        PermissionRegistry registry,
        IPermissionService permissions,
        IAuditLogger audit,
        ICurrentUser current,
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
        var removed = role.Permissions.Where(p => !wanted.Contains(p.Permission)).Select(p => p.Permission).ToList();
        var added = wanted.Where(p => role.Permissions.All(x => x.Permission != p)).ToList();
        role.Permissions.RemoveAll(p => !wanted.Contains(p.Permission));
        role.Permissions.AddRange(added.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        role.UpdatedAt = DateTimeOffset.UtcNow; // force a version bump even when only child rows change

        audit.Add(new AuditEntry(AuditCategories.Admin, "role.permissions_changed", $"Updated role {role.Name}")
        {
            EntityType = nameof(Role),
            EntityId = role.Id.ToString(),
            Details = new { added, removed },
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        permissions.InvalidateAll();
        return TypedResults.NoContent();
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
    private static async Task<Result> ValidateRoleAssignmentAsync(IReadOnlyList<Guid> roleIds, HospitalDbContext db, ICurrentUser current, IPermissionService permissions, CancellationToken ct)
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
    private static async Task<Result> EnsureMayGrantAsync(IEnumerable<string> permissionsToGrant, ICurrentUser current, IPermissionService permissions, CancellationToken ct)
    {
        var held = await CallerPermissionsAsync(current, permissions, ct);
        if (held.Contains(PermissionRegistry.Wildcard))
        {
            return Result.Success();
        }

        var missing = permissionsToGrant.Where(IsAdministrative).Where(p => !held.Contains(p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return missing.Count == 0
            ? Result.Success()
            : Error.Forbidden("role.escalation", $"Only an administrator who holds these permissions can give them: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// Changing another account (its roles, status, password, two-step verification or sessions) needs every
    /// administrative permission that account holds. Otherwise a password reset would hand the administrator control
    /// they were never given: a hospital administrator could take over a Super Administrator.
    /// </summary>
    private static async Task<Result> EnsureMayManageAsync(Guid targetId, HospitalDbContext db, ICurrentUser current, IPermissionService permissions, CancellationToken ct)
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
        return target.Where(IsAdministrative).All(held.Contains)
            ? Result.Success()
            : Error.Forbidden("user.more_privileged", "This account has administrative permissions you do not hold, so only an administrator who holds them can change it.");
    }

    /// <summary>
    /// The permissions that control accounts, roles, credentials and the server itself: all-access and the Administration
    /// ("admin.") permissions. Clinical and business permissions are handed out by user administrators every day.
    /// </summary>
    private static bool IsAdministrative(string permission) =>
        permission == PermissionRegistry.Wildcard || permission.StartsWith("admin.", StringComparison.Ordinal);

    private static async Task<IReadOnlySet<string>> CallerPermissionsAsync(ICurrentUser current, IPermissionService permissions, CancellationToken ct) =>
        current.UserId is { } callerId ? await permissions.GetPermissionsAsync(callerId, ct) : new HashSet<string>(StringComparer.Ordinal);

    private static async Task RevokeAllSessionsAsync(HospitalDbContext db, ISessionValidator validator, Guid userId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.Set<UserSession>().Where(s => s.UserId == userId && s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in active)
        {
            session.RevokedAt = now;
            session.RevokedReason = reason;
            validator.Invalidate(session.Id);
        }
    }

    private static UserDetail ToDetail(User user) => new(
        user.Id, user.UserName, user.DisplayName, user.Email, user.Phone, user.IsActive, user.MfaEnabled, user.MustChangePassword,
        user.LastLoginAt, user.LockoutEndsAt, user.PractitionerId, user.DefaultFacilityId,
        user.Roles.Select(r => r.RoleId).ToList(), user.Facilities.Select(f => f.FacilityId).ToList(), user.Version);

    internal static string GenerateTemporaryPassword() =>
        $"{SecretTokens.NewReadableCode(4)}-{SecretTokens.NewReadableCode(4)}-{SecretTokens.NewReadableCode(4)}";
}
