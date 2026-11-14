using System.Net;
using HyFib.HealthCare360.Modules.Privacy;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A whole role turned off and back on (<see cref="Role.IsActive"/>), over the real API and database. Off takes what the
/// role gives from its holders at once, a session already signed in included, while the role stays theirs; and a holder of a
/// role that is off is given no work. On gives it back only when a second administrator who may approve grants agrees, never
/// the one who asked and never someone who holds the role. The Super Administrator and Patient roles are never off, and only
/// a holder of <c>admin.roles.manage</c> switches a role, against the version it was shown at. Each change is in the audit
/// trail with its reason. The rows of who may switch a role that the seeded hospital cannot reach (a role one holds) are
/// unit-tested (RoleSwitchRuleTests); what an upgrade does to existing roles is RoleSwitchMigrationTests.
/// </summary>
public sealed class RoleSwitchTests(HospitalServer server)
{
    private const string RolesPath = "/api/v1/admin/roles";
    private const string UsersPath = "/api/v1/admin/users";
    private const string TheatresPath = "/api/v1/ot/theatres"; // ot.read
    private const string OfficersPath = "/api/v1/privacy/officers"; // who a privacy request can be given to

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_role_turned_off_stops_its_holders_at_once_and_is_back_only_when_another_administrator_agrees()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var role = await server.CreateRoleAsync(admin, new SaveRoleRequest("IT theatre list " + Suffix(), "Reads the theatre list (tests)", ["ot.read"], Version: null));
        var userName = "it-role-off-" + Suffix();
        await server.CreateStaffAccountAsync(userName, role.Name);
        var (holder, _) = await server.SignInNewSessionAsync(userName);
        await ShowsAsync(holder, TheatresPath, HttpStatusCode.OK);

        // Off: at once, for a session already signed in, while the role stays theirs.
        using (var off = await SwitchAsync(admin, role.Id, active: false, role.Version, "The theatre list goes back to paper for the audit week."))
        {
            off.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await ShowsAsync(holder, TheatresPath, HttpStatusCode.Forbidden, "a role turned off gives nothing from the next request");
        var listed = (await admin.GetAsync<List<RoleListItem>>(RolesPath)).Single(r => r.Id == role.Id);
        (listed.IsActive, listed.UserCount, listed.SoleRoleHolders).ShouldBe((false, 1, 1), "the role is still theirs, and their only one");
        (await holder.GetAsync<AuthUser>("/api/v1/auth/me")).Roles.ShouldNotContain(role.Name, "a role turned off is not shown as one of theirs");
        var turnedOff = (await AuditAsync(AccessAdminEndpoints.RoleDeactivatedAction, role.Id)).ShouldHaveSingleItem();
        turnedOff.Severity.ShouldBe(AuditSeverity.Warning);
        turnedOff.Details.ShouldNotBeNull().ShouldContain("The theatre list goes back to paper for the audit week.");

        // On: it waits for another administrator who may approve grants; nothing comes back meanwhile, and the one who asked
        // cannot approve it.
        var shown = await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}");
        shown.IsActive.ShouldBeFalse();
        using var on = await SwitchAsync(admin, role.Id, active: true, shown.Version, "The theatre list is back on HC360.");
        on.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(on, HttpMethod.Put, $"{RolesPath}/{role.Id}/active");
        (asked.Kind, asked.Reason).ShouldBe((GrantApprovals.RoleActivationKind, "The theatre list is back on HC360."));
        (await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}")).PendingActivation.ShouldNotBeNull().ApprovalId.ShouldBe(asked.Id);
        await ShowsAsync(holder, TheatresPath, HttpStatusCode.Forbidden, "not while it waits");
        using (var own = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The hospital administrator approves: what the role gives is back, for the session already signed in too.
        (await server.ApproveGrantAsync(asked.Id)).Status.ShouldBe(ApprovalStatus.Approved);
        await ShowsAsync(holder, TheatresPath, HttpStatusCode.OK, "the permissions the server held for the role's holders are forgotten once it is on");
        var back = await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}");
        (back.IsActive, back.PendingActivation).ShouldBe((true, null));
        (await holder.GetAsync<AuthUser>("/api/v1/auth/me")).Roles.ShouldContain(role.Name);
        (await AuditAsync(GrantApprovals.RoleReactivatedAction, role.Id)).ShouldHaveSingleItem().Severity.ShouldBe(AuditSeverity.Warning);
    }

    [Fact]
    public async Task The_Super_Administrator_and_Patient_roles_are_never_off_and_only_a_holder_of_the_roles_permission_switches_one()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var roles = await admin.GetAsync<List<RoleListItem>>(RolesPath);
        foreach (var name in new[] { SystemRoles.SuperAdmin, SystemRoles.Patient })
        {
            var role = roles.Single(r => r.Name == name);
            role.CanTurnOff.ShouldBeFalse();
            using var refused = await SwitchAsync(admin, role.Id, active: false, role.Version, reason: null);
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("role.protected");
        }

        (await admin.GetAsync<List<RoleListItem>>(RolesPath)).Where(r => r.Name is SystemRoles.SuperAdmin or SystemRoles.Patient)
            .ShouldAllBe(r => r.IsActive);

        // The hospital administrator manages users; roles are the Super Administrator's (admin.roles.manage).
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var nurse = roles.Single(r => r.Name == SystemRoles.Nurse);
        nurse.CanTurnOff.ShouldBeTrue();
        using (var notPermitted = await SwitchAsync(hospitalAdmin, nurse.Id, active: false, nurse.Version, reason: null))
        {
            notPermitted.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await admin.GetAsync<RoleDetail>($"{RolesPath}/{nurse.Id}")).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task A_holder_of_a_role_that_is_off_is_given_no_work()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var role = await server.CreateRoleAsync(admin, new SaveRoleRequest(
            "IT privacy desk " + Suffix(), "Handles privacy requests (tests)", [PrivacyPermissions.RequestsRead, PrivacyPermissions.RequestsManage], Version: null));
        var userName = "it-role-work-" + Suffix();
        await server.CreateStaffAccountAsync(userName, role.Name);
        var officer = await FindUserAsync(admin, userName);
        (await admin.GetAsync<List<PrivacyOfficer>>(OfficersPath)).ShouldContain(o => o.Id == officer.Id, "a privacy request can be given to them");

        using (var off = await SwitchAsync(admin, role.Id, active: false, role.Version, "The privacy desk moves to another team."))
        {
            off.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await admin.GetAsync<List<PrivacyOfficer>>(OfficersPath)).ShouldNotContain(o => o.Id == officer.Id,
            "nothing is given to someone whose role gives them nothing");
    }

    [Fact]
    public async Task A_role_shown_before_someone_changed_it_is_not_switched()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var role = await server.CreateRoleAsync(admin, new SaveRoleRequest("IT stale switch " + Suffix(), "Reads masters (tests)", [PlatformPermissions.MastersRead], Version: null));
        await server.UpdateAndApproveAsync(admin, $"{RolesPath}/{role.Id}",
            new SaveRoleRequest(role.Name, "Reads the masters (tests)", [PlatformPermissions.MastersRead], role.Version));

        using (var stale = await SwitchAsync(admin, role.Id, active: false, role.Version, reason: null))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}")).IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Turning_a_role_back_on_is_not_approved_by_someone_who_holds_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var role = await server.CreateRoleAsync(admin, new SaveRoleRequest("IT held role " + Suffix(), "Reads masters (tests)", [PlatformPermissions.MastersRead], Version: null));
        var me = (await FindUserAsync(hospitalAdmin, HospitalServer.Users.HospitalAdmin)).Id;
        var mine = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{me}");
        await server.UpdateAndApproveAsync(hospitalAdmin, $"{UsersPath}/{me}", Update(mine, [.. mine.RoleIds, role.Id]));
        try
        {
            using (var off = await SwitchAsync(admin, role.Id, active: false, (await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}")).Version, "Paused."))
            {
                off.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }

            // The hospital administrator may approve grants, so turning it on waits for them; but they hold the role.
            using var on = await SwitchAsync(admin, role.Id, active: true, (await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}")).Version, "Resumed.");
            on.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(on, HttpMethod.Put, $"{RolesPath}/{role.Id}/active");
            using (var own = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            {
                own.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                (await own.Content.ReadAsStringAsync(Ct)).ShouldContain(GrantApprovals.SelfGrantCode);
            }

            (await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Pending);
            (await admin.GetAsync<RoleDetail>($"{RolesPath}/{role.Id}")).IsActive.ShouldBeFalse();
            var left = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
            await admin.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/withdraw", new WithdrawApprovalRequest(left.Version));
        }
        finally
        {
            // The hospital administrator's roles as they were: taking one away is done at once.
            var now = await hospitalAdmin.GetAsync<UserDetail>($"{UsersPath}/{me}");
            using var restored = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"{UsersPath}/{me}", Update(now, [.. now.RoleIds.Where(r => r != role.Id)]));
            restored.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }

    private static Task<HttpResponseMessage> SwitchAsync(ApiClient client, Guid roleId, bool active, uint version, string? reason) =>
        client.SendRawAsync(HttpMethod.Put, $"{RolesPath}/{roleId}/active", new SetRoleActiveRequest(active, version, reason));

    private static async Task ShowsAsync(ApiClient client, string path, HttpStatusCode expected, string? because = null)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(expected, because);
    }

    private static async Task<UserListItem> FindUserAsync(ApiClient admin, string userName) =>
        (await admin.GetAsync<PagedResult<UserListItem>>($"{UsersPath}?search={userName}")).Items.Single(u => u.UserName == userName);

    private static UpdateUserRequest Update(UserDetail user, IReadOnlyList<Guid> roleIds) =>
        new(user.DisplayName, user.Email, user.Phone, roleIds, user.FacilityIds, user.DefaultFacilityId, user.PractitionerId, user.IsActive, user.Version);

    private async Task<List<AuditEvent>> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == action && e.EntityId == id).ToListAsync(Ct);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
}
