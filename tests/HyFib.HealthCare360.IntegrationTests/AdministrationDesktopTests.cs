using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SEC-08: only enrolled devices may use privileged staff features, and each device can be revoked. Administration
/// works only in a session signed in on an enrolled HC360 Desktop; a browser keeps everything else its user holds
/// (<see cref="AdministrationOnDesktop"/>). The other tests sign in from the run's enrolled workstation
/// (<see cref="HospitalServer.Desktop"/>); these sign in from a browser where they say so.
/// </summary>
public sealed class AdministrationDesktopTests(HospitalServer server)
{
    private const string SettingPath = "/api/v1/admin/settings/" + AdministrationOnDesktop.SettingKey;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_browser_keeps_the_users_other_work_but_not_administration()
    {
        var (browser, refreshCookie) = await server.SignInFromBrowserAsync(HospitalServer.Users.HospitalAdmin);

        await RefusedAsync(browser, HttpMethod.Get, "/api/v1/admin/users");
        await RefusedAsync(browser, HttpMethod.Get, "/api/v1/admin/audit");
        await RefusedAsync(browser, HttpMethod.Put, SettingPath, new SaveSettingRequest(JsonValue.Create(false), null, false, null));
        (await browser.GetAsync<PagedResult<JsonElement>>("/api/v1/patients?pageSize=1")).ShouldNotBeNull();

        // The app is told what the server allows: it shows no administration there, and says where it is.
        var me = await browser.GetAsync<AuthUser>("/api/v1/auth/me");
        me.Permissions.ShouldNotContain(p => PlatformPermissions.IsAdministrative(p));
        me.Permissions.ShouldContain("patients.read");
        me.AdministrationNeedsDesktop.ShouldBeTrue();
        using (var refresh = await server.RefreshAsync(refreshCookie))
        {
            var refreshed = await ApiClient.ReadAsync<AuthResult>(refresh, HttpMethod.Post, "/api/v1/auth/refresh");
            refreshed.User.ShouldNotBeNull().Permissions.ShouldBe(me.Permissions, ignoreOrder: true);
            refreshed.User.AdministrationNeedsDesktop.ShouldBeTrue();
        }

        // What signing in answers says the same.
        (await SignInAnswerAsync(fromDesktop: false)).Permissions.ShouldNotContain(PlatformPermissions.UsersManage);
        (await SignInAnswerAsync(fromDesktop: true)).Permissions.ShouldContain(PlatformPermissions.UsersManage);

        // The same account in HC360 Desktop on the enrolled workstation.
        var (desktop, desktopCookie) = await server.SignInNewSessionAsync(HospitalServer.Users.HospitalAdmin);
        (await desktop.GetAsync<PagedResult<UserListItem>>("/api/v1/admin/users?pageSize=1")).Items.ShouldNotBeEmpty();
        var there = await desktop.GetAsync<AuthUser>("/api/v1/auth/me");
        there.Permissions.ShouldContain(PlatformPermissions.UsersManage);
        there.AdministrationNeedsDesktop.ShouldBeFalse();
        using (var refresh = await server.RefreshAsync(desktopCookie))
        {
            (await ApiClient.ReadAsync<AuthResult>(refresh, HttpMethod.Post, "/api/v1/auth/refresh")).User.ShouldNotBeNull()
                .Permissions.ShouldContain(PlatformPermissions.UsersManage);
        }
    }

    [Fact]
    public async Task All_access_in_a_browser_is_every_permission_but_administration()
    {
        var (browser, _) = await server.SignInFromBrowserAsync(HospitalServer.Users.Admin);

        var me = await browser.GetAsync<AuthUser>("/api/v1/auth/me");
        var registry = server.Services.GetRequiredService<PermissionRegistry>();
        me.Permissions.ShouldBe(registry.All.Select(p => p.Name).Where(p => !PlatformPermissions.IsAdministrative(p)), ignoreOrder: true);
        me.AdministrationNeedsDesktop.ShouldBeTrue();
        await RefusedAsync(browser, HttpMethod.Get, "/api/v1/admin/system/health");
        (await browser.GetAsync<PagedResult<JsonElement>>("/api/v1/patients?pageSize=1")).ShouldNotBeNull();
    }

    [Fact]
    public async Task Someone_without_the_permission_is_refused_as_before()
    {
        var (nurse, _) = await server.SignInFromBrowserAsync(HospitalServer.Users.Nurse);

        using var response = await nurse.SendRawAsync(HttpMethod.Get, "/api/v1/admin/users");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(AdministrationOnDesktop.ErrorCode);
        (await nurse.GetAsync<AuthUser>("/api/v1/auth/me")).AdministrationNeedsDesktop.ShouldBeFalse();
    }

    [Fact]
    public async Task A_revoked_desktop_ends_its_sessions_and_no_longer_opens_administration()
    {
        var it = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (deviceId, secret) = await EnrolAsync(it, DeviceKind.Workstation);
        var onDevice = await server.SignInFromDeviceAsync(HospitalServer.Users.HospitalAdmin, deviceId, secret);
        (await onDevice.GetAsync<PagedResult<UserListItem>>("/api/v1/admin/users?pageSize=1")).Items.ShouldNotBeEmpty();

        await it.PostAsync($"/api/v1/admin/devices/{deviceId}/revoke");

        using (var ended = await onDevice.SendRawAsync(HttpMethod.Get, "/api/v1/admin/users?pageSize=1"))
        {
            ended.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Its credential is now no better than a browser's: the account signs in, without administration.
        var again = await server.SignInFromDeviceAsync(HospitalServer.Users.HospitalAdmin, deviceId, secret);
        await RefusedAsync(again, HttpMethod.Get, "/api/v1/admin/users");
        (await again.GetAsync<AuthUser>("/api/v1/auth/me")).AdministrationNeedsDesktop.ShouldBeTrue();
    }

    [Theory]
    [InlineData(DeviceKind.Kiosk)]
    [InlineData(DeviceKind.DisplayBoard)]
    public async Task A_kiosk_or_queue_display_does_not_open_administration(DeviceKind kind)
    {
        var it = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (deviceId, secret) = await EnrolAsync(it, kind);
        try
        {
            // An administrator who signs in at a shared kiosk or display keeps their other work there, as in a browser.
            var there = await server.SignInFromDeviceAsync(HospitalServer.Users.HospitalAdmin, deviceId, secret);
            await RefusedAsync(there, HttpMethod.Get, "/api/v1/admin/users");
            (await there.GetAsync<PagedResult<JsonElement>>("/api/v1/patients?pageSize=1")).ShouldNotBeNull();
            var me = await there.GetAsync<AuthUser>("/api/v1/auth/me");
            me.Permissions.ShouldNotContain(p => PlatformPermissions.IsAdministrative(p));
            me.AdministrationNeedsDesktop.ShouldBeTrue();
        }
        finally
        {
            await it.PostAsync($"/api/v1/admin/devices/{deviceId}/revoke");
        }
    }

    [Fact]
    public async Task The_hospital_can_let_browsers_administer_too()
    {
        var it = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (browser, _) = await server.SignInFromBrowserAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await it.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId;

        // A yes or a no, for the whole hospital: anything else is refused rather than read as something.
        foreach (var refused in new[]
                 {
                     new SaveSettingRequest(JsonValue.Create("no"), null, false, null),
                     new SaveSettingRequest(JsonValue.Create(false), facilityId, false, null),
                 })
        {
            using var response = await it.SendRawAsync(HttpMethod.Put, SettingPath, refused);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        try
        {
            await it.SaveSettingAsync(AdministrationOnDesktop.SettingKey, new SaveSettingRequest(JsonValue.Create(false), null, false, null));
            (await browser.GetAsync<PagedResult<UserListItem>>("/api/v1/admin/users?pageSize=1")).Items.ShouldNotBeEmpty();
            var me = await browser.GetAsync<AuthUser>("/api/v1/auth/me");
            me.Permissions.ShouldContain(PlatformPermissions.UsersManage);
            me.AdministrationNeedsDesktop.ShouldBeFalse();

            await it.SaveSettingAsync(AdministrationOnDesktop.SettingKey, new SaveSettingRequest(JsonValue.Create("true"), null, false, null));
            await RefusedAsync(browser, HttpMethod.Get, "/api/v1/admin/users");
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(AdministrationOnDesktop.SettingKey, null, Ct);
        }
    }

    [Fact]
    public async Task While_no_workstation_is_enrolled_a_browser_can_enrol_the_first_one()
    {
        var it = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // A hospital whose desktops are kiosks and queue displays, or none yet: the workstations and tablets enrolled so far
        // are set aside for the test (their sessions go on) and put back after it.
        var setAside = await SetAsideAsync(revoked: true, []);
        try
        {
            var (browser, _) = await server.SignInFromBrowserAsync(HospitalServer.Users.HospitalAdmin);
            var me = await browser.GetAsync<AuthUser>("/api/v1/auth/me");
            me.Permissions.Where(PlatformPermissions.IsAdministrative).ShouldBe([PlatformPermissions.DevicesManage]);
            me.AdministrationNeedsDesktop.ShouldBeTrue();

            // What enrolling needs, and nothing else: no other administration, no changing or revoking a device.
            (await browser.GetAsync<List<DeviceDto>>("/api/v1/admin/devices")).ShouldNotBeNull();
            (await browser.GetAsync<JsonElement>("/api/v1/admin/devices/desktop-update")).ValueKind.ShouldBe(JsonValueKind.Object);
            await RefusedAsync(browser, HttpMethod.Get, "/api/v1/admin/users");
            var kiosk = await EnrolAsync(browser, DeviceKind.Kiosk);
            await RefusedAsync(browser, HttpMethod.Post, $"/api/v1/admin/devices/{kiosk.DeviceId}/revoke");

            // A kiosk is no place to administer from, so enrolling one ends nothing; the first workstation does.
            (await browser.GetAsync<List<DeviceDto>>("/api/v1/admin/devices")).ShouldContain(d => d.Id == kiosk.DeviceId);
            var workstation = await EnrolAsync(browser, DeviceKind.Workstation);
            await RefusedAsync(browser, HttpMethod.Get, "/api/v1/admin/devices");
            await RefusedAsync(browser, HttpMethod.Post, "/api/v1/admin/devices/enrollment-codes",
                new CreateEnrollmentCodeRequest(DeviceKind.Workstation, null, "Second workstation", 30));
            (await browser.GetAsync<AuthUser>("/api/v1/auth/me")).Permissions.ShouldNotContain(PlatformPermissions.DevicesManage);

            var onWorkstation = await server.SignInFromDeviceAsync(HospitalServer.Users.HospitalAdmin, workstation.DeviceId, workstation.Secret);
            (await onWorkstation.GetAsync<PagedResult<UserListItem>>("/api/v1/admin/users?pageSize=1")).Items.ShouldNotBeEmpty();
            await onWorkstation.PostAsync($"/api/v1/admin/devices/{kiosk.DeviceId}/revoke");
        }
        finally
        {
            await SetAsideAsync(revoked: false, setAside);
        }

        (await it.GetAsync<PagedResult<UserListItem>>("/api/v1/admin/users?pageSize=1")).Items.ShouldNotBeEmpty();
    }

    /// <summary>Asserts the refusal of a request that only administration allows, made away from an enrolled desktop.</summary>
    private static async Task RefusedAsync(ApiClient client, HttpMethod method, string path, object? body = null)
    {
        using var response = await client.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe(AdministrationOnDesktop.ErrorCode);
        problem.GetProperty("detail").GetString().ShouldBe(AdministrationOnDesktop.Message);
    }

    /// <summary>The user a sign-in of the hospital administrator answers with, from a browser or the enrolled workstation.</summary>
    private async Task<AuthUser> SignInAnswerAsync(bool fromDesktop)
    {
        var (deviceId, secret) = server.Desktop;
        var request = fromDesktop
            ? new LoginRequest(HospitalServer.Users.HospitalAdmin, server.Password, DeviceId: deviceId, DeviceSecret: secret)
            : new LoginRequest(HospitalServer.Users.HospitalAdmin, server.Password);
        var answer = await server.AnonymousFrom($"198.18.{RandomNumberGenerator.GetInt32(0, 256)}.{RandomNumberGenerator.GetInt32(1, 255)}")
            .PostAsync<AuthResult>("/api/v1/auth/login", request);
        return answer.User.ShouldNotBeNull();
    }

    private async Task<(Guid DeviceId, string Secret)> EnrolAsync(ApiClient admin, DeviceKind kind)
    {
        var code = await admin.PostAsync<EnrollmentCodeResponse>("/api/v1/admin/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(kind, null, "SEC-08 test", 30));
        var enrolled = await server.Anonymous(ownAddress: true).PostAsync<EnrollDeviceResponse>("/api/v1/devices/enroll",
            new EnrollDeviceRequest(code.Code, $"SEC-08 {kind} {Guid.NewGuid().ToString("N")[..6]}", "ADMIN-PC", "Windows 11", "1.0.0"));
        return (enrolled.DeviceId, enrolled.DeviceSecret);
    }

    /// <summary>
    /// Marks the enrolled workstations and tablets revoked in the database, without ending their sessions as revoking them
    /// would, and returns them; with <paramref name="revoked"/> false, puts <paramref name="devices"/> back.
    /// </summary>
    private async Task<List<Guid>> SetAsideAsync(bool revoked, List<Guid> devices)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var all = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Device>();
        if (revoked)
        {
            devices = await all.Where(d => !d.IsRevoked && (d.Kind == DeviceKind.Workstation || d.Kind == DeviceKind.Tablet))
                .Select(d => d.Id).ToListAsync(Ct);
        }

        await all.Where(d => devices.Contains(d.Id)).ExecuteUpdateAsync(d => d.SetProperty(x => x.IsRevoked, revoked), Ct);
        return devices;
    }
}
