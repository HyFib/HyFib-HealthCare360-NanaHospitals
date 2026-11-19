using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// All facilities is a grant of its own (<see cref="User.AllFacilities"/>): an empty list of facilities is none. Before,
/// an empty list meant every facility, so an administrator who took away a nurse's last facility gave her every facility
/// at once, with no second administrator. Now that leaves her none, and every facility waits for a second administrator
/// like any other access given; taking it back to one facility applies at once.
/// A facility taken away, or taken out of use, applies at once to the sessions open in it as well: before, they went on
/// working there, and were renewed, for the rest of their 12 hours.
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

        // Taken back to one facility: a narrowing, at once. Read again first, as the screen would show her: signing in wrote
        // her last sign-in to the account, so the version read before it is out of date.
        var signedIn = await admin.GetAsync<UserDetail>(path);
        signedIn.AllFacilities.ShouldBeTrue();
        using (var back = await admin.SendRawAsync(HttpMethod.Put, path, Update(signedIn, [main], all: false)))
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

    [Fact]
    public async Task A_facility_taken_away_ends_the_users_session_there_at_once_and_her_session_in_another_goes_on()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var main = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var annexe = await AnnexeInUseAsync(admin);
        try
        {
            var (name, path) = await NurseInBothAsync(admin, main, annexe);
            var (atMain, mainCookie) = await server.SignInNewSessionAsync(name);
            var (atAnnexe, annexeCookie) = await SignInToAsync(name, annexe);
            (await atMain.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldBe(main);

            // The annexe taken away: a narrowing, at once.
            using (var narrowed = await admin.SendRawAsync(HttpMethod.Put, path, Update(await admin.GetAsync<UserDetail>(path), [main], all: false)))
            {
                narrowed.StatusCode.ShouldBe(HttpStatusCode.NoContent, await narrowed.Content.ReadAsStringAsync(Ct));
            }

            // Her session at the annexe stops on its next request, and is not renewed.
            (await StatusOfAsync(atAnnexe)).ShouldBe(HttpStatusCode.Unauthorized);
            using (var refused = await server.RefreshAsync(annexeCookie))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            (await RevokedReasonAsync(atAnnexe)).ShouldBe("facility_removed");

            // Her session at the main hospital, which she still works in, goes on and is renewed.
            (await StatusOfAsync(atMain)).ShouldBe(HttpStatusCode.OK);
            using var renewed = await server.RefreshAsync(mainCookie);
            renewed.StatusCode.ShouldBe(HttpStatusCode.OK, await renewed.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            await TakeOutOfUseAsync(admin, annexe);
        }
    }

    [Fact]
    public async Task A_session_is_not_renewed_in_a_facility_the_user_no_longer_works_in_or_one_out_of_use()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var main = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var annexe = await AnnexeInUseAsync(admin);
        try
        {
            var (name, _) = await NurseInBothAsync(admin, main, annexe);

            // The annexe taken from her where no save of her account ends her session there (straight in the database, as
            // a session opened before this release would find it): the session's next refresh ends it.
            var (first, firstCookie) = await SignInToAsync(name, annexe);
            await WorksInAsync(name, main);
            using (var refused = await server.RefreshAsync(firstCookie))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            (await StatusOfAsync(first)).ShouldBe(HttpStatusCode.Unauthorized);
            (await RevokedReasonAsync(first)).ShouldBe("facility_removed");

            // Given back to her, but taken out of use the same way: a session there is not renewed either.
            await WorksInAsync(name, main, annexe);
            var (second, secondCookie) = await SignInToAsync(name, annexe);
            await InUseAsync(annexe, false);
            using (var notRenewed = await server.RefreshAsync(secondCookie))
            {
                notRenewed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            (await RevokedReasonAsync(second)).ShouldBe("facility_inactive");
        }
        finally
        {
            await TakeOutOfUseAsync(admin, annexe);
        }
    }

    [Fact]
    public async Task A_facility_taken_out_of_use_ends_the_sessions_open_in_it_at_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var main = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var annexe = await AnnexeInUseAsync(admin);
        try
        {
            var (name, _) = await NurseInBothAsync(admin, main, annexe);
            var (atAnnexe, annexeCookie) = await SignInToAsync(name, annexe);

            await TakeOutOfUseAsync(admin, annexe);

            (await StatusOfAsync(atAnnexe)).ShouldBe(HttpStatusCode.Unauthorized);
            using (var refused = await server.RefreshAsync(annexeCookie))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            }

            (await RevokedReasonAsync(atAnnexe)).ShouldBe("facility_inactive");
            (await StatusOfAsync(admin)).ShouldBe(HttpStatusCode.OK, "the administrator, at the main hospital, works on");
        }
        finally
        {
            await TakeOutOfUseAsync(admin, annexe);
        }
    }

    /// <summary>
    /// A new facility of the hospital, in use, so staff can sign in to it. The test takes it out of use when it ends: a
    /// facility in use is every facility's user's to work in.
    /// </summary>
    private static async Task<Guid> AnnexeInUseAsync(ApiClient admin)
    {
        var code = string.Concat(Enumerable.Range(0, 6).Select(_ => (char)('A' + Random.Shared.Next(26))));
        return (await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            code, "Nana Hospitals Annexe " + code, null, new Address { City = "Chennai", PinCode = "600002" },
            null, null, null, null, null, null, null, null, null, null, IsActive: true, null))).Id;
    }

    /// <summary>Takes the facility out of use from Administration › Masters, as an administrator signed in elsewhere does.</summary>
    private static async Task TakeOutOfUseAsync(ApiClient admin, Guid facilityId)
    {
        var f = await admin.GetAsync<FacilityDto>($"/api/v1/masters/facilities/{facilityId}");
        if (f.IsActive)
        {
            await admin.PutAsync($"/api/v1/masters/facilities/{facilityId}", new SaveFacilityRequest(
                f.Code, f.Name, f.LegalName, f.Address, f.Phone, f.Email, f.Website, f.Gstin, f.Pan, f.RegistrationNumber, f.NabhNumber, f.HfrId,
                f.LetterheadNote, f.LogoFileId, IsActive: false, f.Version));
        }
    }

    /// <summary>A new nurse who works at the main hospital and, given by the other administrator, at the annexe too.</summary>
    private async Task<(string Name, string Path)> NurseInBothAsync(ApiClient admin, Guid main, Guid annexe)
    {
        var name = "it-annexe-" + Guid.NewGuid().ToString("N")[..8];
        await server.CreateStaffAccountAsync(name, SystemRoles.Nurse);
        var path = $"/api/v1/admin/users/{(await admin.GetAsync<PagedResult<UserListItem>>($"/api/v1/admin/users?search={name}")).Items.Single().Id}";
        await server.UpdateAndApproveAsync(admin, path, Update(await admin.GetAsync<UserDetail>(path), [main, annexe], all: false));
        (await admin.GetAsync<UserDetail>(path)).FacilityIds.ShouldBe([main, annexe], ignoreOrder: true);
        return (name, path);
    }

    /// <summary>A new session of the account, signed in at its default facility and switched to <paramref name="facilityId"/>.</summary>
    private async Task<(ApiClient Client, string RefreshCookie)> SignInToAsync(string name, Guid facilityId)
    {
        var (client, cookie) = await server.SignInNewSessionAsync(name);
        var switched = await client.PostAsync<AuthResult>("/api/v1/auth/switch-facility", new SwitchFacilityRequest(facilityId));
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", switched.AccessToken.ShouldNotBeNull());
        (await client.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldBe(facilityId);
        return (client, cookie);
    }

    /// <summary>The account's facilities set straight in the database, where no save of the account ends a session.</summary>
    private async Task WorksInAsync(string name, params Guid[] facilities)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var userId = await db.Set<User>().Where(u => u.UserName == name).Select(u => u.Id).SingleAsync(Ct);
        await db.Set<UserFacility>().Where(f => f.UserId == userId).ExecuteDeleteAsync(Ct);
        db.AddRange(facilities.Select(f => new UserFacility { UserId = userId, FacilityId = f }));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The facility put in or out of use straight in the database, where no save of the facility ends a session.</summary>
    private async Task InUseAsync(Guid facilityId, bool inUse)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Facility>().Where(f => f.Id == facilityId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsActive, inUse), Ct)).ShouldBe(1);
    }

    private static async Task<HttpStatusCode> StatusOfAsync(ApiClient client)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, "/api/v1/auth/me");
        return response.StatusCode;
    }

    private async Task<string?> RevokedReasonAsync(ApiClient client)
    {
        var jwt = client.Http.DefaultRequestHeaders.Authorization?.Parameter ?? throw new InvalidOperationException("The client has no token.");
        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]));
        var sessionId = Guid.Parse(payload.RootElement.GetProperty("sid").GetString()!);
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>()
            .Where(s => s.Id == sessionId)
            .Select(s => s.RevokedReason)
            .SingleAsync(Ct);
    }

    private sealed record Created(Guid Id);

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
