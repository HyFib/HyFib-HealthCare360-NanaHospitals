using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS SEC-01 over the real API: a password published from data breaches cannot be chosen, and an existing one found at
/// sign-in has to be changed before anything else; the staff app is told why.
/// </summary>
public sealed class BreachedPasswordTests(HospitalServer server)
{
    private const string Breached = "justinbieber";

    [Fact]
    public async Task A_breached_password_found_at_sign_in_must_be_changed_first()
    {
        var userName = "it-breach-" + Guid.NewGuid().ToString("N")[..8];
        var facility = await CreateStaffWithPasswordAsync(userName, Breached);
        var client = server.Anonymous(ownAddress: true);

        var signIn = await client.PostAsync<AuthResult>("/api/v1/auth/login", new LoginRequest(userName, Breached));

        signIn.Restriction.ShouldBe(AuthService.RestrictionPasswordChange);
        signIn.User.ShouldNotBeNull().PasswordBreached.ShouldBeTrue();
        signIn.User.Permissions.ShouldBeEmpty();
        (await AuditActionsAsync(userName)).ShouldContain("auth.password_breached");
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signIn.AccessToken);

        // Another breached password, or the hospital's own name, is refused as the new one.
        (await RefusedAsync(client, new ChangePasswordRequest(Breached, "Telechargement")))
            .ShouldContain("published from a data breach");
        (await RefusedAsync(client, new ChangePasswordRequest(Breached, $"{facility.Replace(" ", "", StringComparison.Ordinal)}-ward-7")))
            .ShouldContain("the hospital's name");

        var changed = await client.PostAsync<AuthResult>("/api/v1/auth/change-password",
            new ChangePasswordRequest(Breached, "Kettle sings on ward 7 at four"));

        changed.Restriction.ShouldBeNull();
        changed.User.ShouldNotBeNull().PasswordBreached.ShouldBeFalse();
        await using var scope = server.Services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>()
            .SingleAsync(u => u.UserName == userName, TestContext.Current.CancellationToken);
        (user.MustChangePassword, user.PasswordBreachedAt).ShouldBe((false, null));
    }

    [Fact]
    public async Task An_administrator_cannot_give_a_breached_temporary_password()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var me = await admin.GetAsync<AuthUser>("/api/v1/auth/me");
        var role = (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).First(r => r.Name == SystemRoles.FrontOffice);

        using var response = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/admin/users", new CreateUserRequest(
            "it-breach-admin", "Breach Test", Email: null, Phone: null, [role.Id], [me.FacilityId!.Value], me.FacilityId, PractitionerId: null, Breached));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiClient.Json, TestContext.Current.CancellationToken))!
            .Errors["temporaryPassword"].ShouldContain(e => e.Contains("published from a data breach", StringComparison.Ordinal));
    }

    /// <summary>A member of staff whose password was set before HC360 checked passwords against breaches.</summary>
    private async Task<string> CreateStaffWithPasswordAsync(string userName, string password)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var me = await admin.GetAsync<AuthUser>("/api/v1/auth/me");
        var role = (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).First(r => r.Name == SystemRoles.FrontOffice);
        await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", new CreateUserRequest(
            userName, "Breach Test", Email: null, Phone: null, [role.Id], [me.FacilityId!.Value], me.FacilityId, PractitionerId: null, null));

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var user = await db.Set<User>().SingleAsync(u => u.UserName == userName, TestContext.Current.CancellationToken);
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<PasswordHashing>().Hash(password);
        user.MustChangePassword = false;
        user.PasswordChangedAt = DateTimeOffset.UtcNow.AddYears(-1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return me.FacilityName ?? throw new InvalidOperationException("The hospital administrator has no facility.");
    }

    private async Task<List<string>> AuditActionsAsync(string userName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>()
            .Where(a => a.UserName == userName)
            .Select(a => a.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> RefusedAsync(ApiClient client, ChangePasswordRequest request)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, "/api/v1/auth/change-password", request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(ApiClient.Json, TestContext.Current.CancellationToken);
        return string.Join(" ", problem!.Errors["newPassword"]);
    }
}
