using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The key the staff app encrypts drafts with on the computer (SRS DED-10, audit P10). A user has one, the same in each of
/// their sessions, so a draft typed before a crash or an outage opens again once they sign in again. Someone else signed
/// in on the same Windows account has a key of their own. A session that is locked, or must first change its password,
/// is given none.
/// </summary>
public sealed class DraftKeyTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_user_has_one_draft_key_in_every_session_and_nobody_else_has_it()
    {
        var nurse = await NewAccountAsync();
        var colleague = await NewAccountAsync();

        var signedIn = await server.Anonymous(ownAddress: true).PostAsync<AuthResult>("/api/v1/auth/login", new LoginRequest(nurse, server.Password));
        var (refreshed, next) = await RefreshAsync((await server.SignInNewSessionAsync(nurse)).RefreshCookie);
        var (again, _) = await RefreshAsync(next);
        var (theirs, _) = await RefreshAsync((await server.SignInNewSessionAsync(colleague)).RefreshCookie);

        Convert.FromBase64String(signedIn.DraftKey.ShouldNotBeNull()).Length.ShouldBe(32);
        refreshed.DraftKey.ShouldBe(signedIn.DraftKey);
        again.DraftKey.ShouldBe(signedIn.DraftKey);
        theirs.DraftKey.ShouldNotBeNull().ShouldNotBe(signedIn.DraftKey);
    }

    [Fact]
    public async Task A_locked_session_is_given_no_draft_key_until_it_is_unlocked()
    {
        var nurse = await NewAccountAsync();
        var (client, cookie) = await server.SignInNewSessionAsync(nurse);
        var (before, next) = await RefreshAsync(cookie);

        await client.PostAsync("/api/v1/auth/lock");
        var (locked, _) = await RefreshAsync(next);
        var unlocked = await client.PostAsync<AuthResult>("/api/v1/auth/unlock", new UnlockRequest(null, server.Password));

        locked.Restriction.ShouldBe(AuthService.RestrictionLocked);
        locked.DraftKey.ShouldBeNull();
        unlocked.DraftKey.ShouldNotBeNull().ShouldBe(before.DraftKey);
    }

    [Fact]
    public async Task An_account_that_must_first_change_its_password_is_given_no_draft_key()
    {
        var nurse = await NewAccountAsync();
        var (_, cookie) = await server.SignInNewSessionAsync(nurse);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>()
                .Where(u => u.UserName == nurse)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.MustChangePassword, true), Ct);
        }

        var (refreshed, _) = await RefreshAsync(cookie);

        refreshed.Restriction.ShouldBe(AuthService.RestrictionPasswordChange);
        refreshed.AccessToken.ShouldNotBeNullOrEmpty();
        refreshed.DraftKey.ShouldBeNull();
    }

    private async Task<string> NewAccountAsync()
    {
        var userName = $"it-drafts-{Guid.NewGuid():N}"[..24];
        await server.CreateStaffAccountAsync(userName, SystemRoles.Nurse);
        return userName;
    }

    /// <summary>A refresh as the staff app does it: what the server answered, and the refresh cookie to use next.</summary>
    private async Task<(AuthResult Result, string Cookie)> RefreshAsync(string cookie)
    {
        using var response = await server.RefreshAsync(cookie);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct)).ShouldNotBeNull();
        return (result, HospitalServer.RefreshCookieOf(response) ?? cookie);
    }
}
