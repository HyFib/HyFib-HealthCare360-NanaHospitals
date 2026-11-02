using System.Net;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Access is given by two people, over the real API and database. What widens someone's access (roles or facilities
/// added to a user, a new user's included, permissions added to a role) waits for a second administrator who may approve
/// grants; what narrows it is done at once. Nobody approves their own request, nor access given to themselves, and an
/// administrative permission is approved only by someone who holds it: with nobody else who does, a Super Administrator's
/// grant is a break-glass, given at once, audited as critical and shown on a live server's System health. What is approved
/// works at once for someone already signed in, and gives nothing to someone who left while it waited. The rule's rows the
/// seeded hospital (which always has two approvers) cannot reach are unit-tested (GrantDecisionTests).
/// </summary>
public sealed class GrantApprovalTests(HospitalServer server)
{
    private const string UsersPath = "/api/v1/admin/users";
    private const string RolesPath = "/api/v1/admin/roles";
    private const string TheatresPath = "/api/v1/ot/theatres"; // ot.read
    private const string BloodUnitsPath = "/api/v1/blood-bank/units"; // bloodbank.read, which nurses hold

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_users_roles_wait_for_another_administrator_and_are_given_when_approved()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await RoleIdAsync(hospitalAdmin, SystemRoles.FrontOffice);
        var facility = await FacilityAsync(hospitalAdmin);
        var created = await hospitalAdmin.PostAsync<TemporaryPasswordResponse>(UsersPath, NewUser(frontOffice, facility));
        var approval = created.ApprovalId.ShouldNotBeNull("a new user's roles are access given");

        var waiting = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{created.UserId}");
        waiting.RoleIds.ShouldBeEmpty();
        waiting.FacilityIds.ShouldBeEmpty();
        var pending = waiting.PendingGrant.ShouldNotBeNull();
        (pending.ApprovalId, pending.RoleIds.Single(), pending.FacilityIds.Single(), pending.Activate).ShouldBe((approval, frontOffice, facility, false));

        // Neither the one who asked nor someone who may not approve grants can approve it.
        var request = await hospitalAdmin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval}");
        (request.Kind, request.CanDecide).ShouldBe((GrantApprovals.UserGrantKind, false));
        using (var own = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval}/approve", new ApproveRequest(null, request.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        using (var notPermitted = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval}/approve", new ApproveRequest(null, request.Version)))
        {
            notPermitted.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The Super Administrator approves it: the roles and facilities are given, and nothing waits any more.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var approved = await admin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval}/approve", new ApproveRequest("Joins the front office.", request.Version));
        approved.Status.ShouldBe(ApprovalStatus.Approved);
        var given = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{created.UserId}");
        given.RoleIds.ShouldBe([frontOffice]);
        given.FacilityIds.ShouldBe([facility]);
        given.PendingGrant.ShouldBeNull();
        (await AuditCountAsync("user.access_granted", created.UserId)).ShouldBe(1);
    }

    [Fact]
    public async Task Taking_a_role_away_is_done_at_once_while_adding_one_waits_and_is_asked_for_once()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (frontOffice, nurse, billing) = (
            await RoleIdAsync(hospitalAdmin, SystemRoles.FrontOffice), await RoleIdAsync(hospitalAdmin, SystemRoles.Nurse), await RoleIdAsync(hospitalAdmin, SystemRoles.Billing));
        var created = await server.CreateUserAsync(hospitalAdmin, NewUser(frontOffice, await FacilityAsync(hospitalAdmin)));
        var user = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{created.UserId}");

        // To the wards: the front office role goes at once, the nurse role waits.
        using (var moved = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{user.Id}", Update(user, [nurse])))
        {
            moved.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var moving = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{user.Id}");
        moving.RoleIds.ShouldBeEmpty("the front office role is taken away at once");
        var pending = moving.PendingGrant.ShouldNotBeNull();
        pending.RoleIds.ShouldBe([nurse]);

        // Saved again as the form shows it, nothing is asked for twice; another role while one grant waits is refused.
        using (var again = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{user.Id}", Update(moving, [nurse])))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var still = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{user.Id}");
        using (var more = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{user.Id}", Update(still, [nurse, billing])))
        {
            more.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await more.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.already_requested");
        }

        await server.ApproveGrantAsync(pending.ApprovalId);
        var nurseNow = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{user.Id}");
        nurseNow.RoleIds.ShouldBe([nurse]);
        nurseNow.PendingGrant.ShouldBeNull();
    }

    [Fact]
    public async Task Nobody_approves_access_given_to_themselves()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var accountant = await RoleIdAsync(hospitalAdmin, SystemRoles.Accountant);
        var me = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{(await FindUserAsync(hospitalAdmin, HospitalServer.Users.HospitalAdmin)).Id}");

        // The hospital administrator gives themselves a role: it waits for someone else, here the Super Administrator.
        using (var mine = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{me.Id}", Update(me, [.. me.RoleIds, accountant])))
        {
            mine.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(mine, HttpMethod.Put, $"{UsersPath}/{me.Id}");
            await hospitalAdmin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/withdraw", new WithdrawApprovalRequest(asked.Version));
        }

        // The Super Administrator gives the hospital administrator a role: the hospital administrator, who may approve
        // grants, may not approve this one. It stays waiting for someone else.
        var current = await admin.GetAsync<UserDetail>($"{UsersPath}/{me.Id}");
        using var given = await admin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{me.Id}", Update(current, [.. current.RoleIds, accountant]));
        given.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var request = await ApiClient.ReadAsync<ApprovalRequestDto>(given, HttpMethod.Put, $"{UsersPath}/{me.Id}");
        try
        {
            using var own = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest(null, request.Version));
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await own.Content.ReadAsStringAsync(Ct)).ShouldContain("access.self_grant");
            (await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{request.Id}")).Status.ShouldBe(ApprovalStatus.Pending);
            (await admin.GetAsync<UserDetail>($"{UsersPath}/{me.Id}")).RoleIds.ShouldNotContain(accountant);
        }
        finally
        {
            var left = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{request.Id}");
            await admin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{request.Id}/withdraw", new WithdrawApprovalRequest(left.Version));
        }
    }

    [Fact]
    public async Task An_approver_gives_only_what_they_could_give_themselves()
    {
        // Someone who may approve grants and holds nothing else administrative.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var approvers = await server.CreateRoleAsync(admin, new SaveRoleRequest(
            "IT grant approvers " + Suffix(), "Approves grants", [PlatformPermissions.GrantsApprove], Version: null));
        var approverName = "it-grant-approver-" + Suffix();
        await server.CreateStaffAccountAsync(approverName, approvers.Name);
        var (approver, _) = await server.SignInNewSessionAsync(approverName);
        try
        {
            // Nothing administrative: they approve it, and it is given.
            var nurse = await RoleIdAsync(hospitalAdmin, SystemRoles.Nurse);
            var created = await hospitalAdmin.PostAsync<TemporaryPasswordResponse>(UsersPath, NewUser(nurse, await FacilityAsync(hospitalAdmin)));
            var request = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{created.ApprovalId}");
            (await approver.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{request.Id}/approve", new ApproveRequest(null, request.Version)))
                .Status.ShouldBe(ApprovalStatus.Approved);

            // Reading the audit trail, added to a role (roles are the Super Administrator's to change) or given with one:
            // they do not hold it, so they may not give it.
            var desk = await server.CreateRoleAsync(admin, new SaveRoleRequest(
                "IT audit desk " + Suffix(), "Reads masters", [PlatformPermissions.MastersRead], Version: null));
            using (var added = await admin.SendRawAsync(HttpMethod.Put, $"{RolesPath}/{desk.Id}", new SaveRoleRequest(
                       desk.Name, desk.Description, [PlatformPermissions.MastersRead, PlatformPermissions.AuditRead], desk.Version)))
            {
                await RefusedAsync(approver, admin, await ApiClient.ReadAsync<ApprovalRequestDto>(added, HttpMethod.Put, $"{RolesPath}/{desk.Id}"));
            }

            var auditors = await server.CreateRoleAsync(admin, new SaveRoleRequest(
                "IT auditors " + Suffix(), "Read the audit trail", [PlatformPermissions.AuditRead], Version: null));
            var user = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{created.UserId}");
            using (var given = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{user.Id}", Update(user, [nurse, auditors.Id])))
            {
                await RefusedAsync(approver, hospitalAdmin, await ApiClient.ReadAsync<ApprovalRequestDto>(given, HttpMethod.Put, $"{UsersPath}/{user.Id}"));
            }

            // Nor do they give anything to someone they may not manage: the hospital administrator holds more than they do.
            var accountant = await RoleIdAsync(admin, SystemRoles.Accountant);
            var target = await admin.GetAsync<UserDetail>($"{UsersPath}/{(await FindUserAsync(admin, HospitalServer.Users.HospitalAdmin)).Id}");
            using (var toAdmin = await admin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{target.Id}", Update(target, [.. target.RoleIds, accountant])))
            {
                await RefusedAsync(approver, admin, await ApiClient.ReadAsync<ApprovalRequestDto>(toAdmin, HttpMethod.Put, $"{UsersPath}/{target.Id}"));
            }

            (await admin.GetAsync<UserDetail>($"{UsersPath}/{user.Id}")).RoleIds.ShouldBe([nurse]);
            (await admin.GetAsync<UserDetail>($"{UsersPath}/{target.Id}")).RoleIds.ShouldNotContain(accountant);
            (await admin.GetAsync<RoleDetail>($"{RolesPath}/{desk.Id}")).Permissions.ShouldBe([PlatformPermissions.MastersRead]);
        }
        finally
        {
            // Leaving is done at once.
            var them = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{(await FindUserAsync(hospitalAdmin, approverName)).Id}");
            using var left = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{them.Id}", Update(them, them.RoleIds) with { IsActive = false });
            left.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Permissions_taken_from_a_role_go_at_once_and_those_added_wait_for_someone_who_holds_them()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var role = await server.CreateRoleAsync(admin, new SaveRoleRequest(
            "IT records desk " + Suffix(), "Reads masters and files", [PlatformPermissions.MastersRead, PlatformPermissions.FilesRead], Version: null));
        role.Permissions.ShouldBe([PlatformPermissions.FilesRead, PlatformPermissions.MastersRead]);

        // Reading the audit trail is administrative: the hospital administrator holds it, so may approve it.
        using (var changed = await admin.SendRawAsync(HttpMethod.Put, $"{RolesPath}/{role.Id}", new SaveRoleRequest(
                   role.Name, role.Description, [PlatformPermissions.MastersRead, PlatformPermissions.AuditRead], role.Version)))
        {
            changed.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var waiting = await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}");
        waiting.Permissions.ShouldBe([PlatformPermissions.MastersRead], "the files permission is taken away at once");
        waiting.PendingGrant.ShouldNotBeNull().Permissions.ShouldBe([PlatformPermissions.AuditRead]);
        await server.ApproveGrantAsync(waiting.PendingGrant.ApprovalId);
        (await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}")).Permissions.ShouldBe([PlatformPermissions.AuditRead, PlatformPermissions.MastersRead]);
        (await AuditCountAsync("role.permissions_granted", role.Id)).ShouldBe(2, "its first permissions and the one added, each approved");
    }

    [Fact]
    public async Task Someone_who_leaves_while_their_access_waits_is_given_nothing_when_it_is_approved()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await RoleIdAsync(hospitalAdmin, SystemRoles.FrontOffice);
        var created = await hospitalAdmin.PostAsync<TemporaryPasswordResponse>(UsersPath, NewUser(frontOffice, await FacilityAsync(hospitalAdmin)));
        var approval = created.ApprovalId.ShouldNotBeNull();

        // They leave before anyone decides it: deactivating is done at once, and what waits is not asked for again.
        var user = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{created.UserId}");
        using (var left = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{user.Id}", Update(user, [frontOffice]) with { IsActive = false }))
        {
            left.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var request = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval}");
        using (var approved = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{approval}/approve", new ApproveRequest(null, request.Version)))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await approved.Content.ReadAsStringAsync(Ct)).ShouldContain("access.user_inactive");
        }

        (await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approval}")).Status.ShouldBe(ApprovalStatus.Failed);
        var after = await admin.GetAsync<UserDetail>($"{UsersPath}/{user.Id}");
        (after.IsActive, after.RoleIds.Count, after.FacilityIds.Count).ShouldBe((false, 0, 0));
    }

    [Fact]
    public async Task Access_given_works_at_once_for_someone_already_signed_in()
    {
        // Someone at a desk of the hospital's own, signed in all the while: the server holds their permissions for a while.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var desk = await server.CreateRoleAsync(admin, new SaveRoleRequest("IT theatre desk " + Suffix(), "Reads masters", [PlatformPermissions.MastersRead], Version: null));
        var userName = "it-grant-live-" + Suffix();
        await server.CreateStaffAccountAsync(userName, desk.Name);
        var (clerk, _) = await server.SignInNewSessionAsync(userName);

        // A permission added to their role works the moment it is approved, not a few minutes later.
        await ShowsAsync(clerk, TheatresPath, HttpStatusCode.Forbidden);
        using (var asked = await admin.SendRawAsync(HttpMethod.Put, $"{RolesPath}/{desk.Id}", new SaveRoleRequest(
                   desk.Name, desk.Description, [PlatformPermissions.MastersRead, "ot.read"], desk.Version)))
        {
            asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            await ShowsAsync(clerk, TheatresPath, HttpStatusCode.Forbidden, "not while it waits");
            await server.ApproveGrantAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(asked, HttpMethod.Put, $"{RolesPath}/{desk.Id}")).Id);
        }

        await ShowsAsync(clerk, TheatresPath, HttpStatusCode.OK, "the permissions the server held for the role's holders are forgotten once it is given");

        // So does a role given to them.
        await ShowsAsync(clerk, BloodUnitsPath, HttpStatusCode.Forbidden);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var user = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{(await FindUserAsync(hospitalAdmin, userName)).Id}");
        using (var asked = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{user.Id}",
                   Update(user, [.. user.RoleIds, await RoleIdAsync(hospitalAdmin, SystemRoles.Nurse)])))
        {
            asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            await ShowsAsync(clerk, BloodUnitsPath, HttpStatusCode.Forbidden, "not while it waits");
            await server.ApproveGrantAsync((await ApiClient.ReadAsync<ApprovalRequestDto>(asked, HttpMethod.Put, $"{UsersPath}/{user.Id}")).Id);
        }

        await ShowsAsync(clerk, BloodUnitsPath, HttpStatusCode.OK, "the permissions the server held for them are forgotten once it is given");
    }

    [Fact]
    public async Task That_the_hospital_has_had_two_approvers_is_kept_in_the_audit_trail_through_a_restart()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await server.CreateRoleAsync(admin, new SaveRoleRequest("IT latch desk " + Suffix(), "Reads masters", [PlatformPermissions.MastersRead], Version: null));
        (await LatchCountAsync()).ShouldBe(1, "two people may approve grants here");

        // A restart forgets what the server held in memory; the audit trail does not.
        server.Services.GetRequiredService<GrantLatch>().IsSet = false;
        await server.CreateRoleAsync(admin, new SaveRoleRequest("IT latch desk " + Suffix(), "Reads masters", [PlatformPermissions.MastersRead], Version: null));
        (await LatchCountAsync()).ShouldBe(1, "written once, the first time; read back after a restart");
    }

    [Fact]
    public async Task A_grant_nobody_else_may_approve_is_a_Super_Administrators_break_glass_shown_on_System_health()
    {
        // Backups are the Super Administrator's alone: nobody else holds them to approve them.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var role = await admin.PostAsync<RoleDetail>(RolesPath, new SaveRoleRequest(
            "IT backup runners " + Suffix(), "Runs the backups", [PlatformPermissions.BackupsManage], Version: null));
        role.Permissions.ShouldBe([PlatformPermissions.BackupsManage], "given at once, as a break-glass");
        role.PendingGrant.ShouldBeNull();
        (await AuditAsync(GrantApprovals.AloneAction, role.Id)).ShouldBe(AuditSeverity.Critical);

        // A break-glass gives others access, never oneself: nobody else may approve the role for the Super Administrator.
        var me = await admin.GetAsync<UserDetail>($"{UsersPath}/{(await FindUserAsync(admin, HospitalServer.Users.Admin)).Id}");
        using (var mine = await admin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{me.Id}", Update(me, [.. me.RoleIds, role.Id])))
        {
            mine.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await mine.Content.ReadAsStringAsync(Ct)).ShouldContain(GrantApprovals.SelfGrantCode);
        }

        // A training server shows nothing; a live one, a critical warning, so IT on-call is told.
        (await admin.GetAsync<SystemHealthReport>("/api/v1/admin/system/health")).Warnings.ShouldNotContain(w => w.Code == "access.granted_alone");
        using (server.AsLiveServer())
        {
            var warning = (await admin.GetAsync<SystemHealthReport>("/api/v1/admin/system/health")).Warnings.Single(w => w.Code == "access.granted_alone");
            warning.Severity.ShouldBe("critical");
        }
    }

    private static CreateUserRequest NewUser(Guid roleId, Guid facilityId) =>
        new("it-grant-" + Suffix(), "Grant Test", Email: null, Phone: null, [roleId], [facilityId], facilityId, PractitionerId: null, TemporaryPassword: null,
            Reason: "Joins the hospital.");

    private static UpdateUserRequest Update(UserDetail user, IReadOnlyList<Guid> roleIds) =>
        new(user.DisplayName, user.Email, user.Phone, roleIds, user.FacilityIds, user.DefaultFacilityId, user.PractitionerId, user.IsActive, user.Version);

    private static async Task<Guid> RoleIdAsync(ApiClient admin, string role) =>
        (await admin.GetAsync<List<RoleListItem>>(RolesPath)).Single(r => r.Name == role).Id;

    private static async Task<Guid> FacilityAsync(ApiClient admin) =>
        (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("No facility.");

    private static async Task<UserListItem> FindUserAsync(ApiClient admin, string userName) =>
        (await admin.GetAsync<PagedResult<UserListItem>>($"{UsersPath}?search={userName}")).Items.Single(u => u.UserName == userName);

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }

    /// <summary>
    /// <paramref name="approver"/> may not approve <paramref name="asked"/>: refused, it still waits, and the one who asked
    /// withdraws it.
    /// </summary>
    private static async Task RefusedAsync(ApiClient approver, ApiClient asker, ApprovalRequestDto asked)
    {
        using (var approved = await approver.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            approved.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await approved.Content.ReadAsStringAsync(Ct));
        }

        var still = await asker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        still.Status.ShouldBe(ApprovalStatus.Pending);
        await asker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/withdraw", new WithdrawApprovalRequest(still.Version));
    }

    private static async Task ShowsAsync(ApiClient client, string path, HttpStatusCode expected, string? because = null)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(expected, because);
    }

    private async Task<int> LatchCountAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == GrantApprovals.LatchAction, Ct);
    }

    private async Task<AuditSeverity> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>()
            .Where(e => e.Action == action && e.EntityId == id).Select(e => e.Severity).SingleAsync(Ct);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
}
