using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OtpNet;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Session controls over the real API (ASVS V7): the idle lock kept by the server, the end of a session nobody uses, the
/// limit on sessions per account, signing out other devices or everybody, and changes to two-step verification. Tests
/// that change an account's sessions use an account of their own, so the seeded accounts other tests use stay signed in.
/// </summary>
public sealed class SessionControlTests(HospitalServer server)
{
    private const HttpStatusCode Locked = (HttpStatusCode)423;

    // Any signed-in user may read it, with no permission needed: the lock is the server's, not only the token's.
    private const string AnyPage = "/api/v1/settings/public";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_locked_screen_opens_nothing_until_it_is_unlocked()
    {
        var (client, _) = await server.SignInNewSessionAsync(HospitalServer.Users.Billing);

        await client.PostAsync("/api/v1/auth/lock");

        using (var refused = await client.SendRawAsync(HttpMethod.Get, AnyPage))
        {
            refused.StatusCode.ShouldBe(Locked);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("auth.session_locked");
        }

        (await client.GetAsync<AuthUser>("/api/v1/auth/me")).Restriction.ShouldBe(AuthService.RestrictionLocked);
        using (var wrong = await client.SendRawAsync(HttpMethod.Post, "/api/v1/auth/unlock", new UnlockRequest(null, "not-the-password")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var unlocked = await client.PostAsync<AuthResult>("/api/v1/auth/unlock", new UnlockRequest(null, server.Password));

        unlocked.Restriction.ShouldBeNull();
        unlocked.AccessToken.ShouldNotBeNullOrEmpty();
        (await StatusOfAsync(client)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_server_locks_a_screen_nobody_has_used_even_when_the_app_did_not()
    {
        var (client, cookie) = await server.SignInNewSessionAsync(HospitalServer.Users.Pharmacist);
        await IdleAsync(client, TimeSpan.FromMinutes(15));

        // The app refreshes in the background, and says nobody has touched it for 15 minutes.
        using var refreshed = await server.RefreshAsync(cookie, idleSeconds: 15 * 60);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await refreshed.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct))!.Restriction.ShouldBe(AuthService.RestrictionLocked);
        (await StatusOfAsync(client)).ShouldBe(Locked);
    }

    [Fact]
    public async Task Using_the_app_keeps_the_screen_unlocked()
    {
        var (client, cookie) = await server.SignInNewSessionAsync(HospitalServer.Users.Pharmacist);
        await IdleAsync(client, TimeSpan.FromMinutes(9));

        // Someone pressed a key 20 seconds ago.
        using var refreshed = await server.RefreshAsync(cookie, idleSeconds: 20);

        (await refreshed.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct))!.Restriction.ShouldBeNull();
        (await StatusOfAsync(client)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_session_nobody_has_used_for_two_hours_ends_however_often_it_refreshes()
    {
        var (client, cookie) = await server.SignInNewSessionAsync(HospitalServer.Users.Lab);
        await IdleAsync(client, TimeSpan.FromMinutes(121));

        // The app has refreshed every few minutes in the background, and says nobody has touched it for 2 hours.
        using var refreshed = await server.RefreshAsync(cookie, idleSeconds: 121 * 60);

        refreshed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusOfAsync(client)).ShouldBe(HttpStatusCode.Unauthorized);
        (await RevokedReasonAsync(client)).ShouldBe("idle_timeout");
    }

    [Fact]
    public async Task An_app_still_open_from_before_the_upgrade_counts_each_refresh_as_use()
    {
        var (client, cookie) = await server.SignInNewSessionAsync(HospitalServer.Users.Lab);
        await IdleAsync(client, TimeSpan.FromMinutes(15));

        // It does not say how long nobody has used it: until the page is reloaded, it is not locked or ended for that.
        using var refreshed = await server.RefreshAsync(cookie);

        (await refreshed.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct))!.Restriction.ShouldBeNull();
        (await StatusOfAsync(client)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Signing_in_beyond_the_limit_signs_out_the_session_used_least_recently()
    {
        var userName = NewUserName("limit");
        await server.CreateStaffAccountAsync(userName, SystemRoles.Nurse);
        await SetMaxSessionsAsync(2);
        try
        {
            var (first, _) = await server.SignInNewSessionAsync(userName);
            var (second, _) = await server.SignInNewSessionAsync(userName);
            await IdleAsync(second, TimeSpan.FromMinutes(5));
            await IdleAsync(first, TimeSpan.FromMinutes(1));

            var (third, _) = await server.SignInNewSessionAsync(userName);

            (await StatusOfAsync(second)).ShouldBe(HttpStatusCode.Unauthorized);
            (await RevokedReasonAsync(second)).ShouldBe("session_limit");
            (await StatusOfAsync(first)).ShouldBe(HttpStatusCode.OK);
            (await StatusOfAsync(third)).ShouldBe(HttpStatusCode.OK);
            (await AuditCountAsync("auth.session_limit", userName)).ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await SetMaxSessionsAsync(0);
        }
    }

    [Fact]
    public async Task Signing_in_on_several_computers_at_once_keeps_to_the_limit()
    {
        var userName = NewUserName("burst");
        await server.CreateStaffAccountAsync(userName, SystemRoles.Nurse);
        await SetMaxSessionsAsync(2);
        try
        {
            // Six at once. Each also updates the account's row, whose version lets one at a time commit: a sign-in that
            // overlaps another is refused (409) rather than finding room under the limit alongside it.
            var signedIn = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
            {
                try
                {
                    await server.SignInNewSessionAsync(userName);
                    return true;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
                {
                    return false;
                }
            }));

            signedIn.ShouldContain(true);
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var userId = await db.Set<User>().Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(Ct);
            (await db.Set<UserSession>().CountAsync(s => s.UserId == userId && s.RevokedAt == null, Ct)).ShouldBeLessThanOrEqualTo(2);
        }
        finally
        {
            await SetMaxSessionsAsync(0);
        }
    }

    [Fact]
    public async Task A_server_that_cannot_hear_the_others_does_not_keep_a_session_active()
    {
        var (client, _) = await server.SignInNewSessionAsync(HospitalServer.Users.Pharmacist);
        var id = SessionIdOf(client);
        var hearing = Validator(listening: true);
        var deaf = Validator(listening: false);
        (await hearing.GetStateAsync(id, Ct)).ShouldBe(SessionState.Active);
        (await deaf.GetStateAsync(id, Ct)).ShouldBe(SessionState.Active);

        // Locked where neither cache hears of it (another server's change signals every server, SCAL-01).
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>()
                .Where(s => s.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LockedAt, DateTimeOffset.UtcNow), Ct);
        }

        // A server that hears the others is told of every change, so keeping "active" there is safe...
        (await hearing.GetStateAsync(id, Ct)).ShouldBe(SessionState.Active);

        // ...but one that cannot hear them keeps nothing active: the lock holds there at once.
        (await deaf.GetStateAsync(id, Ct)).ShouldBe(SessionState.Locked);
    }

    [Fact]
    public async Task Signing_out_other_devices_asks_for_the_password()
    {
        var userName = NewUserName("others");
        await server.CreateStaffAccountAsync(userName, SystemRoles.Nurse);
        var (here, _) = await server.SignInNewSessionAsync(userName);
        var (there, _) = await server.SignInNewSessionAsync(userName);
        var (elsewhere, _) = await server.SignInNewSessionAsync(userName);

        using (var wrong = await here.SendRawAsync(HttpMethod.Post, "/api/v1/auth/sessions/end-others", new ReauthenticateRequest("not-the-password")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await StatusOfAsync(there)).ShouldBe(HttpStatusCode.OK);

        // One device...
        await here.PostAsync($"/api/v1/auth/sessions/{SessionIdOf(there)}/end", new ReauthenticateRequest(server.Password));
        (await StatusOfAsync(there)).ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusOfAsync(elsewhere)).ShouldBe(HttpStatusCode.OK);

        // ...or all the others: the one left, and the session that set the account's password.
        var ended = await here.PostAsync<SessionsEnded>("/api/v1/auth/sessions/end-others", new ReauthenticateRequest(server.Password));

        ended.Ended.ShouldBe(2);
        (await StatusOfAsync(elsewhere)).ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusOfAsync(here)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Turning_on_two_step_verification_needs_the_password_and_signs_out_the_other_devices()
    {
        var userName = NewUserName("mfa");
        await server.CreateStaffAccountAsync(userName, SystemRoles.Nurse);
        var (here, _) = await server.SignInNewSessionAsync(userName);
        var (there, _) = await server.SignInNewSessionAsync(userName);

        using (var wrong = await here.SendRawAsync(HttpMethod.Post, "/api/v1/auth/mfa/setup", new MfaSetupRequest("not-the-password")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var setup = await here.PostAsync<MfaSetupResponse>("/api/v1/auth/mfa/setup", new MfaSetupRequest(server.Password));
        var code = new Totp(Base32Encoding.ToBytes(setup.Secret)).ComputeTotp(DateTime.UtcNow);
        var enabled = await here.PostAsync<AuthResult>("/api/v1/auth/mfa/enable", new MfaEnableRequest(code));

        enabled.User.ShouldNotBeNull().MfaEnabled.ShouldBeTrue();
        (await StatusOfAsync(there)).ShouldBe(HttpStatusCode.Unauthorized);
        (await RevokedReasonAsync(there)).ShouldBe("mfa_enabled");
        (await StatusOfAsync(here)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_administrator_can_sign_everybody_out_and_stays_signed_in()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using (var refused = await hospitalAdmin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/sessions/end-all", new ReauthenticateRequest(server.Password)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var (admin, _) = await server.SignInNewSessionAsync(HospitalServer.Users.Admin);
        var (nurse, _) = await server.SignInNewSessionAsync(HospitalServer.Users.Nurse);
        var patient = await server.SignInToPortalAsync("9" + Random.Shared.Next(100_000_000, 999_999_999));
        try
        {
            using (var wrong = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/sessions/end-all", new ReauthenticateRequest("not-the-password")))
            {
                wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            }

            var ended = await admin.PostAsync<SessionsEnded>("/api/v1/admin/sessions/end-all", new ReauthenticateRequest(server.Password));

            ended.Ended.ShouldBeGreaterThanOrEqualTo(3);
            (await StatusOfAsync(nurse)).ShouldBe(HttpStatusCode.Unauthorized);
            (await StatusOfAsync(patient, "/api/v1/portal/profiles")).ShouldBe(HttpStatusCode.Unauthorized);
            (await StatusOfAsync(hospitalAdmin)).ShouldBe(HttpStatusCode.Unauthorized);
            (await StatusOfAsync(admin)).ShouldBe(HttpStatusCode.OK);
            (await AuditCountAsync("auth.all_sessions_ended", HospitalServer.Users.Admin)).ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            // The sessions the other tests keep signed in ended too: they sign in again.
            server.ForgetSignIns();
        }
    }

    private static string NewUserName(string purpose) => $"it-{purpose}-{Guid.NewGuid():N}"[..24];

    private SessionValidator Validator(bool listening) => new(
        new MemoryCache(new MemoryCacheOptions()),
        server.Services.GetRequiredService<IServiceScopeFactory>(),
        TimeProvider.System,
        new FixedSignals(listening));

    private static async Task<HttpStatusCode> StatusOfAsync(ApiClient client, string page = AnyPage)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, page);
        return response.StatusCode;
    }

    private static Guid SessionIdOf(ApiClient client)
    {
        var jwt = client.Http.DefaultRequestHeaders.Authorization?.Parameter ?? throw new InvalidOperationException("The client has no token.");
        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]));
        return Guid.Parse(payload.RootElement.GetProperty("sid").GetString()!);
    }

    /// <summary>As if nobody had used the client's session for <paramref name="idle"/>.</summary>
    private async Task IdleAsync(ApiClient client, TimeSpan idle)
    {
        var id = SessionIdOf(client);
        var at = DateTimeOffset.UtcNow - idle;
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>()
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActiveAt, at), Ct);
    }

    private async Task<string?> RevokedReasonAsync(ApiClient client)
    {
        var id = SessionIdOf(client);
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>()
            .Where(s => s.Id == id)
            .Select(s => s.RevokedReason)
            .SingleAsync(Ct);
    }

    private async Task<int> AuditCountAsync(string action, string userName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>()
            .CountAsync(e => e.Action == action && e.UserName == userName, Ct);
    }

    private async Task SetMaxSessionsAsync(int max)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(AuthService.MaxSessionsSetting, max, cancellationToken: Ct);
    }

    /// <summary>Cache signals a server hears, or not, and that never arrive: as if another server changed the session.</summary>
    private sealed class FixedSignals(bool listening) : IClusterSignals
    {
        public bool IsListening => listening;

        public void Publish(string topic, string? argument = null)
        {
        }
    }
}
