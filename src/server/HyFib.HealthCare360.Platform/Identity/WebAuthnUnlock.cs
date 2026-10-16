using System.Buffers.Text;
using System.Net;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Exceptions;
using Fido2NetLib.Objects;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>The password, asked again before a device is set up to unlock the screen (ASVS V7.5.1).</summary>
public sealed record WebAuthnRegistrationRequest(string Password);

/// <summary>
/// A WebAuthn ceremony the browser is to carry out: the options for <c>navigator.credentials.create</c> or
/// <c>.get</c>, in their JSON form, and the id to send its answer back with.
/// </summary>
public sealed record WebAuthnCeremony(Guid ChallengeId, JsonElement Options);

/// <summary>The device's answer to a registration, and what to call the device.</summary>
public sealed record WebAuthnRegisterRequest(Guid ChallengeId, string? Name, JsonElement Credential);

/// <summary>The device's answer to an unlock.</summary>
public sealed record WebAuthnUnlockRequest(Guid ChallengeId, JsonElement Credential);

/// <summary>A device its owner set up to unlock the screen.</summary>
/// <param name="CredentialId">The credential's id (base64url), by which the app knows the device it runs on holds it.</param>
/// <param name="ServerName">The server name it was set up under; it works only for pages opened by that name.</param>
/// <param name="UsableHere">It was set up under the name this request came to.</param>
public sealed record WebAuthnCredentialInfo(
    Guid Id,
    string Name,
    string CredentialId,
    string ServerName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    bool UsableHere);

/// <summary>
/// Quick unlock of the lock screen with the device's own biometrics: Windows Hello (face, fingerprint or its PIN) in
/// Edge, Chrome and HC360 Desktop, Touch ID on a Mac, and the like, through WebAuthn with the device's built-in
/// (platform) authenticator (SRS UX-11). No vendor or service is involved: the device keeps a private key, unlocked by
/// the person's face or finger, and the server keeps its public half (<see cref="WebAuthnCredential"/>). The
/// fido2-net-lib library checks every answer.
/// <list type="bullet">
/// <item>It only resumes a session that is locked, or locking, and never signs anyone in: each call needs the session's
/// own token, it finishes through <see cref="AuthService.FinishUnlockAsync"/> like the PIN, and a session that ended, or
/// that nobody used for the idle limit, stays ended.</item>
/// <item>The device must verify the person (user verification required), not just that someone touched it.</item>
/// <item>A key works only for its owner, under the server name it was set up under: WebAuthn ties keys to a domain name,
/// so it is unavailable when the app was opened by IP address.</item>
/// <item>A signature counter that goes back means a copied key: refused, audited as critical, and counted as a failed
/// try. Failed tries count with wrong PINs: <see cref="SecurityOptions.MaxPinAttempts"/> end the session.</item>
/// <item>Setting up a device needs the password (ASVS V7.5.1); its owner can remove it; the
/// <see cref="AuthService.BiometricUnlockSetting"/> setting turns it off. The PIN and the password always work.</item>
/// <item>Setting up, removing and unlocking are audited.</item>
/// </list>
/// </summary>
public sealed class WebAuthnUnlockService(
    HospitalDbContext db,
    AuthService auth,
    IAuditLogger audit,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    LanCertificateManager? certificates = null)
{
    /// <summary>How long the browser gives the person to answer the device's prompt.</summary>
    public static readonly TimeSpan CeremonyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The most devices one account may set up.</summary>
    public const int MaxCredentialsPerUser = 10;

    private const string RelyingPartyName = "HealthCare360";

    // The ceremony's answer may arrive a little after the browser's own timeout.
    private static readonly TimeSpan ChallengeLifetime = CeremonyTimeout + TimeSpan.FromSeconds(30);

    private static readonly Error Off = Error.Forbidden(
        "auth.biometric_unlock_off", "Unlocking with Windows Hello or a fingerprint is turned off. Use your PIN or password.");

    private static readonly Error StaffOnly = Error.Forbidden(
        "auth.biometric_unlock_staff", "Unlocking with Windows Hello or a fingerprint is for staff workstations.");

    private static readonly Error Address = Error.Rule(
        "auth.biometric_unlock_address",
        "Windows Hello and fingerprint unlock work when HealthCare360 is opened by the server's name (such as https://HC360-SRV:7360), not by its IP address. Use your PIN or password.");

    private static readonly Error Expired = Error.Rule("auth.biometric_unlock_expired", "That took too long. Try again.");

    private static readonly Error NotVerified = Error.Rule(
        "auth.biometric_unlock_failed", "This device could not be verified. Try again, or use your PIN or password.");

    /// <summary>The signed-in user's devices set up to unlock the screen.</summary>
    public async Task<IReadOnlyList<WebAuthnCredentialInfo>> ListAsync(Guid? userId, HttpContext http, CancellationToken ct)
    {
        if (userId is null)
        {
            return [];
        }

        var here = RelyingPartyOf(http)?.Id;
        var credentials = await db.Set<WebAuthnCredential>().AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.LastUsedAt ?? c.CreatedAt)
            .ToListAsync(ct);
        return credentials.Select(c => Info(c, here)).ToList();
    }

    /// <summary>Starts setting up this device: the options for <c>navigator.credentials.create</c>.</summary>
    public async Task<Result<WebAuthnCeremony>> BeginRegistrationAsync(WebAuthnRegistrationRequest request, HttpContext http, CancellationToken ct)
    {
        var (user, session) = await auth.CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return AuthService.SessionEnded;
        }

        var refused = await RefusalAsync(user, session, http, ct);
        if (refused.Error is { } error)
        {
            return error;
        }

        if (await db.Set<WebAuthnCredential>().CountAsync(c => c.UserId == user.Id, ct) >= MaxCredentialsPerUser)
        {
            return Error.Rule("auth.biometric_unlock_limit", $"You have set up {MaxCredentialsPerUser} devices. Remove one you no longer use first.");
        }

        var confirmed = await auth.ConfirmPasswordAsync(new ReauthenticateRequest(request.Password), ct);
        if (confirmed.IsFailure)
        {
            return confirmed.Error!;
        }

        var rp = refused.RelyingParty!;
        var registered = await db.Set<WebAuthnCredential>()
            .Where(c => c.UserId == user.Id && c.RelyingPartyId == rp.Id)
            .Select(c => c.CredentialId)
            .ToListAsync(ct);
        var ceremony = Fido2For(rp).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.ToByteArray(), Name = user.UserName, DisplayName = user.DisplayName },

            // The device that already holds a key for this account says so, rather than making a second.
            ExcludeCredentials = registered.Select(Descriptor).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                AuthenticatorAttachment = AuthenticatorAttachment.Platform,
                ResidentKey = ResidentKeyRequirement.Discouraged,
                UserVerification = UserVerificationRequirement.Required,
            },

            // What make of device it is does not matter: it unlocks only a session its owner signed in to.
            AttestationPreference = AttestationConveyancePreference.None,
        });

        return await StartAsync(AuthChallengeKind.WebAuthnRegistration, user, session, ceremony.ToJson(), ct);
    }

    /// <summary>Finishes setting up this device with its answer, and keeps its public key.</summary>
    public async Task<Result<WebAuthnCredentialInfo>> RegisterAsync(WebAuthnRegisterRequest request, HttpContext http, CancellationToken ct)
    {
        var (user, session) = await auth.CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return AuthService.SessionEnded;
        }

        var refused = await RefusalAsync(user, session, http, ct);
        if (refused.Error is { } error)
        {
            return error;
        }

        var rp = refused.RelyingParty!;
        var challenge = await TakeAsync(request.ChallengeId, AuthChallengeKind.WebAuthnRegistration, user, session, ct);
        var created = challenge?.Options is { } json ? CredentialCreateOptions.FromJson(json) : null;
        if (created is null || created.Rp.Id != rp.Id)
        {
            return Expired;
        }

        if (Parse<AuthenticatorAttestationRawResponse>(request.Credential) is not { } answer)
        {
            return NotVerified;
        }

        RegisteredPublicKeyCredential key;
        try
        {
            key = await Fido2For(rp).MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = answer,
                OriginalOptions = created,
                IsCredentialIdUniqueToUserCallback = async (args, token) =>
                    !await db.Set<WebAuthnCredential>().AnyAsync(c => c.CredentialId == args.CredentialId, token),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            audit.Add(new AuditEntry(AuditCategories.Security, "auth.biometric_unlock_refused", $"A device's answer to setting up quick unlock was refused ({ex.Code})")
            {
                UserId = user.Id,
                UserName = user.UserName,
                Severity = AuditSeverity.Warning,
            });
            await db.SaveChangesAsync(ct);
            return NotVerified;
        }

        var now = time.GetUtcNow();
        var credential = new WebAuthnCredential
        {
            UserId = user.Id,
            CredentialId = key.Id,
            PublicKey = key.PublicKey,
            SignCount = key.SignCount,
            RelyingPartyId = rp.Id,
            Name = await NameAsync(request.Name, session, ct),
            AaGuid = key.AaGuid,
            Transports = key.Transports is { Length: > 0 } transports ? string.Join(',', transports.Select(t => t.ToString().ToLowerInvariant())) : null,
            IsBackupEligible = key.IsBackupEligible,
            DeviceId = session.DeviceId,
            CreatedAt = now,
        };
        db.Add(credential);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.biometric_unlock_added", $"Set up quick unlock with Windows Hello or a fingerprint on \"{credential.Name}\" ({rp.Id})")
        {
            UserId = user.Id,
            UserName = user.UserName,
            EntityType = nameof(WebAuthnCredential),
            EntityId = credential.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Info(credential, rp.Id);
    }

    /// <summary>Removes one of the signed-in user's devices: it no longer unlocks their screen.</summary>
    public async Task<Result> RemoveAsync(Guid id, CancellationToken ct)
    {
        var (user, _) = await auth.CurrentUserAndSessionAsync(ct);
        if (user is null)
        {
            return AuthService.SessionEnded;
        }

        var credential = await db.Set<WebAuthnCredential>().FirstOrDefaultAsync(c => c.Id == id && c.UserId == user.Id, ct);
        if (credential is null)
        {
            return Error.NotFound(nameof(WebAuthnCredential), id);
        }

        db.Remove(credential);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.biometric_unlock_removed", $"Removed quick unlock on \"{credential.Name}\" ({credential.RelyingPartyId})")
        {
            UserId = user.Id,
            UserName = user.UserName,
            EntityType = nameof(WebAuthnCredential),
            EntityId = credential.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Starts unlocking the screen with a device: the options for <c>navigator.credentials.get</c>.</summary>
    public async Task<Result<WebAuthnCeremony>> BeginUnlockAsync(HttpContext http, CancellationToken ct)
    {
        var (user, session) = await auth.CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return AuthService.SessionEnded;
        }

        var refused = await RefusalAsync(user, session, http, ct);
        if (refused.Error is { } error)
        {
            return error;
        }

        var rp = refused.RelyingParty!;
        var registered = await db.Set<WebAuthnCredential>()
            .Where(c => c.UserId == user.Id && c.RelyingPartyId == rp.Id)
            .Select(c => c.CredentialId)
            .ToListAsync(ct);
        if (registered.Count == 0)
        {
            return Error.Rule("auth.biometric_unlock_none", "No device is set up to unlock your screen here. Use your PIN or password.");
        }

        var ceremony = Fido2For(rp).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = registered.Select(Descriptor).ToList(),
            UserVerification = UserVerificationRequirement.Required,
        });
        return await StartAsync(AuthChallengeKind.WebAuthnUnlock, user, session, ceremony.ToJson(), ct);
    }

    /// <summary>
    /// Unlocks the screen with the device's answer, or counts a failed try (<see cref="AuthService.FinishUnlockAsync"/>).
    /// </summary>
    public async Task<Result<AuthResult>> UnlockAsync(WebAuthnUnlockRequest request, HttpContext http, CancellationToken ct)
    {
        var (user, session) = await auth.CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return AuthService.SessionEnded;
        }

        var refused = await RefusalAsync(user, session, http, ct);
        if (refused.Error is { } error)
        {
            return error;
        }

        var rp = refused.RelyingParty!;
        var challenge = await TakeAsync(request.ChallengeId, AuthChallengeKind.WebAuthnUnlock, user, session, ct);
        var asked = challenge?.Options is { } json ? AssertionOptions.FromJson(json) : null;
        if (asked is null || asked.RpId != rp.Id)
        {
            return Expired;
        }

        // Only the user's own keys, set up under this name, are looked at: another account's key unlocks nothing here.
        var answer = Parse<AuthenticatorAssertionRawResponse>(request.Credential);
        var credential = answer?.RawId is { Length: > 0 } rawId
            ? await db.Set<WebAuthnCredential>().FirstOrDefaultAsync(c => c.CredentialId == rawId && c.UserId == user.Id && c.RelyingPartyId == rp.Id, ct)
            : null;
        var verified = false;
        if (credential is not null)
        {
            try
            {
                var result = await Fido2For(rp).MakeAssertionAsync(new MakeAssertionParams
                {
                    AssertionResponse = answer!,
                    OriginalOptions = asked,
                    StoredPublicKey = credential.PublicKey,
                    StoredSignatureCounter = (uint)credential.SignCount,
                    IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(
                        args.UserHandle.Length == 16 && new Guid(args.UserHandle) == user.Id && args.CredentialId.AsSpan().SequenceEqual(credential.CredentialId)),
                }, ct);
                credential.SignCount = result.SignCount;
                credential.LastUsedAt = time.GetUtcNow();
                verified = true;
            }
            catch (Fido2VerificationException ex)
            {
                var copied = ex.Code == Fido2ErrorCode.InvalidSignCount;
                audit.Add(new AuditEntry(AuditCategories.Security, "auth.biometric_unlock_refused", copied
                    ? $"Quick unlock refused: the signature counter of \"{credential.Name}\" went back, so its key may have been copied"
                    : $"Quick unlock refused: the answer of \"{credential.Name}\" did not check out ({ex.Code})")
                {
                    UserId = user.Id,
                    UserName = user.UserName,
                    EntityType = nameof(WebAuthnCredential),
                    EntityId = credential.Id.ToString(),
                    Severity = copied ? AuditSeverity.Critical : AuditSeverity.Warning,
                });
            }
        }

        return await auth.FinishUnlockAsync(
            user,
            session,
            verified,
            $"Screen unlocked with Windows Hello or a fingerprint on \"{credential?.Name}\"",
            NotVerified,
            http,
            ct);
    }

    /// <summary>The server name and origin WebAuthn uses for a request to this server (<see cref="RelyingPartyOf(HttpRequest, IEnumerable{string})"/>).</summary>
    internal RelyingParty? RelyingPartyOf(HttpContext http) =>
        RelyingPartyOf(http.Request, [.. options.Value.Network.AdditionalHostNames, .. certificates?.SubjectAlternativeNames ?? []]);

    /// <summary>
    /// The server name and origin WebAuthn uses for a request: the name it was addressed by, when that is one of the
    /// server's names (<paramref name="serverNames"/>: its certificate's and <see cref="NetworkOptions.AdditionalHostNames"/>;
    /// its computer name and <c>localhost</c> always are). Null for an IP address, which WebAuthn cannot use, or a name
    /// that is not the server's. Pages must be served over HTTPS, except from localhost (a secure context either way).
    /// </summary>
    internal static RelyingParty? RelyingPartyOf(HttpRequest request, IEnumerable<string> serverNames)
    {
        var host = request.Host.Host.ToLowerInvariant();
        if (host.Length == 0 || host.StartsWith('[') || IPAddress.TryParse(host, out _))
        {
            return null;
        }

        var local = host == "localhost";
        if (!request.IsHttps && !local)
        {
            return null;
        }

        var machine = Environment.MachineName.ToLowerInvariant();
        var known = local || host == machine || host == machine + ".local"
            || serverNames.Any(name => string.Equals(name?.Trim(), host, StringComparison.OrdinalIgnoreCase));
        var port = request.Host.Port is { } p ? $":{p}" : string.Empty;
        return known ? new RelyingParty(host, $"{request.Scheme}://{host}{port}") : null;
    }

    /// <summary>Why the session may not use a device to unlock (and its server name when it may).</summary>
    private async Task<(RelyingParty? RelyingParty, Error? Error)> RefusalAsync(User user, UserSession session, HttpContext http, CancellationToken ct)
    {
        if (user.Type != UserType.Staff || session.ClientType == "portal" || auth.IsRestricted(user))
        {
            return (null, StaffOnly);
        }

        if (!await auth.BiometricUnlockAllowedAsync(session.FacilityId, ct))
        {
            return (null, Off);
        }

        return RelyingPartyOf(http) is { } rp ? (rp, null) : (null, Address);
    }

    /// <summary>Keeps the ceremony's options for its answer, in place of any this session left unfinished.</summary>
    private async Task<Result<WebAuthnCeremony>> StartAsync(AuthChallengeKind kind, User user, UserSession session, string json, CancellationToken ct)
    {
        await db.Set<AuthChallenge>()
            .Where(c => c.SessionId == session.Id && c.Kind == kind && c.ConsumedAt == null)
            .ExecuteDeleteAsync(ct);
        var now = time.GetUtcNow();
        var challenge = new AuthChallenge
        {
            Kind = kind,
            UserId = user.Id,
            SessionId = session.Id,
            FacilityId = session.FacilityId,
            DeviceId = session.DeviceId,
            ClientType = session.ClientType,
            Options = json,
            CreatedAt = now,
            ExpiresAt = now + ChallengeLifetime,
        };
        db.Add(challenge);
        await db.SaveChangesAsync(ct);
        using var document = JsonDocument.Parse(json);
        return new WebAuthnCeremony(challenge.Id, document.RootElement.Clone());
    }

    /// <summary>
    /// Uses up the session's ceremony: each challenge is answered once, by the session that asked for it, before it
    /// expires. Null when it cannot be (already used, expired, or another's).
    /// </summary>
    private async Task<AuthChallenge?> TakeAsync(Guid id, AuthChallengeKind kind, User user, UserSession session, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var taken = await db.Set<AuthChallenge>()
            .Where(c => c.Id == id && c.Kind == kind && c.UserId == user.Id && c.SessionId == session.Id && c.ConsumedAt == null && c.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        return taken == 1 ? await db.Set<AuthChallenge>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) : null;
    }

    private async Task<string> NameAsync(string? requested, UserSession session, CancellationToken ct)
    {
        var name = requested?.Trim();
        if (string.IsNullOrEmpty(name) && session.DeviceId is { } deviceId)
        {
            name = await db.Set<Device>().Where(d => d.Id == deviceId).Select(d => d.Name).FirstOrDefaultAsync(ct);
        }

        name = string.IsNullOrWhiteSpace(name) ? "This device" : name;
        return name.Length <= 64 ? name : name[..64];
    }

    private static Fido2 Fido2For(RelyingParty rp) => new(
        new Fido2Configuration
        {
            RPID = rp.Id,
            RPName = RelyingPartyName,
            Origins = new HashSet<string>(StringComparer.Ordinal) { rp.Origin },
            Timeout = (uint)CeremonyTimeout.TotalMilliseconds,
        },
        metadataService: null);

    // A key built into the device: the browser asks it, and does not offer a phone or a security key instead.
    private static PublicKeyCredentialDescriptor Descriptor(byte[] id) => new(PublicKeyCredentialType.PublicKey, id, [AuthenticatorTransport.Internal]);

    private static T? Parse<T>(JsonElement json)
        where T : class
    {
        try
        {
            return json.ValueKind == JsonValueKind.Object ? json.Deserialize<T>() : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or NotSupportedException)
        {
            return null;
        }
    }

    private static WebAuthnCredentialInfo Info(WebAuthnCredential c, string? here) =>
        new(c.Id, c.Name, Base64Url.EncodeToString(c.CredentialId), c.RelyingPartyId, c.CreatedAt, c.LastUsedAt, c.RelyingPartyId == here);

    /// <summary>A WebAuthn relying party: the server name (its id) and the origin pages come from.</summary>
    internal sealed record RelyingParty(string Id, string Origin);
}
