using System.Net;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Access-control holes found by the ASVS Level 2 review, checked through the real API: an administrator cannot take
/// over an account more privileged than their own or hand out permissions they do not hold, and a patient cannot act in
/// a facility's name.
/// </summary>
public sealed class AccessControlTests(HospitalServer server)
{
    [Theory]
    [InlineData("reset-password")]
    [InlineData("reset-mfa")]
    [InlineData("unlock")]
    [InlineData("revoke-sessions")]
    public async Task A_hospital_administrator_cannot_take_over_a_super_administrator(string action)
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var superAdmin = await FindUserAsync(hospitalAdmin, HospitalServer.Users.Admin);

        using var response = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"/api/v1/admin/users/{superAdmin.Id}/{action}");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("user.more_privileged");
    }

    [Fact]
    public async Task A_hospital_administrator_cannot_edit_a_super_administrators_account()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var superAdmin = await hospitalAdmin.GetAsync<UserDetail>($"/api/v1/admin/users/{(await FindUserAsync(hospitalAdmin, HospitalServer.Users.Admin)).Id}");

        using var response = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/users/{superAdmin.Id}", new UpdateUserRequest(
            superAdmin.DisplayName, superAdmin.Email, superAdmin.Phone, superAdmin.RoleIds, superAdmin.FacilityIds,
            superAdmin.DefaultFacilityId, superAdmin.PractitionerId, IsActive: false, superAdmin.Version));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_hospital_administrator_still_manages_the_accounts_below_them()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await RoleIdAsync(hospitalAdmin, SystemRoles.FrontOffice);
        var created = await hospitalAdmin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", NewUser("it-front-desk-2", frontOffice));

        var reset = await hospitalAdmin.PostAsync<TemporaryPasswordResponse>($"/api/v1/admin/users/{created.UserId}/reset-password");

        reset.TemporaryPassword.ShouldNotBe(created.TemporaryPassword);
    }

    [Fact]
    public async Task Administrative_permissions_stay_with_those_who_hold_them()
    {
        // Backups (and the recovery key) are for the Super Administrator only: the hospital administrator does not hold
        // that permission, so can neither give it nor take over an account that has it.
        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        var backups = await superAdmin.PostAsync<RoleDetail>("/api/v1/admin/roles", new SaveRoleRequest(
            "IT backups", "Runs the backups", [PlatformPermissions.BackupsManage], Version: null));
        var operatorAccount = await superAdmin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", NewUser("it-backup-operator", backups.Id));
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        using var grant = await hospitalAdmin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/users", NewUser("it-backup-operator-2", backups.Id));
        using var takeOver = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"/api/v1/admin/users/{operatorAccount.UserId}/reset-password");

        grant.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await grant.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain(PlatformPermissions.BackupsManage);
        takeOver.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_patient_role_cannot_be_given_staff_permissions()
    {
        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        var patientRole = await superAdmin.GetAsync<RoleDetail>($"/api/v1/admin/roles/{await RoleIdAsync(superAdmin, SystemRoles.Patient)}");

        using var response = await superAdmin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/roles/{patientRole.Id}", new SaveRoleRequest(
            patientRole.Name, patientRole.Description, [.. patientRole.Permissions, PlatformPermissions.UsersRead], patientRole.Version));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("role.protected");
    }

    [Fact]
    public async Task A_patient_cannot_act_in_a_facilitys_name()
    {
        var facilityId = (await (await server.SignInAsync(HospitalServer.Users.FrontOffice)).GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The front office has no facility.");
        var patient = await server.SignInToPortalAsync("9876500011");

        // Staff endpoints do not answer on the portal's address, and refuse a patient's sign-in on the staff app's.
        using (var onPortal = await patient.SendRawAsync(HttpMethod.Post, "/api/v1/auth/switch-facility", new SwitchFacilityRequest(facilityId)))
        {
            onPortal.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var response = await server.OnStaffAddress(patient).SendRawAsync(HttpMethod.Post, "/api/v1/auth/switch-facility", new SwitchFacilityRequest(facilityId));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<UserListItem> FindUserAsync(ApiClient admin, string userName) =>
        (await admin.GetAsync<PagedResult<UserListItem>>($"/api/v1/admin/users?search={userName}")).Items.Single(u => u.UserName == userName);

    private static async Task<Guid> RoleIdAsync(ApiClient admin, string role) =>
        (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).Single(r => r.Name == role).Id;

    private static CreateUserRequest NewUser(string userName, Guid roleId) =>
        new(userName, userName, Email: null, Phone: null, [roleId], FacilityIds: [], DefaultFacilityId: null, PractitionerId: null, TemporaryPassword: null);
}
