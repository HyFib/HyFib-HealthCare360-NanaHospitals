using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
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
    public async Task Wrong_portal_codes_sent_at_once_each_count()
    {
        // A portal account has no lock, so each code's five tries are what stop guessing.
        var phone = NewMobile();
        OtpChallengeResponse challenge;
        using (var requested = await OnPortal().SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/request", new OtpRequest(phone)))
        {
            challenge = await ApiClient.ReadAsync<OtpChallengeResponse>(requested, HttpMethod.Post, "/api/v1/auth/otp/request");
        }

        var code = await server.PortalCodeAsync(phone);
        var wrong = ((int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

        // Ten wrong codes at once, as from ten machines: each reads the challenge before any of the others is counted.
        var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var response = await OnPortal().SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/verify", new OtpVerifyRequest(challenge.ChallengeId, wrong));
            return response.StatusCode;
        }));

        // Five are told the code is wrong; the rest, and then the right code itself, find the challenge used up.
        answers.Count(a => a == HttpStatusCode.BadRequest).ShouldBe(5);
        answers.Count(a => a == HttpStatusCode.Unauthorized).ShouldBe(5);
        using (var right = await OnPortal().SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/verify", new OtpVerifyRequest(challenge.ChallengeId, code)))
        {
            right.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        await using var scope = server.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuthChallenge>().AsNoTracking()
            .SingleAsync(c => c.Id == challenge.ChallengeId, Ct);
        (stored.Attempts, stored.ConsumedAt).ShouldBe((5, null));
    }

    [Fact]
    public async Task Portal_codes_asked_for_at_once_stay_within_three_per_number()
    {
        // Each code sent is five more guesses, so a number gets three in 15 minutes, however the requests arrive.
        var phone = NewMobile();
        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await OnPortal().SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/request", new OtpRequest(phone));
            return response.StatusCode;
        }));

        answers.Count(a => a == HttpStatusCode.OK).ShouldBe(3);
        answers.Count(a => a == HttpStatusCode.UnprocessableEntity).ShouldBe(5);
        await using var scope = server.Services.CreateAsyncScope();
        var normalized = Phone.Normalize(phone);
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuthChallenge>()
            .CountAsync(c => c.Phone == normalized, Ct)).ShouldBe(3);
    }

    [Fact]
    public async Task A_number_refused_another_code_is_told_when_it_may_have_one()
    {
        // In the production-style run the portal said "ask again in 05:06" whatever the server's 15 minutes said.
        var phone = NewMobile();
        for (var i = 0; i < 3; i++)
        {
            using var sent = await OnPortal().SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/request", new OtpRequest(phone));
            sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        using var refused = await OnPortal().SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/request", new OtpRequest(phone));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("auth.otp_throttled");

        // Until the first of the three leaves the 15 minutes.
        await using var scope = server.Services.CreateAsyncScope();
        var normalized = Phone.Normalize(phone);
        var first = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuthChallenge>()
            .Where(c => c.Phone == normalized).MinAsync(c => c.CreatedAt, Ct);
        var expected = (first.AddMinutes(15) - DateTimeOffset.UtcNow).TotalSeconds;
        problem.GetProperty("data").GetProperty("retryAfterSeconds").GetInt32().ShouldBeInRange((int)expected - 5, (int)expected + 5);
    }

    [Fact]
    public async Task An_enrolment_code_sent_twice_at_once_enrols_one_device()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var code = await admin.PostAsync<EnrollmentCodeResponse>("/api/v1/admin/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(DeviceKind.Workstation, null, "Enrolment race", 30));

        var name = "Race PC " + Guid.NewGuid().ToString("N")[..8];
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(async i =>
        {
            using var response = await server.AnonymousFrom(NewAddress()).SendRawAsync(HttpMethod.Post, "/api/v1/devices/enroll",
                new EnrollDeviceRequest(code.Code, $"{name} {i}", null, "Windows 11", "1.0.0"));
            return response.StatusCode;
        }));

        answers.Count(a => a == HttpStatusCode.OK).ShouldBe(1);
        answers.Count(a => a == HttpStatusCode.UnprocessableEntity).ShouldBe(3);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var hash = SecretTokens.Hash(code.Code.Replace("-", string.Empty, StringComparison.Ordinal));
        var used = await db.Set<DeviceEnrollmentCode>().AsNoTracking().SingleAsync(c => c.CodeHash == hash, Ct);
        (await db.Set<Device>().CountAsync(d => d.Name.StartsWith(name), Ct)).ShouldBe(1);
        used.UsedByDeviceId.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_administrators_password_reset_clears_the_lock_screen_PIN()
    {
        const string name = "it-pin-reset";
        await server.CreateStaffAccountAsync(name, SystemRoles.Billing);
        var (client, _) = await server.SignInNewSessionAsync(name);
        await client.PostAsync("/api/v1/auth/pin", new SetPinRequest("2580", server.Password));
        (await UserAsync(name)).PinHash.ShouldNotBeNull();

        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        await admin.PostAsync<TemporaryPasswordResponse>($"/api/v1/admin/users/{(await UserAsync(name)).Id}/reset-password");

        // Whoever may know the old password may know the PIN: until the holder sets a new one, the password unlocks.
        (await UserAsync(name)).PinHash.ShouldBeNull();
    }

    [Fact]
    public async Task Wrong_PINs_sent_at_once_each_count()
    {
        const string name = "it-pin-guess";
        await server.CreateStaffAccountAsync(name, SystemRoles.Billing);
        var (client, _) = await server.SignInNewSessionAsync(name);
        await client.PostAsync("/api/v1/auth/pin", new SetPinRequest("2580", server.Password));
        await client.PostAsync("/api/v1/auth/lock");

        // Ten wrong PINs at once, as a script on the locked workstation could send them: each reads the session before any
        // of the others is counted.
        var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var guesser = server.AnonymousFrom(NewAddress());
            guesser.Http.DefaultRequestHeaders.Authorization = client.Http.DefaultRequestHeaders.Authorization;
            using var response = await guesser.SendRawAsync(HttpMethod.Post, "/api/v1/auth/unlock",
                new UnlockRequest((1357 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), null));
            return response.StatusCode;
        }));

        // Four are told the PIN is wrong, the fifth ends the session, and the rest find it ended.
        answers.Count(a => a == HttpStatusCode.BadRequest).ShouldBe(4);
        answers.Count(a => a == HttpStatusCode.Unauthorized).ShouldBe(6);
        var user = await UserAsync(name);
        await using var scope = server.Services.CreateAsyncScope();
        var ended = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>().AsNoTracking()
            .Where(s => s.UserId == user.Id && s.RevokedReason == "unlock_attempts_exceeded")
            .ToListAsync(Ct);
        ended.ShouldHaveSingleItem().FailedUnlockAttempts.ShouldBe(5);
    }

    [Fact]
    public async Task Wrong_passwords_given_to_confirm_an_action_count_toward_the_lock()
    {
        const string name = "it-reauth-guess";
        await server.CreateStaffAccountAsync(name, SystemRoles.Billing);
        var (client, _) = await server.SignInNewSessionAsync(name);

        // Someone at a workstation left signed in guesses the password where an action asks for it again, ten at once.
        var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            var guesser = server.AnonymousFrom(NewAddress());
            guesser.Http.DefaultRequestHeaders.Authorization = client.Http.DefaultRequestHeaders.Authorization;
            using var response = await guesser.SendRawAsync(HttpMethod.Post, "/api/v1/auth/sessions/end-others", new ReauthenticateRequest($"Not-the-password-{i}"));
            return response.StatusCode;
        }));
        answers.ShouldAllBe(a => a == HttpStatusCode.BadRequest);

        // Five were counted, and they locked the account: now the right password is refused too, there and at sign-in.
        var user = await UserAsync(name);
        user.FailedLoginCount.ShouldBe(5);
        user.LockoutEndsAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        var failures = await AuditAsync(user.Id, "auth.reauthentication_failed");
        failures.Count.ShouldBe(5);
        failures.Count(e => e.Severity == AuditSeverity.Critical).ShouldBe(1);
        using (var right = await client.SendRawAsync(HttpMethod.Post, "/api/v1/auth/sessions/end-others", new ReauthenticateRequest(server.Password)))
        {
            right.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var signIn = await LogInAsync(name, server.Password);
        signIn.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
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

    // A caller at the patient portal's address, from an address of its own.
    private ApiClient OnPortal()
    {
        var client = server.AnonymousFrom(NewAddress());
        client.Http.BaseAddress = server.PortalAddress;
        return client;
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

    private static string NewMobile() => "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));

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
