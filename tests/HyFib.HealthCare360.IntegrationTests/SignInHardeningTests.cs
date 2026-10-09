using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OtpNet;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Sign-in hardening over the real API (ASVS V6): wrong authenticator codes count against the account like wrong
/// passwords, failures are forgotten after a day, temporary passwords expire, the account holder hears of every change to
/// how they sign in, and holders of administrative permissions must use two-step verification. Each test uses accounts
/// of its own, and each sign-in attempt comes from an address of its own, so the per-address limit does not interfere.
/// </summary>
public sealed class SignInHardeningTests(HospitalServer server)
{
    private static int _addresses;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Wrong_authenticator_codes_lock_the_account_whichever_sign_in_they_come_in()
    {
        const string name = "it-code-guess";
        await server.CreateStaffAccountAsync(name, SystemRoles.Billing);
        var (client, _) = await server.SignInNewSessionAsync(name);
        var setup = await client.PostAsync<MfaSetupResponse>("/api/v1/auth/mfa/setup", new MfaSetupRequest(server.Password));
        await client.PostAsync<AuthResult>("/api/v1/auth/mfa/enable", new MfaEnableRequest(CodeAt(setup.Secret, DateTime.UtcNow)));

        // Three wrong codes in one sign-in and two in the next: five in a row.
        var first = await StartSignInAsync(name);
        for (var i = 0; i < 3; i++)
        {
            (await VerifyAsync(first, WrongCode(setup.Secret))).ShouldBe(HttpStatusCode.BadRequest);
        }

        var second = await StartSignInAsync(name);
        for (var i = 0; i < 2; i++)
        {
            (await VerifyAsync(second, WrongCode(setup.Secret))).ShouldBe(HttpStatusCode.BadRequest);
        }

        // Locked: the right code no longer signs in, and neither does the password.
        (await VerifyAsync(second, CodeAt(setup.Secret, DateTime.UtcNow.AddSeconds(30)))).ShouldBe(HttpStatusCode.Unauthorized);
        using (var password = await LogInAsync(name, server.Password))
        {
            password.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var user = await UserAsync(name);
        user.FailedLoginCount.ShouldBe(5);
        user.LockoutEndsAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        (await AuditAsync(user.Id, "auth.mfa_failed")).Count(e => e.Severity == AuditSeverity.Critical).ShouldBe(1);
    }

    [Fact]
    public async Task Wrong_passwords_sent_at_once_each_count()
    {
        const string name = "it-password-burst";
        await server.CreateStaffAccountAsync(name, SystemRoles.Billing);

        // Ten at once, as from ten machines: each reads the account before any of the others is counted.
        var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var response = await LogInAsync(name, "not-the-password");
            return response.StatusCode;
        }));

        // The fifth locks the account, and the rest are answered as for a locked account.
        answers.ShouldAllBe(a => a == HttpStatusCode.Unauthorized);
        var user = await UserAsync(name);
        user.FailedLoginCount.ShouldBe(5);
        user.LockoutEndsAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        (await AuditAsync(user.Id, "auth.login_failed")).Count.ShouldBe(5);
    }

    [Fact]
    public async Task Wrong_codes_sent_at_once_each_count()
    {
        const string name = "it-code-burst";
        await server.CreateStaffAccountAsync(name, SystemRoles.Billing);
        var (client, _) = await server.SignInNewSessionAsync(name);
        var setup = await client.PostAsync<MfaSetupResponse>("/api/v1/auth/mfa/setup", new MfaSetupRequest(server.Password));
        await client.PostAsync<AuthResult>("/api/v1/auth/mfa/enable", new MfaEnableRequest(CodeAt(setup.Secret, DateTime.UtcNow)));

        // Ten sign-ins with the password, then a wrong code for each, all at once.
        var challenges = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            challenges.Add(await StartSignInAsync(name));
        }

        var answers = await Task.WhenAll(challenges.Select(c => VerifyAsync(c, WrongCode(setup.Secret))));

        // Five are told the code is wrong, the fifth locking the account; the rest find the account locked.
        answers.Count(a => a == HttpStatusCode.BadRequest).ShouldBe(5);
        answers.Count(a => a == HttpStatusCode.Unauthorized).ShouldBe(5);
        var user = await UserAsync(name);
        user.FailedLoginCount.ShouldBe(5);
        user.LockoutEndsAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Failures_are_forgotten_after_a_day_without_another()
    {
        const string name = "it-forgotten";
        await server.CreateStaffAccountAsync(name, SystemRoles.Billing);

        // Four failures more than a day ago: a fifth starts the count again.
        await SetFailuresAsync(name, 4, DateTimeOffset.UtcNow.AddHours(-25));
        using (var wrong = await LogInAsync(name, "not-the-password"))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var forgotten = await UserAsync(name);
        (forgotten.FailedLoginCount, forgotten.LockoutEndsAt).ShouldBe((1, null));

        // Four within the hour: the fifth locks the account.
        await SetFailuresAsync(name, 4, DateTimeOffset.UtcNow.AddHours(-1));
        using (var wrong = await LogInAsync(name, "not-the-password"))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var locked = await UserAsync(name);
        locked.FailedLoginCount.ShouldBe(5);
        locked.LockoutEndsAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_temporary_password_stops_working_when_it_expires()
    {
        const string name = "it-temporary";
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await CreateUserAsync(admin, name, SystemRoles.Billing, email: null);
        var options = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.Security;

        created.ExpiresAt.ShouldBe(DateTimeOffset.UtcNow.AddHours(options.TemporaryPasswordHours), TimeSpan.FromMinutes(1));

        await ExpireTemporaryPasswordAsync(name);
        using (var expired = await LogInAsync(name, created.TemporaryPassword))
        {
            expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await expired.Content.ReadAsStringAsync(Ct)).ShouldContain("auth.temporary_password_expired");
        }

        // An administrator gives out a new one, which works.
        var reset = await admin.PostAsync<TemporaryPasswordResponse>($"/api/v1/admin/users/{created.UserId}/reset-password");
        using var signedIn = await LogInAsync(name, reset.TemporaryPassword);
        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await signedIn.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct))!.Restriction.ShouldBe(AuthService.RestrictionPasswordChange);
    }

    [Fact]
    public async Task The_account_holder_hears_of_every_change_to_how_they_sign_in()
    {
        const string name = "it-notices";
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await CreateUserAsync(admin, name, SystemRoles.Billing, email: "it-notices@example.test");

        // The first sign-in replaces the temporary password.
        using (var first = await LogInAsync(name, created.TemporaryPassword))
        {
            var restricted = (await first.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct))!.AccessToken;
            using var change = await ChangePasswordAsync(restricted!, created.TemporaryPassword, server.Password);
            change.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await admin.PostAsync<TemporaryPasswordResponse>($"/api/v1/admin/users/{created.UserId}/reset-password");

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var inApp = await db.Set<UserNotification>().AsNoTracking().Where(n => n.UserId == created.UserId).OrderBy(n => n.CreatedAt).Select(n => n.Title).ToListAsync(Ct);
        inApp.ShouldBe([CredentialNotices.PasswordChanged, CredentialNotices.PasswordReset]);
        var emails = await db.Set<NotificationLog>().AsNoTracking()
            .Where(l => l.TemplateKey == CredentialNotices.TemplateKey && l.Recipient != null && l.Recipient.EndsWith("@example.test"))
            .ToListAsync(Ct);
        emails.Count.ShouldBe(2);
        emails.ShouldAllBe(l => l.Channel == NotificationChannel.Email);
    }

    [Fact]
    public async Task An_account_without_an_e_mail_address_is_told_in_the_app_only()
    {
        const string name = "it-no-email";
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var created = await CreateUserAsync(admin, name, SystemRoles.Billing, email: null);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var before = await db.Set<NotificationLog>().CountAsync(l => l.TemplateKey == CredentialNotices.TemplateKey, Ct);

        await admin.PostAsync<TemporaryPasswordResponse>($"/api/v1/admin/users/{created.UserId}/reset-password");

        (await db.Set<UserNotification>().CountAsync(n => n.UserId == created.UserId, Ct)).ShouldBe(1);
        (await db.Set<NotificationLog>().CountAsync(l => l.TemplateKey == CredentialNotices.TemplateKey, Ct)).ShouldBe(before);
    }

    [Fact]
    public async Task Holders_of_administrative_permissions_must_use_two_step_verification()
    {
        // Made while the test server asks nobody for two-step verification.
        var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
        await superAdmin.PostAsync<RoleDetail>("/api/v1/admin/roles", new SaveRoleRequest(
            "IT audit reviewers", "Reads the audit trail", [PlatformPermissions.AuditRead], Version: null));
        await server.CreateStaffAccountAsync("it-auditor", "IT audit reviewers");
        await server.CreateStaffAccountAsync("it-accountant", SystemRoles.Accountant);
        await server.CreateStaffAccountAsync("it-hr-manager", SystemRoles.HrManager);
        await server.CreateStaffAccountAsync("it-billing-desk", SystemRoles.Billing);

        // With the server's own settings (appsettings.json), not only the defaults in code.
        var security = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value.Security;
        security.EnforceMfa = true;
        try
        {
            (await RestrictionAfterSignInAsync("it-auditor")).ShouldBe(AuthService.RestrictionMfaSetup);
            (await RestrictionAfterSignInAsync("it-accountant")).ShouldBe(AuthService.RestrictionMfaSetup);
            (await RestrictionAfterSignInAsync("it-hr-manager")).ShouldBe(AuthService.RestrictionMfaSetup);
            (await RestrictionAfterSignInAsync("it-billing-desk")).ShouldBeNull();
        }
        finally
        {
            security.EnforceMfa = false;
        }
    }

    private async Task<string?> RestrictionAfterSignInAsync(string name)
    {
        using var response = await LogInAsync(name, server.Password);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuthResult>(ApiClient.Json, Ct))!.Restriction;
    }

    private async Task<TemporaryPasswordResponse> CreateUserAsync(ApiClient admin, string name, string role, string? email)
    {
        var roleId = (await admin.GetAsync<List<RoleListItem>>("/api/v1/admin/roles")).Single(r => r.Name == role).Id;
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId;
        return await admin.PostAsync<TemporaryPasswordResponse>("/api/v1/admin/users", new CreateUserRequest(
            name, $"Test {name}", email, Phone: null, [roleId], facilityId is { } f ? [f] : [], facilityId, PractitionerId: null, TemporaryPassword: null));
    }

    private async Task<Guid> StartSignInAsync(string name)
    {
        using var response = await LogInAsync(name, server.Password);
        var result = await ApiClient.ReadAsync<AuthResult>(response, HttpMethod.Post, "/api/v1/auth/login");
        result.MfaRequired.ShouldBeTrue();
        return result.ChallengeId!.Value;
    }

    private async Task<HttpStatusCode> VerifyAsync(Guid challenge, string code)
    {
        using var response = await server.AnonymousFrom(NewAddress()).SendRawAsync(HttpMethod.Post, "/api/v1/auth/mfa", new MfaVerifyRequest(challenge, code));
        return response.StatusCode;
    }

    private Task<HttpResponseMessage> LogInAsync(string name, string password) =>
        server.AnonymousFrom(NewAddress()).SendRawAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(name, password));

    private Task<HttpResponseMessage> ChangePasswordAsync(string restrictedToken, string current, string next)
    {
        var client = server.AnonymousFrom(NewAddress());
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", restrictedToken);
        return client.SendRawAsync(HttpMethod.Post, "/api/v1/auth/change-password", new ChangePasswordRequest(current, next));
    }

    private static string CodeAt(string secret, DateTime at) => new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at);

    // A code for ten minutes from now: never one the server accepts now.
    private static string WrongCode(string secret) => CodeAt(secret, DateTime.UtcNow.AddMinutes(10));

    private static string NewAddress()
    {
        var n = Interlocked.Increment(ref _addresses);
        return $"10.251.{n / 250}.{(n % 250) + 1}";
    }

    private async Task<User> UserAsync(string name)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>().AsNoTracking().SingleAsync(u => u.NormalizedUserName == name, Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid userId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.UserId == userId && e.Action == action)
            .ToListAsync(Ct);
    }

    private async Task SetFailuresAsync(string name, int count, DateTimeOffset lastAt)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>()
            .Where(u => u.NormalizedUserName == name)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.FailedLoginCount, count)
                .SetProperty(x => x.LastFailedLoginAt, lastAt)
                .SetProperty(x => x.LockoutEndsAt, (DateTimeOffset?)null), Ct);
    }

    private async Task ExpireTemporaryPasswordAsync(string name)
    {
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>()
            .Where(u => u.NormalizedUserName == name)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.TemporaryPasswordExpiresAt, past), Ct);
    }
}
