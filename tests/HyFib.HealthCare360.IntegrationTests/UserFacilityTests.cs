using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// All facilities is a grant of its own (<see cref="User.AllFacilities"/>): an empty list of facilities is none. Before,
/// an empty list meant every facility, so an administrator who took away a nurse's last facility gave her every facility
/// at once, with no second administrator. Now that leaves her none, and every facility waits for a second administrator
/// like any other access given; taking it back to one facility applies at once.
/// </summary>
public sealed class UserFacilityTests(HospitalServer server)
{
    private static int _addresses;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Taking_away_a_users_last_facility_leaves_them_none_and_all_facilities_waits_for_a_second_administrator()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var main = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var name = "it-facilities-" + Guid.NewGuid().ToString("N")[..8];
        await server.CreateStaffAccountAsync(name, SystemRoles.Nurse);
        var path = $"/api/v1/admin/users/{(await admin.GetAsync<PagedResult<UserListItem>>($"/api/v1/admin/users?search={name}")).Items.Single().Id}";
        var user = await admin.GetAsync<UserDetail>(path);
        user.AllFacilities.ShouldBeFalse();
        user.FacilityIds.ShouldBe([main], "the test's accounts are created in the hospital administrator's facility");

        // Her last facility taken away: at once, and she then works in no facility, not in every one. A session without a
        // facility would read every facility's records, so she does not sign in at all.
        using (var narrowed = await admin.SendRawAsync(HttpMethod.Put, path, Update(user, [], all: false)))
        {
            narrowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await narrowed.Content.ReadAsStringAsync(Ct));
        }

        await NoFacilityAsync(name);

        // Every facility is a widening: it waits for a second administrator, and gives nothing until approved.
        user = await admin.GetAsync<UserDetail>(path);
        using var asking = await admin.SendRawAsync(HttpMethod.Put, path, Update(user, [], all: true));
        asking.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asking.Content.ReadAsStringAsync(Ct));
        var asked = (await asking.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
        asked.Kind.ShouldBe(GrantApprovals.UserGrantKind);
        asked.Details.ShouldContain(d => d.Label == "Facilities to give" && d.Value == "All facilities, those added later included");
        var waiting = await admin.GetAsync<UserDetail>(path);
        waiting.AllFacilities.ShouldBeFalse();
        waiting.PendingGrant.ShouldNotBeNull().AllFacilities.ShouldBeTrue();
        await NoFacilityAsync(name);

        // Approved by the other administrator, she works in every facility, the hospital administrator's among them.
        (await server.ApproveGrantAsync(asked.Id)).Status.ShouldBe(ApprovalStatus.Approved);
        var given = await admin.GetAsync<UserDetail>(path);
        given.AllFacilities.ShouldBeTrue();
        given.FacilityIds.ShouldBeEmpty();
        var everywhere = await (await server.SignInNewSessionAsync(name)).Client.GetAsync<AuthUser>("/api/v1/auth/me");
        everywhere.Facilities.Select(f => f.Id).ShouldContain(main);

        // Taken back to one facility: a narrowing, at once.
        using (var back = await admin.SendRawAsync(HttpMethod.Put, path, Update(given, [main], all: false)))
        {
            back.StatusCode.ShouldBe(HttpStatusCode.NoContent, await back.Content.ReadAsStringAsync(Ct));
        }

        var one = await admin.GetAsync<UserDetail>(path);
        one.AllFacilities.ShouldBeFalse();
        one.FacilityIds.ShouldBe([main]);
    }

    [Fact]
    public async Task All_facilities_and_a_list_of_some_are_not_both_given()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var main = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var nurse = (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).Single(r => r.Name == SystemRoles.Nurse).Id;

        using var response = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/users", new CreateUserRequest(
            "it-both-" + Guid.NewGuid().ToString("N")[..8], "Both", Email: null, Phone: null, [nurse], [main], main, PractitionerId: null,
            TemporaryPassword: null, AllFacilities: true));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(FacilityGrant.AllOrSomeMessage);
    }

    /// <summary>The account signs in with its password, from the enrolled workstation, and is refused: it has no facility.</summary>
    private async Task NoFacilityAsync(string name)
    {
        var (deviceId, secret) = server.Desktop;
        using var response = await server.AnonymousFrom(NewAddress()).SendRawAsync(HttpMethod.Post, "/api/v1/auth/login",
            new LoginRequest(name, server.Password, DeviceId: deviceId, DeviceSecret: secret));
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body);
        body.ShouldContain("auth.no_facility");
        body.ShouldNotContain("accessToken");
    }

    private static string NewAddress()
    {
        var n = Interlocked.Increment(ref _addresses);
        return $"10.249.{n / 250}.{(n % 250) + 1}";
    }

    private static UpdateUserRequest Update(UserDetail user, IReadOnlyList<Guid> facilities, bool all) => new(
        user.DisplayName, user.Email, user.Phone, user.RoleIds, facilities, user.DefaultFacilityId, user.PractitionerId, user.IsActive, user.Version,
        Reason: "Moves between the hospital's facilities.", AllFacilities: all);
}
