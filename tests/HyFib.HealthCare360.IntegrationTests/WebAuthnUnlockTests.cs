using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Quick unlock of the lock screen with the device's Windows Hello or fingerprint (WebAuthn, SRS UX-11) over the real
/// API, with a software authenticator in place of the device: setting a device up needs the password, a device unlocks
/// only its owner's locked session, the device must verify the person, a copied key and a replayed answer are refused,
/// a session nobody used for the idle limit stays ended, and an administrator can turn it off while the PIN keeps
/// working. Each test uses an account of its own.
/// </summary>
public sealed class WebAuthnUnlockTests(HospitalServer server)
{
    private const HttpStatusCode Locked = (HttpStatusCode)423;
    private const string AnyPage = "/api/v1/settings/public";
    private const string RegistrationOptions = "/api/v1/auth/webauthn/credentials/options";
    private const string Credentials = "/api/v1/auth/webauthn/credentials";
    private const string UnlockOptions = "/api/v1/auth/unlock/webauthn/options";
    private const string Unlock = "/api/v1/auth/unlock/webauthn";

    private static int _addresses;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_device_set_up_with_the_password_unlocks_the_locked_screen()
    {
        var (client, userName) = await NewStaffAsync("setup");
        using var device = new SoftwareAuthenticator();

        using (var wrong = await client.SendRawAsync(HttpMethod.Post, RegistrationOptions, new WebAuthnRegistrationRequest("not-the-password")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var ceremony = await client.PostAsync<WebAuthnCeremony>(RegistrationOptions, new WebAuthnRegistrationRequest(server.Password));
        var options = ceremony.Options;
        options.GetProperty("rp").GetProperty("id").GetString().ShouldBe("localhost");
        var selection = options.GetProperty("authenticatorSelection");
        selection.GetProperty("authenticatorAttachment").GetString().ShouldBe("platform");
        selection.GetProperty("userVerification").GetString().ShouldBe("required");

        var added = await client.PostAsync<WebAuthnCredentialInfo>(Credentials, new WebAuthnRegisterRequest(ceremony.ChallengeId, "OPD Reception 2", device.Register(options)));

        added.Name.ShouldBe("OPD Reception 2");
        added.CredentialId.ShouldBe(device.CredentialIdText);
        added.ServerName.ShouldBe("localhost");
        added.UsableHere.ShouldBeTrue();
        (await client.GetAsync<List<WebAuthnCredentialInfo>>(Credentials)).ShouldHaveSingleItem().Id.ShouldBe(added.Id);
        (await client.GetAsync<AuthUser>("/api/v1/auth/me")).BiometricUnlock.ShouldBeTrue();

        await client.PostAsync("/api/v1/auth/lock");
        (await StatusOfAsync(client)).ShouldBe(Locked);

        var unlock = await client.PostAsync<WebAuthnCeremony>(UnlockOptions);
        unlock.Options.GetProperty("userVerification").GetString().ShouldBe("required");
        unlock.Options.GetProperty("allowCredentials").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(device.CredentialIdText);
        var unlocked = await client.PostAsync<AuthResult>(Unlock, new WebAuthnUnlockRequest(unlock.ChallengeId, device.Sign(unlock.Options, await UserIdAsync(userName))));

        unlocked.Restriction.ShouldBeNull();
        unlocked.AccessToken.ShouldNotBeNullOrEmpty();
        (await StatusOfAsync(client)).ShouldBe(HttpStatusCode.OK);
        (await AuditAsync(userName, "auth.biometric_unlock_added")).ShouldHaveSingleItem();
        (await AuditAsync(userName, "auth.screen_unlocked")).ShouldContain(e => e.Summary != null && e.Summary.Contains("Windows Hello"));
        var stored = await CredentialAsync(added.Id);
        stored.SignCount.ShouldBe(2);
        stored.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_device_only_resumes_its_owners_session_and_never_signs_anyone_in()
    {
        var (owner, _) = await NewStaffAsync("owner");
        var (other, _) = await NewStaffAsync("other");
        using var ownersDevice = new SoftwareAuthenticator();
        using var othersDevice = new SoftwareAuthenticator();
        await SetUpAsync(owner, ownersDevice);
        await SetUpAsync(other, othersDevice);

        // Without a session there is nothing to resume.
        using (var anonymous = await server.Anonymous().SendRawAsync(HttpMethod.Post, UnlockOptions))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // The owner's device, answering the other account's locked screen, is refused, and counts as a failed try.
        await other.PostAsync("/api/v1/auth/lock");
        var ceremony = await other.PostAsync<WebAuthnCeremony>(UnlockOptions);
        using (var refused = await other.SendRawAsync(HttpMethod.Post, Unlock, new WebAuthnUnlockRequest(ceremony.ChallengeId, ownersDevice.Sign(ceremony.Options))))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("auth.biometric_unlock_failed");
        }

        (await StatusOfAsync(other)).ShouldBe(Locked);
        (await SessionAsync(other)).FailedUnlockAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task The_device_must_verify_the_person_not_only_their_presence()
    {
        var (client, _) = await NewStaffAsync("verify");
        using var device = new SoftwareAuthenticator { VerifiesUser = false };

        var ceremony = await client.PostAsync<WebAuthnCeremony>(RegistrationOptions, new WebAuthnRegistrationRequest(server.Password));
        using (var refused = await client.SendRawAsync(HttpMethod.Post, Credentials, new WebAuthnRegisterRequest(ceremony.ChallengeId, null, device.Register(ceremony.Options))))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        // Set up properly, it still cannot unlock with an answer that skipped verifying the person.
        device.VerifiesUser = true;
        await SetUpAsync(client, device);
        await client.PostAsync("/api/v1/auth/lock");
        device.VerifiesUser = false;
        var unlock = await client.PostAsync<WebAuthnCeremony>(UnlockOptions);
        using (var refused = await client.SendRawAsync(HttpMethod.Post, Unlock, new WebAuthnUnlockRequest(unlock.ChallengeId, device.Sign(unlock.Options))))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        (await StatusOfAsync(client)).ShouldBe(Locked);
    }

    [Fact]
    public async Task A_key_whose_counter_goes_back_is_refused_as_a_copy()
    {
        var (client, userName) = await NewStaffAsync("copy");
        using var device = new SoftwareAuthenticator();
        await SetUpAsync(client, device);
        await client.PostAsync("/api/v1/auth/lock");
        await UnlockWithAsync(client, device);

        // A copy of the key answers with a count it already used.
        await client.PostAsync("/api/v1/auth/lock");
        device.Counter -= 1;
        var ceremony = await client.PostAsync<WebAuthnCeremony>(UnlockOptions);
        using (var refused = await client.SendRawAsync(HttpMethod.Post, Unlock, new WebAuthnUnlockRequest(ceremony.ChallengeId, device.Sign(ceremony.Options))))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        (await StatusOfAsync(client)).ShouldBe(Locked);
        (await AuditAsync(userName, "auth.biometric_unlock_refused")).ShouldContain(e => e.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public async Task Each_answer_is_taken_once_and_only_by_the_session_that_asked()
    {
        var (client, userName) = await NewStaffAsync("replay");
        using var device = new SoftwareAuthenticator();
        await SetUpAsync(client, device);
        var (elsewhere, _) = await server.SignInNewSessionAsync(userName);
        elsewhere = FromNewAddress(elsewhere);

        await client.PostAsync("/api/v1/auth/lock");
        var ceremony = await client.PostAsync<WebAuthnCeremony>(UnlockOptions);
        var answer = new WebAuthnUnlockRequest(ceremony.ChallengeId, device.Sign(ceremony.Options));

        // Another session of the same account cannot use this session's challenge...
        await elsewhere.PostAsync("/api/v1/auth/lock");
        using (var refused = await elsewhere.SendRawAsync(HttpMethod.Post, Unlock, answer))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("auth.biometric_unlock_expired");
        }

        // ...and once used, the answer cannot unlock again.
        (await client.PostAsync<AuthResult>(Unlock, answer)).Restriction.ShouldBeNull();
        await client.PostAsync("/api/v1/auth/lock");
        using (var replayed = await client.SendRawAsync(HttpMethod.Post, Unlock, answer))
        {
            replayed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        (await StatusOfAsync(client)).ShouldBe(Locked);
        (await StatusOfAsync(elsewhere)).ShouldBe(Locked);
    }

    [Fact]
    public async Task A_session_nobody_used_for_the_idle_limit_is_not_resumed_by_any_unlock()
    {
        var (byDevice, _) = await NewStaffAsync("idle-device");
        var (byPin, _) = await NewStaffAsync("idle-pin");
        using var device = new SoftwareAuthenticator();
        await SetUpAsync(byDevice, device);
        await byDevice.PostAsync("/api/v1/auth/lock");
        await byPin.PostAsync("/api/v1/auth/lock");
        var ceremony = await byDevice.PostAsync<WebAuthnCeremony>(UnlockOptions);
        await IdleAsync(byDevice, TimeSpan.FromMinutes(121));
        await IdleAsync(byPin, TimeSpan.FromMinutes(121));

        using (var ended = await byDevice.SendRawAsync(HttpMethod.Post, Unlock, new WebAuthnUnlockRequest(ceremony.ChallengeId, device.Sign(ceremony.Options))))
        {
            ended.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var ended = await byPin.SendRawAsync(HttpMethod.Post, "/api/v1/auth/unlock", new UnlockRequest(null, server.Password)))
        {
            ended.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await SessionAsync(byDevice)).RevokedReason.ShouldBe("idle_timeout");
        (await SessionAsync(byPin)).RevokedReason.ShouldBe("idle_timeout");
    }

    [Fact]
    public async Task An_administrator_can_turn_it_off_and_the_PIN_and_password_still_work()
    {
        var (client, _) = await NewStaffAsync("off");
        using var device = new SoftwareAuthenticator();
        await SetUpAsync(client, device);
        await SetBiometricUnlockAsync(false);
        try
        {
            (await client.GetAsync<AuthUser>("/api/v1/auth/me")).BiometricUnlock.ShouldBeFalse();
            await client.PostAsync("/api/v1/auth/lock");
            using (var off = await client.SendRawAsync(HttpMethod.Post, UnlockOptions))
            {
                off.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                (await off.Content.ReadAsStringAsync(Ct)).ShouldContain("auth.biometric_unlock_off");
            }

            (await client.PostAsync<AuthResult>("/api/v1/auth/unlock", new UnlockRequest(null, server.Password))).Restriction.ShouldBeNull();
        }
        finally
        {
            await SetBiometricUnlockAsync(null);
        }
    }

    [Fact]
    public async Task Its_owner_can_remove_a_device_which_then_unlocks_nothing()
    {
        var (client, userName) = await NewStaffAsync("remove");
        using var device = new SoftwareAuthenticator();
        var added = await SetUpAsync(client, device);

        using (var removed = await client.SendRawAsync(HttpMethod.Delete, $"{Credentials}/{added.Id}"))
        {
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await client.PostAsync("/api/v1/auth/lock");
        using (var none = await client.SendRawAsync(HttpMethod.Post, UnlockOptions))
        {
            none.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await none.Content.ReadAsStringAsync(Ct)).ShouldContain("auth.biometric_unlock_none");
        }

        (await AuditAsync(userName, "auth.biometric_unlock_removed")).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task An_administrators_password_reset_removes_the_devices_with_the_PIN()
    {
        var (client, userName) = await NewStaffAsync("reset");
        using var device = new SoftwareAuthenticator();
        var added = await SetUpAsync(client, device);
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);

        await admin.PostAsync<JsonElement>($"/api/v1/admin/users/{await UserIdAsync(userName)}/reset-password");

        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<WebAuthnCredential>().AnyAsync(c => c.Id == added.Id, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task It_needs_the_servers_name_in_the_address_not_an_IP_address()
    {
        var (client, _) = await NewStaffAsync("address");
        var byAddress = server.AnonymousAt(new Uri("http://127.0.0.1/"));
        byAddress.Http.DefaultRequestHeaders.Authorization = client.Http.DefaultRequestHeaders.Authorization;

        using var refused = await byAddress.SendRawAsync(HttpMethod.Post, RegistrationOptions, new WebAuthnRegistrationRequest(server.Password));

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("auth.biometric_unlock_address");
    }

    private async Task<(ApiClient Client, string UserName)> NewStaffAsync(string purpose)
    {
        var userName = $"it-wa-{purpose}-{Guid.NewGuid():N}"[..24];
        await server.CreateStaffAccountAsync(userName, SystemRoles.Nurse);
        var (client, _) = await server.SignInNewSessionAsync(userName);
        return (FromNewAddress(client), userName);
    }

    /// <summary>
    /// The client's sign-in, sending from an address of its own: unlocking shares sign-in's limit of 10 requests a
    /// minute per address, and a test makes several.
    /// </summary>
    private ApiClient FromNewAddress(ApiClient client)
    {
        var n = Interlocked.Increment(ref _addresses);
        var moved = server.AnonymousFrom($"10.251.{n / 250}.{(n % 250) + 1}");
        moved.Http.DefaultRequestHeaders.Authorization = client.Http.DefaultRequestHeaders.Authorization;
        return moved;
    }

    private async Task<WebAuthnCredentialInfo> SetUpAsync(ApiClient client, SoftwareAuthenticator device)
    {
        var ceremony = await client.PostAsync<WebAuthnCeremony>(RegistrationOptions, new WebAuthnRegistrationRequest(server.Password));
        return await client.PostAsync<WebAuthnCredentialInfo>(Credentials, new WebAuthnRegisterRequest(ceremony.ChallengeId, null, device.Register(ceremony.Options)));
    }

    private static async Task UnlockWithAsync(ApiClient client, SoftwareAuthenticator device)
    {
        var ceremony = await client.PostAsync<WebAuthnCeremony>(UnlockOptions);
        (await client.PostAsync<AuthResult>(Unlock, new WebAuthnUnlockRequest(ceremony.ChallengeId, device.Sign(ceremony.Options)))).Restriction.ShouldBeNull();
    }

    private static async Task<HttpStatusCode> StatusOfAsync(ApiClient client)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, AnyPage);
        return response.StatusCode;
    }

    private static Guid SessionIdOf(ApiClient client)
    {
        var jwt = client.Http.DefaultRequestHeaders.Authorization?.Parameter ?? throw new InvalidOperationException("The client has no token.");
        using var payload = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.Split('.')[1]));
        return Guid.Parse(payload.RootElement.GetProperty("sid").GetString()!);
    }

    private async Task<UserSession> SessionAsync(ApiClient client)
    {
        var id = SessionIdOf(client);
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserSession>().AsNoTracking().SingleAsync(s => s.Id == id, Ct);
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

    private async Task<Guid> UserIdAsync(string userName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>().Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(Ct);
    }

    private async Task<WebAuthnCredential> CredentialAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<WebAuthnCredential>().AsNoTracking().SingleAsync(c => c.Id == id, Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(string userName, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.Action == action && e.UserName == userName)
            .ToListAsync(Ct);
    }

    private async Task SetBiometricUnlockAsync(bool? on)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (on is { } value)
        {
            await settings.SetAsync(AuthService.BiometricUnlockSetting, value, cancellationToken: Ct);
        }
        else
        {
            await settings.RemoveAsync(AuthService.BiometricUnlockSetting, null, Ct);
        }
    }
}
