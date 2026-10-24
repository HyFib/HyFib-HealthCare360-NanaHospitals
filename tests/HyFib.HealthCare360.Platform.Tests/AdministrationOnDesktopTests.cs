using System.Security.Claims;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// SRS SEC-08: the Administration permissions work only in a session signed in on an enrolled HC360 Desktop; a browser
/// keeps the rest (<see cref="AdministrationOnDesktop"/>). The requests themselves are refused by
/// <see cref="PermissionAuthorizationHandler"/>; the app is told what is left by <see cref="AdministrationOnDesktop.Elsewhere"/>.
/// </summary>
public sealed class AdministrationOnDesktopTests
{
    private const string PatientsRead = "patients.read";
    private const string LabStatusRead = "integrations.lab.status.read";

    private static readonly Guid UserId = Guid.Parse("0f3e2d1c-0000-4000-8000-000000000001");
    private static readonly Guid DeviceId = Guid.Parse("0f3e2d1c-0000-4000-8000-0000000000d1");

    private static readonly PermissionRegistry Registry = new(
    [
        new(PlatformPermissions.UsersManage, "Administration", "Users"),
        new(PlatformPermissions.DevicesManage, "Administration", "Devices"),
        new(PlatformPermissions.AuditRead, "Administration", "Audit"),
        new(PatientsRead, "Patients", "Read"),
        new(LabStatusRead, "Integrations", "Lab status"),
    ]);

    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly IAdministrationDesktops _desktops = Substitute.For<IAdministrationDesktops>();

    public AdministrationOnDesktopTests()
    {
        _desktops.RequiredAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        _desktops.EnrolledAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
        _desktops.AdministersFromAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));
        _desktops.AdministersFromAsync(DeviceId, Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(true));
    }

    [Fact]
    public async Task Administration_is_refused_in_a_browser_and_says_why()
    {
        Holds(PlatformPermissions.UsersManage, PatientsRead);

        var context = await AuthorizeAsync(Browser(), PlatformPermissions.UsersManage);

        context.HasSucceeded.ShouldBeFalse();
        context.HasFailed.ShouldBeTrue();
        context.FailureReasons.ShouldHaveSingleItem().ShouldBeOfType<DesktopRequired>().Message.ShouldBe(AdministrationOnDesktop.Message);
    }

    [Fact]
    public async Task On_an_enrolled_desktop_it_is_allowed()
    {
        Holds(PlatformPermissions.UsersManage);

        (await AuthorizeAsync(Desktop(), PlatformPermissions.UsersManage)).HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task On_a_kiosk_or_queue_display_it_is_refused_as_in_a_browser()
    {
        Holds(PlatformPermissions.UsersManage);
        var kiosk = Browser(new Claim(HcClaims.Device, Guid.NewGuid().ToString()));

        var context = await AuthorizeAsync(kiosk, PlatformPermissions.UsersManage);

        context.HasSucceeded.ShouldBeFalse();
        context.FailureReasons.ShouldHaveSingleItem().ShouldBeOfType<DesktopRequired>();
    }

    [Theory]
    [InlineData(DeviceKind.Workstation, true)]
    [InlineData(DeviceKind.Tablet, true)]
    [InlineData(DeviceKind.Kiosk, false)]
    [InlineData(DeviceKind.DisplayBoard, false)]
    [InlineData(DeviceKind.LabAgent, false)]
    public void Only_workstations_and_tablets_administer(DeviceKind kind, bool administers) =>
        AdministrationOnDesktop.Administers(kind).ShouldBe(administers);

    [Fact]
    public async Task Any_other_permission_the_endpoint_takes_is_enough_in_a_browser()
    {
        Holds(PlatformPermissions.IntegrationsManage, LabStatusRead);

        (await AuthorizeAsync(Browser(), LabStatusRead, PlatformPermissions.IntegrationsManage)).HasSucceeded.ShouldBeTrue();
        (await AuthorizeAsync(Browser(), PatientsRead)).HasSucceeded.ShouldBeFalse();
        await _desktops.DidNotReceive().RequiredAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task One_held_administrative_permission_among_others_the_user_lacks_is_not_enough()
    {
        Holds(PlatformPermissions.IntegrationsManage);

        var context = await AuthorizeAsync(Browser(), LabStatusRead, PlatformPermissions.IntegrationsManage);

        context.HasSucceeded.ShouldBeFalse();
        context.FailureReasons.ShouldHaveSingleItem().ShouldBeOfType<DesktopRequired>();
    }

    [Fact]
    public async Task All_access_in_a_browser_opens_all_but_administration()
    {
        Holds(PermissionRegistry.Wildcard);

        (await AuthorizeAsync(Browser(), PatientsRead)).HasSucceeded.ShouldBeTrue();
        (await AuthorizeAsync(Browser(), PlatformPermissions.AuditRead)).HasFailed.ShouldBeTrue();
        (await AuthorizeAsync(Desktop(), PlatformPermissions.AuditRead)).HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task With_the_rule_turned_off_a_browser_may_administer()
    {
        Holds(PlatformPermissions.UsersManage);
        _desktops.RequiredAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));

        (await AuthorizeAsync(Browser(), PlatformPermissions.UsersManage)).HasSucceeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_the_permission_nothing_is_decided_and_the_rule_is_not_read()
    {
        Holds(PatientsRead);

        var context = await AuthorizeAsync(Browser(), PlatformPermissions.UsersManage);

        context.HasSucceeded.ShouldBeFalse();
        context.HasFailed.ShouldBeFalse();
        await _desktops.DidNotReceive().RequiredAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_restricted_token_gets_nothing_even_on_a_desktop()
    {
        Holds(PermissionRegistry.Wildcard);

        var context = await AuthorizeAsync(Desktop(new Claim(HcClaims.Restriction, "mfa-setup")), PatientsRead);

        context.HasSucceeded.ShouldBeFalse();
        context.HasFailed.ShouldBeFalse();
    }

    [Fact]
    public async Task While_no_workstation_is_enrolled_a_browser_may_use_the_endpoints_that_enrol_one()
    {
        Holds(PlatformPermissions.DevicesManage);
        _desktops.EnrolledAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(false));

        (await AuthorizeAsync(Browser(), PlatformPermissions.DevicesManage, Endpoint(FirstDesktopEnrolment.Instance))).HasSucceeded.ShouldBeTrue();
        (await AuthorizeAsync(Browser(), PlatformPermissions.DevicesManage, Endpoint())).HasFailed.ShouldBeTrue();
        (await AuthorizeAsync(Browser(), PlatformPermissions.DevicesManage)).HasFailed.ShouldBeTrue();
    }

    [Fact]
    public async Task Once_one_is_enrolled_those_endpoints_need_a_desktop_too()
    {
        Holds(PlatformPermissions.DevicesManage);

        (await AuthorizeAsync(Browser(), PlatformPermissions.DevicesManage, Endpoint(FirstDesktopEnrolment.Instance))).HasFailed.ShouldBeTrue();
    }

    [Fact]
    public void A_browser_is_given_every_permission_but_the_administrative_ones()
    {
        var granted = Set(PlatformPermissions.UsersManage, PlatformPermissions.DevicesManage, PatientsRead);

        AdministrationOnDesktop.Elsewhere(granted, Registry, firstEnrolment: false).ShouldBe([PatientsRead]);
        AdministrationOnDesktop.Elsewhere(granted, Registry, firstEnrolment: true).ShouldBe([PlatformPermissions.DevicesManage, PatientsRead], ignoreOrder: true);
    }

    [Fact]
    public void All_access_becomes_each_permission_that_is_not_administrative()
    {
        AdministrationOnDesktop.Elsewhere(Set(PermissionRegistry.Wildcard), Registry, firstEnrolment: false)
            .ShouldBe([PatientsRead, LabStatusRead], ignoreOrder: true);
        AdministrationOnDesktop.Elsewhere(Set(PermissionRegistry.Wildcard), Registry, firstEnrolment: true)
            .ShouldBe([PlatformPermissions.DevicesManage, PatientsRead, LabStatusRead], ignoreOrder: true);
    }

    [Fact]
    public void The_setting_is_true_or_false_for_the_whole_hospital()
    {
        const string key = AdministrationOnDesktop.SettingKey;
        AdministrationOnDesktop.Problem(key, JsonValue.Create(false), null).ShouldBeNull();
        AdministrationOnDesktop.Problem(key, JsonValue.Create(" True "), null).ShouldBeNull();
        AdministrationOnDesktop.Problem(key, null, null).ShouldBeNull();
        AdministrationOnDesktop.Problem(key, JsonValue.Create("no"), null).ShouldBe("Enter true or false.");
        AdministrationOnDesktop.Problem(key, JsonValue.Create(0), null).ShouldBe("Enter true or false.");
        AdministrationOnDesktop.Problem(key, new JsonObject(), null).ShouldBe("Enter true or false.");
        AdministrationOnDesktop.Problem(key, JsonValue.Create(true), Guid.NewGuid()).ShouldNotBeNull().ShouldContain("whole hospital");
        AdministrationOnDesktop.Problem("ui.idleLockMinutes", JsonValue.Create("no"), Guid.NewGuid()).ShouldBeNull();
    }

    private static IReadOnlySet<string> Set(params string[] permissions) => permissions.ToHashSet(StringComparer.Ordinal);

    private void Holds(params string[] permissions) =>
        _permissions.GetPermissionsAsync(UserId, Arg.Any<CancellationToken>()).Returns(ValueTask.FromResult(Set(permissions)));

    private async Task<AuthorizationHandlerContext> AuthorizeAsync(ClaimsPrincipal user, string permission, HttpContext? resource = null) =>
        await AuthorizeAsync(user, [permission], resource);

    private async Task<AuthorizationHandlerContext> AuthorizeAsync(ClaimsPrincipal user, string first, string second) =>
        await AuthorizeAsync(user, [first, second], null);

    private async Task<AuthorizationHandlerContext> AuthorizeAsync(ClaimsPrincipal user, string[] anyOf, HttpContext? resource)
    {
        var context = new AuthorizationHandlerContext([new PermissionRequirement(anyOf)], user, resource);
        await new PermissionAuthorizationHandler(_permissions, _desktops).HandleAsync(context);
        return context;
    }

    private static ClaimsPrincipal Browser(params Claim[] more) =>
        new(new ClaimsIdentity([new(HcClaims.UserId, UserId.ToString()), .. more], "test"));

    private static ClaimsPrincipal Desktop(params Claim[] more) => Browser([new(HcClaims.Device, DeviceId.ToString()), .. more]);

    private static DefaultHttpContext Endpoint(params object[] metadata)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test endpoint"));
        return http;
    }
}
