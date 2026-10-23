using System.Globalization;
using System.Text.Json;
using FluentValidation;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtpNet;

namespace HyFib.HealthCare360.Platform.Identity;

public sealed record LoginRequest(string UserName, string Password, Guid? FacilityId = null, Guid? DeviceId = null, string? DeviceSecret = null);

/// <summary>
/// A user name and password of the lengths they can have. A request that could never sign in is answered before any
/// account is read, so it neither costs a password check nor counts as a failed sign-in.
/// </summary>
public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.UserName).NotEmpty().WithMessage("Enter your user name.").MaximumLength(64);
        RuleFor(r => r.Password).GivenPassword("Enter your password.");
        RuleFor(r => r.FacilityId).OptionalId();
        RuleFor(r => r.DeviceId).OptionalId();
        RuleFor(r => r.DeviceSecret).MaximumLength(IdentityRules.MaxDeviceSecretLength);
    }
}

public sealed record MfaVerifyRequest(Guid ChallengeId, string Code);

public sealed class MfaVerifyRequestValidator : AbstractValidator<MfaVerifyRequest>
{
    public MfaVerifyRequestValidator()
    {
        RuleFor(r => r.ChallengeId).NotEmpty();
        RuleFor(r => r.Code).SixDigitCode(IdentityRules.AuthenticatorCodeMessage);
    }
}

public sealed record OtpRequest(string Phone);

/// <summary>An Indian mobile number, as <see cref="AuthService.RequestOtpAsync"/> wants it, checked before the proof of work.</summary>
public sealed class OtpRequestValidator : AbstractValidator<OtpRequest>
{
    public OtpRequestValidator() =>
        RuleFor(r => r.Phone).Cascade(CascadeMode.Stop).NotEmpty().WithMessage("Enter a valid 10-digit mobile number.").IndianMobile();
}

public sealed record OtpVerifyRequest(Guid ChallengeId, string Code);

/// <summary>A code of the shape sent, checked before the proof of work, so a mistyped one neither spends it nor counts as a guess.</summary>
public sealed class OtpVerifyRequestValidator : AbstractValidator<OtpVerifyRequest>
{
    public OtpVerifyRequestValidator()
    {
        RuleFor(r => r.ChallengeId).NotEmpty();
        RuleFor(r => r.Code).SixDigitCode("Enter the 6-digit code sent to your mobile.");
    }
}

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <summary>The rest of the password policy needs the account (<see cref="PasswordPolicy.Validate"/>).</summary>
public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(r => r.CurrentPassword).GivenPassword("Enter your current password.");
        RuleFor(r => r.NewPassword).NotEmpty().WithMessage("Choose a new password.")
            .MaximumLength(PasswordPolicy.MaxLength).WithMessage($"Use at most {PasswordPolicy.MaxLength} characters.");
    }
}

public sealed record SetPinRequest(string Pin, string Password);

/// <summary>A quick-unlock PIN of 4–6 digits that are not all the same or in sequence, as the lock screen asks for it.</summary>
public sealed class SetPinRequestValidator : AbstractValidator<SetPinRequest>
{
    public SetPinRequestValidator()
    {
        RuleFor(r => r.Pin).Must(IdentityRules.IsAcceptablePin).WithMessage("Use 4–6 digits that are not all the same or in sequence.");
        RuleFor(r => r.Password).GivenPassword("Enter your password.");
    }
}

public sealed record UnlockRequest(string? Pin, string? Password);

/// <summary>
/// The PIN or the password. A PIN not of the shape one is set in, or neither, is answered without counting a failed try:
/// it could not have unlocked the screen.
/// </summary>
public sealed class UnlockRequestValidator : AbstractValidator<UnlockRequest>
{
    public UnlockRequestValidator()
    {
        RuleFor(r => r.Pin).Matches("^[0-9]{4,6}$").WithMessage("Enter your PIN of 4–6 digits.").When(r => !string.IsNullOrEmpty(r.Pin));
        RuleFor(r => r.Password).NotEmpty().WithMessage("Enter your PIN or password.").When(r => string.IsNullOrEmpty(r.Pin));
        RuleFor(r => r.Password).MaximumLength(PasswordPolicy.MaxLength).WithMessage(IdentityRules.PasswordTooLongMessage);
    }
}

public sealed record SwitchFacilityRequest(Guid FacilityId);

public sealed class SwitchFacilityRequestValidator : AbstractValidator<SwitchFacilityRequest>
{
    public SwitchFacilityRequestValidator() => RuleFor(r => r.FacilityId).NotEmpty().WithMessage("Choose a facility.");
}

public sealed record MfaSetupRequest(string Password);

public sealed class MfaSetupRequestValidator : AbstractValidator<MfaSetupRequest>
{
    public MfaSetupRequestValidator() => RuleFor(r => r.Password).GivenPassword("Enter your password.");
}

public sealed record MfaEnableRequest(string Code);

public sealed class MfaEnableRequestValidator : AbstractValidator<MfaEnableRequest>
{
    public MfaEnableRequestValidator() => RuleFor(r => r.Code).SixDigitCode(IdentityRules.AuthenticatorCodeMessage);
}

public sealed record MfaDisableRequest(string Password);

public sealed class MfaDisableRequestValidator : AbstractValidator<MfaDisableRequest>
{
    public MfaDisableRequestValidator() => RuleFor(r => r.Password).GivenPassword("Enter your password.");
}

public sealed record MfaSetupResponse(string Secret, string OtpAuthUri);

public sealed record FacilityOption(Guid Id, string Code, string Name);

public sealed record AuthUser(
    Guid Id,
    string UserName,
    string DisplayName,
    string Type,
    Guid? FacilityId,
    string? FacilityName,
    Guid? PractitionerId,
    Guid? PatientId,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    IReadOnlyList<FacilityOption> Facilities,
    bool MfaEnabled,
    bool HasPin,
    bool MustChangePassword,
    string? Restriction,
    bool PasswordBreached = false,
    bool BiometricUnlock = false,
    bool AdministrationNeedsDesktop = false);

public sealed record AuthResult(
    string? AccessToken,
    DateTimeOffset? ExpiresAt,
    AuthUser? User,
    bool MfaRequired = false,
    Guid? ChallengeId = null,
    string? Restriction = null);

public sealed record OtpChallengeResponse(Guid ChallengeId, DateTimeOffset ExpiresAt, string MaskedPhone);

public sealed record SessionInfo(
    Guid Id,
    string ClientType,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActiveAt,
    DateTimeOffset ExpiresAt,
    string? IpAddress,
    string? UserAgent,
    string? DeviceName,
    bool IsCurrent,
    bool IsLocked);

/// <summary>The signed-in user's password, asked again before sessions are ended (ASVS V7.5.2).</summary>
public sealed record ReauthenticateRequest(string Password);

public sealed class ReauthenticateRequestValidator : AbstractValidator<ReauthenticateRequest>
{
    public ReauthenticateRequestValidator() => RuleFor(r => r.Password).GivenPassword("Enter your password.");
}

/// <summary>What the sign-in requests have in common.</summary>
public static class IdentityRules
{
    public const string AuthenticatorCodeMessage = "Enter the 6-digit code from your authenticator app.";

    public static readonly string PasswordTooLongMessage = $"Passwords here have at most {PasswordPolicy.MaxLength} characters.";

    /// <summary>A device secret is 32 random bytes in base64url (43 characters); this leaves room and no more.</summary>
    public const int MaxDeviceSecretLength = 128;

    /// <summary>A password typed to prove who someone is: given, and no longer than one can be set (<see cref="PasswordPolicy.MaxLength"/>).</summary>
    public static IRuleBuilderOptions<T, string?> GivenPassword<T>(this IRuleBuilder<T, string?> rule, string message) =>
        rule.NotEmpty().WithMessage(message).MaximumLength(PasswordPolicy.MaxLength).WithMessage(PasswordTooLongMessage);

    /// <summary>The six digits of an authenticator or text-message code; spaces around them are ignored, as when they are checked.</summary>
    public static IRuleBuilderOptions<T, string?> SixDigitCode<T>(this IRuleBuilder<T, string?> rule, string message) =>
        rule.NotEmpty().WithMessage(message).Matches(@"^\s*[0-9]{6}\s*$").WithMessage(message);

    /// <summary>4–6 digits, not all the same and not a run up or down such as 1234 or 9876.</summary>
    public static bool IsAcceptablePin(string? pin) =>
        pin is { Length: >= 4 and <= 6 } && pin.All(char.IsAsciiDigit) && pin.Distinct().Count() > 1
        && !"0123456789".Contains(pin, StringComparison.Ordinal) && !"9876543210".Contains(pin, StringComparison.Ordinal);
}

public sealed record SessionsEnded(int Ended);

/// <summary>
/// Sign-in, token refresh and session management (SEC-01/02, ADR-006). Refresh tokens rotate on every use;
/// re-use of an old token outside a short grace window revokes the whole session (theft detection), while a
/// re-use inside the window is treated as a lost response on flaky WiFi and simply re-issued.
/// </summary>
public sealed class AuthService(
    HospitalDbContext db,
    TokenService tokens,
    PasswordHashing hashing,
    PasswordPolicy passwordPolicy,
    IFieldProtector protector,
    IPermissionService permissions,
    PermissionRegistry permissionRegistry,
    IAdministrationDesktops administrationDesktops,
    ISessionValidator sessionValidator,
    IAuditLogger audit,
    INotificationService notifications,
    IPortalAccountResolver portalAccounts,
    ICurrentUser currentUser,
    ISettingsService settings,
    IOptions<PlatformOptions> options,
    TimeProvider time)
{
    public const string RefreshCookieName = "hc360_rt";

    /// <summary>
    /// The patient portal's refresh cookie. The portal has an address of its own, but browsers send a host's cookies to
    /// every port of it, so each app keeps its own cookie and each address reads only its own app's: signing in to one
    /// never replaces the other's session in the same browser.
    /// </summary>
    public const string PortalRefreshCookieName = "hc360_prt";
    public const string RefreshCookiePath = "/api/v1/auth";
    public const string ClientHeader = "X-HC360-Client";
    public const string RestrictionPasswordChange = "password";
    public const string RestrictionMfaSetup = "mfa-setup";

    /// <summary>The screen is locked (<see cref="SessionLock"/>): the token is good for unlocking only.</summary>
    public const string RestrictionLocked = "locked";

    /// <summary>
    /// Whole seconds since someone last used the app (a key press, click or touch), sent with each refresh. The refresh
    /// itself is no sign of anyone at the screen: the app refreshes in the background every few minutes.
    /// </summary>
    public const string IdleHeader = "X-HC360-Idle";

    /// <summary>Minutes without use before a staff screen locks (0: never); the app's lock and the server's agree.</summary>
    public const string IdleLockMinutesSetting = "ui.idleLockMinutes";

    /// <summary>
    /// Whether staff may unlock the lock screen with the device's Windows Hello, fingerprint or face (false unless set to
    /// true, for the hospital or a facility; <see cref="WebAuthnUnlockService"/>). Windows Hello proves who owns the
    /// Windows login, not who owns the HC360 account: where staff share one Windows login, whoever knows its Windows
    /// Hello PIN, or has a finger or face set up on it, could unlock a colleague's screen. So it is for hospitals where
    /// every staff member signs in to Windows with their own account. The PIN and the password work either way.
    /// </summary>
    public const string BiometricUnlockSetting = "ui.biometricUnlock";

    /// <summary>How many sessions one staff account may have at once (0: no limit).</summary>
    public const string MaxSessionsSetting = "security.maxSessionsPerUser";

    public const int DefaultIdleLockMinutes = 10;
    public const int DefaultMaxSessions = 3;

    private const int MaxChallengeAttempts = 5;
    private static readonly TimeSpan MfaChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshReuseGrace = TimeSpan.FromSeconds(60);
    private static string? _dummyHash;

    // Also the answer for a locked account, so a response never confirms that an account exists.
    private static readonly Error InvalidCredentials = new(
        "auth.invalid_credentials",
        "Invalid username or password. After repeated failures an account is locked for a while; an administrator can unlock it.",
        ErrorType.Unauthorized);
    internal static readonly Error SessionEnded = new("auth.session_ended", "Your session has ended. Please sign in again.", ErrorType.Unauthorized);
    private static readonly Error ChallengeExpired = new("auth.challenge_expired", "This sign-in attempt has expired. Please start again.", ErrorType.Unauthorized);
    private static readonly Error TemporaryPasswordExpired = new(
        "auth.temporary_password_expired", "This temporary password has expired. Ask an administrator for a new one.", ErrorType.Unauthorized);

    private SecurityOptions Security => options.Value.Security;

    // ---------------------------------------------------------------- password sign-in

    public async Task<Result<AuthResult>> LoginAsync(LoginRequest request, HttpContext http, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var normalized = User.Normalize(request.UserName ?? string.Empty);
        var user = await LoadUserAsync(u => u.NormalizedUserName == normalized && u.Type != UserType.Patient, ct);

        if (user?.PasswordHash is null || !user.IsActive)
        {
            // Equalise timing so response time does not reveal whether the account exists.
            hashing.Verify(_dummyHash ??= hashing.Hash("hc360-timing-equaliser"), request.Password ?? string.Empty);
            await audit.WriteAsync(new AuditEntry(AuditCategories.Auth, "auth.login_failed", user is null ? "Unknown user" : "Inactive user")
            {
                UserId = user?.Id,
                UserName = Truncate(normalized, 64),
                Severity = AuditSeverity.Warning,
            }, ct);
            return InvalidCredentials;
        }

        if (user.LockoutEndsAt is { } lockedUntil && lockedUntil > now)
        {
            // The same work and the same answer as a wrong password: neither says whether the account exists. The
            // password is not checked against the lock, so the lock cannot be used to test guesses.
            hashing.Verify(_dummyHash ??= hashing.Hash("hc360-timing-equaliser"), request.Password ?? string.Empty);
            await AuditLockedAsync(user, "Sign-in attempted while locked", ct);
            return InvalidCredentials;
        }

        var check = hashing.Verify(user.PasswordHash, request.Password ?? string.Empty);

        // Attempts sent at once all read the account before any of them was counted. Read it again under its row lock, one
        // attempt at a time, so that each wrong password counts and none gets in once the others have locked the account
        // (ASVS V6.3.1). The lock is held only while this is decided.
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAndReloadAsync(user, ct);
            if (user.LockoutEndsAt > now)
            {
                await AuditLockedAsync(user, "Sign-in attempted while locked", ct);
                return InvalidCredentials;
            }

            if (!check.Succeeded)
            {
                var locked = SignInPolicy.RecordFailure(Security, user, now);
                audit.Add(new AuditEntry(AuditCategories.Auth, "auth.login_failed", "Wrong password")
                {
                    UserId = user.Id,
                    UserName = user.UserName,
                    Severity = locked ? AuditSeverity.Critical : AuditSeverity.Warning,
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return InvalidCredentials;
            }
        }

        // A temporary password works only for a while (ASVS V6.4.1); an administrator gives out a new one.
        if (user.MustChangePassword && user.TemporaryPasswordExpiresAt <= now)
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.Auth, "auth.temporary_password_expired", "Signed in with an expired temporary password")
            {
                UserId = user.Id,
                UserName = user.UserName,
                Severity = AuditSeverity.Warning,
            }, ct);
            return TemporaryPasswordExpired;
        }

        if (check.NeedsRehash)
        {
            user.PasswordHash = hashing.Hash(request.Password!);
        }

        // A password attackers try first has to be changed before anything else (SRS SEC-01). It was set before HC360
        // checked new passwords against breaches, or the lists have grown since.
        if (user.Type == UserType.Staff && !user.MustChangePassword && PasswordPolicy.IsKnownToAttackers(request.Password!))
        {
            user.MustChangePassword = true;
            user.PasswordBreachedAt = now;
            audit.Add(new AuditEntry(AuditCategories.Security, "auth.password_breached",
                "Signed in with a password published from data breaches or on the common-password list; it must be changed")
            {
                UserId = user.Id,
                UserName = user.UserName,
                Severity = AuditSeverity.Warning,
            });
        }

        var roles = RoleNames(user);
        var deviceId = await VerifyDeviceAsync(request.DeviceId, request.DeviceSecret, ct);
        if (deviceId is null && roles.Any(r => Security.EnrolledDeviceRequiredRoles.Contains(r, StringComparer.OrdinalIgnoreCase)))
        {
            return Error.Forbidden("auth.device_required", "This account can sign in only from an enrolled HC360 Desktop workstation.");
        }

        var facility = await ResolveFacilityAsync(user, roles, request.FacilityId, ct);
        if (facility.IsFailure)
        {
            return facility.Error!;
        }

        var clientType = ClientTypeOf(http);
        if (user.MfaEnabled)
        {
            var challenge = new AuthChallenge
            {
                Kind = AuthChallengeKind.Mfa,
                UserId = user.Id,
                FacilityId = facility.Value?.Id,
                DeviceId = deviceId,
                ClientType = clientType,
                CreatedAt = now,
                ExpiresAt = now + MfaChallengeLifetime,
            };
            db.Add(challenge);
            await db.SaveChangesAsync(ct);
            return new AuthResult(null, null, null, MfaRequired: true, ChallengeId: challenge.Id);
        }

        return await IssueSessionAsync(user, roles, facility.Value, deviceId, clientType, RestrictionFor(user, roles), http, ct);
    }

    public async Task<Result<AuthResult>> VerifyMfaAsync(MfaVerifyRequest request, HttpContext http, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var challenge = await db.Set<AuthChallenge>().FirstOrDefaultAsync(c => c.Id == request.ChallengeId && c.Kind == AuthChallengeKind.Mfa, ct);
        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= now || challenge.Attempts >= MaxChallengeAttempts || challenge.UserId is null)
        {
            return ChallengeExpired;
        }

        challenge.Attempts++;
        var user = await LoadUserAsync(u => u.Id == challenge.UserId, ct);
        if (user is null || !user.IsActive || user.LockoutEndsAt > now)
        {
            await db.SaveChangesAsync(ct);
            return ChallengeExpired;
        }

        // As for passwords: codes sent at once are judged one at a time, against the account as read under its row lock,
        // so that each wrong code counts and none gets in once the others have locked the account (ASVS V6.3.1). The code
        // is checked after the account is read again, so that it is checked against the last code used.
        long timeStep;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAndReloadAsync(user, ct);
            if (user.LockoutEndsAt > now)
            {
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return ChallengeExpired;
            }

            if (!VerifyTotp(user, request.Code, out timeStep))
            {
                // Whoever is guessing codes already knows the password, so a wrong code counts against the account like a
                // wrong password, whichever sign-in attempt it comes in.
                var locked = SignInPolicy.RecordFailure(Security, user, now);
                audit.Add(new AuditEntry(AuditCategories.Auth, "auth.mfa_failed", locked ? "Wrong authenticator code; the account is locked" : "Wrong authenticator code")
                {
                    UserId = user.Id,
                    UserName = user.UserName,
                    Severity = locked ? AuditSeverity.Critical : AuditSeverity.Warning,
                });
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return Error.Field("code", "The code is incorrect or has expired.");
            }
        }

        user.MfaLastTimeStep = timeStep;
        challenge.ConsumedAt = now;
        var roles = RoleNames(user);
        var facility = challenge.FacilityId is { } facilityId
            ? await db.Set<Facility>().FirstOrDefaultAsync(f => f.Id == facilityId, ct)
            : null;

        return await IssueSessionAsync(user, roles, facility, challenge.DeviceId, challenge.ClientType, RestrictionFor(user, roles), http, ct);
    }

    // ---------------------------------------------------------------- patient portal OTP

    public async Task<Result<OtpChallengeResponse>> RequestOtpAsync(OtpRequest request, CancellationToken ct)
    {
        var phone = SharedKernel.Phone.Normalize(request.Phone);
        if (!SharedKernel.Phone.IsIndianMobile(phone))
        {
            return Error.Field("phone", "Enter a valid 10-digit mobile number.");
        }

        // Requests for one number take turns under a lock on it, so codes asked for at once, from many machines, cannot get
        // past three in 15 minutes: each code sent is five more guesses at the account.
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"auth.otp:" + phone}, 0))", ct);
        var windowStart = now.AddMinutes(-15);
        var recent = await db.Set<AuthChallenge>().CountAsync(c => c.Kind == AuthChallengeKind.Otp && c.Phone == phone && c.CreatedAt > windowStart, ct);
        if (recent >= 3)
        {
            return Error.Rule("auth.otp_throttled", "Too many codes requested for this number. Please wait a few minutes.");
        }

        var code = SecretTokens.NewNumericCode(6);
        var challenge = new AuthChallenge
        {
            Kind = AuthChallengeKind.Otp,
            Phone = phone,
            ClientType = "portal",
            CreatedAt = now,
            ExpiresAt = now + OtpLifetime,
        };
        challenge.CodeHash = SecretTokens.Hash($"{challenge.Id:N}:{code}");
        db.Add(challenge);

        notifications.Queue(
            "auth.otp",
            new NotificationRecipient(phone, null, null, null),
            new Dictionary<string, string> { ["code"] = code, ["minutes"] = ((int)OtpLifetime.TotalMinutes).ToString(CultureInfo.InvariantCulture) },
            [NotificationChannel.Sms],
            OtpLifetime);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new OtpChallengeResponse(challenge.Id, challenge.ExpiresAt, SharedKernel.Phone.Mask(phone));
    }

    /// <summary>
    /// The number a portal sign-in code was sent to. Giving a code names only its challenge, so the anti-automation check
    /// (SRS SEC-06) looks the number up this way, by key, when an attempt fails, to count the failure against it.
    /// </summary>
    public Task<string?> OtpPhoneAsync(Guid challengeId, CancellationToken ct) =>
        db.Set<AuthChallenge>().AsNoTracking()
            .Where(c => c.Id == challengeId && c.Kind == AuthChallengeKind.Otp)
            .Select(c => c.Phone)
            .FirstOrDefaultAsync(ct);

    public async Task<Result<AuthResult>> VerifyOtpAsync(OtpVerifyRequest request, HttpContext http, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        // A portal account has no lock: the five tries of each code, and three codes per number in 15 minutes, are what
        // stop guessing. So codes sent at once, from many machines, are judged one at a time under the challenge's row
        // lock, as staff passwords and authenticator codes are under the account's: each try counts, none is judged after
        // the fifth, and a code is used once (ASVS V6.3.1).
        AuthChallenge? challenge;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT 1 FROM identity.auth_challenges WHERE id = {request.ChallengeId} FOR NO KEY UPDATE", ct);
            challenge = await db.Set<AuthChallenge>().FirstOrDefaultAsync(c => c.Id == request.ChallengeId && c.Kind == AuthChallengeKind.Otp, ct);
            if (challenge?.Phone is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= now || challenge.Attempts >= MaxChallengeAttempts)
            {
                return ChallengeExpired;
            }

            challenge.Attempts++;
            var expected = SecretTokens.Hash($"{challenge.Id:N}:{(request.Code ?? string.Empty).Trim()}");
            var correct = challenge.CodeHash is not null && SecretTokens.FixedTimeEquals(challenge.CodeHash, expected);
            if (correct)
            {
                challenge.ConsumedAt = now;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            if (!correct)
            {
                return Error.Field("code", "The code is incorrect.");
            }
        }

        var user = await LoadUserAsync(u => u.Type == UserType.Patient && u.Phone == challenge.Phone, ct);
        if (user is null)
        {
            var account = await portalAccounts.ResolveAsync(challenge.Phone, ct);
            var digits = challenge.Phone.TrimStart('+');
            user = new User
            {
                UserName = "p" + digits,
                NormalizedUserName = "p" + digits,
                DisplayName = account?.DisplayName ?? SharedKernel.Phone.Mask(challenge.Phone),
                Type = UserType.Patient,
                Phone = challenge.Phone,
                PatientId = account?.PatientId,
            };
            var patientRole = await db.Set<Role>().FirstOrDefaultAsync(r => r.Name == SystemRoles.Patient, ct);
            if (patientRole is not null)
            {
                user.Roles.Add(new UserRole { UserId = user.Id, RoleId = patientRole.Id, Role = patientRole });
            }

            db.Add(user);
        }
        else if (user.PatientId is null && await portalAccounts.ResolveAsync(challenge.Phone, ct) is { PatientId: { } patientId })
        {
            user.PatientId = patientId;
        }

        if (!user.IsActive)
        {
            await db.SaveChangesAsync(ct);
            return Error.Forbidden("auth.inactive", "This portal account is disabled. Please contact the hospital.");
        }

        return await IssueSessionAsync(user, [SystemRoles.Patient], null, null, "portal", null, http, ct);
    }

    // ---------------------------------------------------------------- refresh / sign-out

    public async Task<Result<AuthResult>> RefreshAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.Request.Headers.ContainsKey(ClientHeader))
        {
            return Error.Forbidden("auth.client_header", $"Missing {ClientHeader} header.");
        }

        var raw = http.Request.Cookies[RefreshCookieFor(ClientTypeOf(http))];
        if (string.IsNullOrEmpty(raw))
        {
            return SessionEnded;
        }

        var now = time.GetUtcNow();
        var hash = SecretTokens.Hash(raw);
        var token = await db.Set<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var session = token is null ? null : await db.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == token.SessionId, ct);

        if (token is null || session is null || session.RevokedAt is not null || session.ExpiresAt <= now || token.ExpiresAt <= now
            || RefreshCookieFor(session.ClientType) != RefreshCookieFor(ClientTypeOf(http)))
        {
            ClearRefreshCookie(http);
            return SessionEnded;
        }

        // Spent past the grace, or retired by a retry: two parties hold this session's tokens, so it ends for both.
        if (token.RetiredAt is not null || (token.UsedAt is { } usedAt && now - usedAt > RefreshReuseGrace))
        {
            session.RevokedAt = now;
            session.RevokedReason = "refresh_token_reuse";
            audit.Add(new AuditEntry(AuditCategories.Security, "auth.refresh_reuse_detected", "Refresh token replayed; session revoked")
            {
                UserId = session.UserId,
                Severity = AuditSeverity.Critical,
            });
            await db.SaveChangesAsync(ct);
            sessionValidator.Invalidate(session.Id);
            ClearRefreshCookie(http);
            return SessionEnded;
        }

        // Presented again within the grace: the previous response was probably lost on the WiFi link. Retire the
        // replacement the client never saw. If it did see it, and this is a copy being replayed, the client presents the
        // retired token next and the session ends.
        if (token.UsedAt is not null && token.ReplacedById is { } replacedId)
        {
            await db.Set<RefreshToken>().Where(t => t.Id == replacedId).ExecuteUpdateAsync(s => s.SetProperty(t => t.RetiredAt, now), ct);
        }

        var user = await LoadUserAsync(u => u.Id == session.UserId, ct);
        if (user is null || !user.IsActive)
        {
            session.RevokedAt = now;
            session.RevokedReason = "user_inactive";
            await db.SaveChangesAsync(ct);
            ClearRefreshCookie(http);
            return SessionEnded;
        }

        // Nobody has used the app for too long: the session ends (ASVS V7.3.1). Use is what the app reports, since the
        // refresh itself happens in the background; a locked screen's reports are not use, as nobody has unlocked it.
        if (session.LockedAt is null)
        {
            RecordActivity(session, http, now);
        }

        if (now - session.LastActiveAt > IdleLimit(session))
        {
            EndIdle(session, user, now);
            await db.SaveChangesAsync(ct);
            sessionValidator.Invalidate(session.Id);
            ClearRefreshCookie(http);
            return SessionEnded;
        }

        var roles = RoleNames(user);
        var locks = session.LockedAt is null && await IdleLockMinutesAsync(user, roles, session, ct) is > 0 and var lockMinutes
            && now - session.LastActiveAt >= TimeSpan.FromMinutes(lockMinutes);
        if (locks)
        {
            // The app should have locked the screen itself: it may have been kept from it, or the request was lost.
            Lock(session, user, now, "Screen locked by the server: nobody had used it for the idle lock time");
        }

        token.UsedAt ??= now; // the grace runs from the first use: retries do not extend it
        var next = CreateRefreshToken(session, now);
        token.ReplacedById = next.Entity.Id;
        session.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        if (locks)
        {
            sessionValidator.Invalidate(session.Id);
        }

        SetRefreshCookie(http, session.ClientType, next.Raw, next.Entity.ExpiresAt);
        var restriction = RestrictionFor(user, roles, session);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, session.DeviceId, restriction, ct), Restriction: restriction);
    }

    public async Task<Result> LogoutAsync(HttpContext http, CancellationToken ct)
    {
        // As for refresh: the custom header cannot be sent cross-site without a CORS preflight the server never grants.
        if (!http.Request.Headers.ContainsKey(ClientHeader))
        {
            return Error.Forbidden("auth.client_header", $"Missing {ClientHeader} header.");
        }

        var now = time.GetUtcNow();
        UserSession? session = null;
        if (http.Request.Cookies[RefreshCookieFor(ClientTypeOf(http))] is { Length: > 0 } raw)
        {
            var hash = SecretTokens.Hash(raw);
            var token = await db.Set<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
            session = token is null ? null : await db.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == token.SessionId, ct);
        }

        if (session is null && currentUser.SessionId is { } sessionId)
        {
            session = await db.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        }

        if (session is { RevokedAt: null })
        {
            session.RevokedAt = now;
            session.RevokedReason = "signed_out";
            audit.Add(new AuditEntry(AuditCategories.Auth, "auth.logout", "Signed out") { UserId = session.UserId });
            await db.SaveChangesAsync(ct);
            sessionValidator.Invalidate(session.Id);
        }

        ClearRefreshCookie(http);
        return Result.Success();
    }

    // ---------------------------------------------------------------- current user

    public async Task<Result<AuthUser>> GetMeAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return SessionEnded;
        }

        var user = await LoadUserAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return SessionEnded;
        }

        var roles = RoleNames(user);
        var locked = currentUser.SessionId is { } sessionId
                     && await db.Set<UserSession>().AnyAsync(s => s.Id == sessionId && s.LockedAt != null, ct);
        return await BuildAuthUserAsync(user, roles, currentUser.FacilityId, currentUser.DeviceId, RestrictionFor(user, roles) ?? (locked ? RestrictionLocked : null), ct);
    }

    public async Task<Result<AuthResult>> SwitchFacilityAsync(SwitchFacilityRequest request, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return SessionEnded;
        }

        var roles = RoleNames(user);
        var allowed = await AllowedFacilitiesAsync(user, roles, ct);
        if (allowed.All(f => f.Id != request.FacilityId))
        {
            return Error.Forbidden("auth.facility_denied", "You do not have access to that facility.");
        }

        session.FacilityId = request.FacilityId;
        audit.Add(new AuditEntry(AuditCategories.Auth, "auth.facility_switched", "Switched facility") { EntityType = nameof(Facility), EntityId = request.FacilityId.ToString() });
        await db.SaveChangesAsync(ct);

        var restriction = RestrictionFor(user, roles);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, session.DeviceId, restriction, ct), Restriction: restriction);
    }

    public async Task<Result<AuthResult>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user?.PasswordHash is null || session is null)
        {
            return SessionEnded;
        }

        if (!await PasswordConfirmedAsync(user, request.CurrentPassword, ct))
        {
            return Error.Field("currentPassword", "Current password is incorrect.");
        }

        var errors = passwordPolicy.Validate(request.NewPassword, user.UserName, user.DisplayName, await FacilityNamesAsync(db, ct));
        if (errors.Count > 0)
        {
            return Error.Validation("auth.weak_password", errors[0], new Dictionary<string, string[]> { ["newPassword"] = errors.ToArray() });
        }

        if (hashing.Verify(user.PasswordHash, request.NewPassword).Succeeded)
        {
            return Error.Field("newPassword", "Choose a password you have not used just now.");
        }

        var now = time.GetUtcNow();
        user.PasswordHash = hashing.Hash(request.NewPassword);
        user.PasswordChangedAt = now;
        user.MustChangePassword = false;
        user.PasswordBreachedAt = null;
        user.TemporaryPasswordExpiresAt = null;

        // Sign out every other session of this user.
        var others = await EndSessionsAsync(OtherSessionsOf(user, session), "password_changed", now, ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.password_changed", "Password changed") { UserId = user.Id });
        CredentialNotices.QueueEmail(notifications, user, CredentialNotices.PasswordChanged, now);
        await db.SaveChangesAsync(ct);
        InvalidateAll(others);
        await CredentialNotices.SendInAppAsync(notifications, user, CredentialNotices.PasswordChanged, ct);

        // The first administrator now has a password of their own: the one-time password's file is no longer needed.
        if (user.UserName == InitialAdminPassword.UserName)
        {
            InitialAdminPassword.Delete(new DataDirectories(options.Value));
        }

        var roles = RoleNames(user);
        var restriction = RestrictionFor(user, roles);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, session.DeviceId, restriction, ct), Restriction: restriction);
    }

    // ---------------------------------------------------------------- idle lock screen

    public async Task<Result> SetPinAsync(SetPinRequest request, CancellationToken ct)
    {
        var (user, _) = await CurrentUserAndSessionAsync(ct);
        if (user?.PasswordHash is null)
        {
            return SessionEnded;
        }

        if (!await PasswordConfirmedAsync(user, request.Password, ct))
        {
            return Error.Field("password", "Password is incorrect.");
        }

        user.PinHash = hashing.Hash(request.Pin);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.pin_set", "Quick-unlock PIN set") { UserId = user.Id });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Locks the screen of this session, as the app does after the idle lock time or when asked to (Ctrl+Shift+L).
    /// Until it is unlocked, the session's tokens reach nothing else (<see cref="SessionLock"/>). Patient portal sessions
    /// have no lock screen: they end after a quiet spell instead.
    /// </summary>
    public async Task<Result> LockAsync(CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return SessionEnded;
        }

        if (session.LockedAt is null && IsStaffSession(session) && RestrictionFor(user, RoleNames(user)) is null)
        {
            Lock(session, user, time.GetUtcNow(), "Screen locked");
            await db.SaveChangesAsync(ct);
            sessionValidator.Invalidate(session.Id);
        }

        return Result.Success();
    }

    public async Task<Result<AuthResult>> UnlockAsync(UnlockRequest request, HttpContext http, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return SessionEnded;
        }

        var withPin = request.Pin is { Length: > 0 } && user.PinHash is not null;
        var ok = withPin
            ? hashing.Verify(user.PinHash!, request.Pin!).Succeeded
            : request.Password is { Length: > 0 } password && user.PasswordHash is not null && hashing.Verify(user.PasswordHash, password).Succeeded;
        return await FinishUnlockAsync(
            user,
            session,
            ok,
            withPin ? "Screen unlocked with the PIN" : "Screen unlocked with the password",
            Error.Field(request.Pin is { Length: > 0 } ? "pin" : "password", "That did not match. Try again."),
            http,
            ct);
    }

    /// <summary>
    /// Unlocks the screen of <paramref name="session"/> once its user has proven who they are (<paramref name="verified"/>),
    /// with the PIN, the password or a device set up for it (<see cref="WebAuthnUnlockService"/>), or counts a failed try:
    /// <see cref="SecurityOptions.MaxPinAttempts"/> of them end the session. Whatever the proof, it only resumes this
    /// session, and only while it is still open: a session that ended, or that nobody used for the idle limit, is not
    /// unlocked but ended, and its user signs in again (ASVS V7.3.1).
    /// </summary>
    /// <param name="unlockedWith">The audit trail's description of the unlock.</param>
    /// <param name="mismatch">The answer to a failed try.</param>
    internal async Task<Result<AuthResult>> FinishUnlockAsync(
        User user, UserSession session, bool verified, string unlockedWith, Error mismatch, HttpContext http, CancellationToken ct)
    {
        // Attempts sent at once all read the session before any of them was counted. Read it again under its row lock, one
        // attempt at a time, so that each wrong PIN counts and none is judged once the session has ended (ASVS V6.3.1).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM identity.user_sessions WHERE id = {session.Id} FOR NO KEY UPDATE", ct);
        await db.Entry(session).ReloadAsync(ct);
        var now = time.GetUtcNow();
        if (session.RevokedAt is not null || session.ExpiresAt <= now)
        {
            return SessionEnded;
        }

        // Nobody has used it for the idle limit: the next refresh would end it, so no unlock resumes it meanwhile.
        if (now - session.LastActiveAt > IdleLimit(session))
        {
            EndIdle(session, user, now);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            sessionValidator.Invalidate(session.Id);
            ClearRefreshCookie(http, session.ClientType);
            return SessionEnded;
        }

        if (verified)
        {
            session.FailedUnlockAttempts = 0;
            session.LastActiveAt = now;
            if (session.LockedAt is not null)
            {
                session.LockedAt = null;
                audit.Add(new AuditEntry(AuditCategories.Auth, "auth.screen_unlocked", unlockedWith)
                {
                    UserId = user.Id,
                    UserName = user.UserName,
                });
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            sessionValidator.Invalidate(session.Id);

            var roles = RoleNames(user);
            var restriction = RestrictionFor(user, roles);
            var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
            return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, session.DeviceId, restriction, ct), Restriction: restriction);
        }

        session.FailedUnlockAttempts++;
        if (session.FailedUnlockAttempts >= Security.MaxPinAttempts)
        {
            session.RevokedAt = now;
            session.RevokedReason = "unlock_attempts_exceeded";
            audit.Add(new AuditEntry(AuditCategories.Security, "auth.unlock_locked_out", "Too many unlock attempts; session ended")
            {
                UserId = user.Id,
                Severity = AuditSeverity.Warning,
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            sessionValidator.Invalidate(session.Id);
            ClearRefreshCookie(http, session.ClientType);
            return SessionEnded;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return mismatch;
    }

    // ---------------------------------------------------------------- MFA enrolment

    /// <summary>Starts setting up two-step verification; the password is asked again first (ASVS V7.5.1).</summary>
    public async Task<Result<MfaSetupResponse>> BeginMfaSetupAsync(MfaSetupRequest request, CancellationToken ct)
    {
        var (user, _) = await CurrentUserAndSessionAsync(ct);
        if (user is null)
        {
            return SessionEnded;
        }

        if (user.MfaEnabled)
        {
            return Error.Rule("auth.mfa_already_enabled", "Two-step verification is already enabled.");
        }

        if (!await PasswordConfirmedAsync(user, request.Password, ct))
        {
            return Error.Field("password", "Password is incorrect.");
        }

        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        user.MfaSecret = protector.Protect(secret);
        await db.SaveChangesAsync(ct);

        var issuer = Uri.EscapeDataString("HC360 " + options.Value.DefaultFacilityName);
        var account = Uri.EscapeDataString(user.UserName);
        return new MfaSetupResponse(secret, $"otpauth://totp/{issuer}:{account}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30");
    }

    public async Task<Result<AuthResult>> EnableMfaAsync(MfaEnableRequest request, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return SessionEnded;
        }

        if (user.MfaSecret is null)
        {
            return Error.Rule("auth.mfa_not_started", "Start two-step verification setup first.");
        }

        if (!VerifyTotp(user, request.Code, out var timeStep))
        {
            return Error.Field("code", "The code is incorrect. Check the time on your phone and try again.");
        }

        var now = time.GetUtcNow();
        user.MfaEnabled = true;
        user.MfaLastTimeStep = timeStep;

        // Sessions signed in before need no code: end them (ASVS V7.4.3).
        var others = await EndSessionsAsync(OtherSessionsOf(user, session), "mfa_enabled", now, ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.mfa_enabled", "Two-step verification enabled") { UserId = user.Id });
        CredentialNotices.QueueEmail(notifications, user, CredentialNotices.MfaEnabled, now);
        await db.SaveChangesAsync(ct);
        InvalidateAll(others);
        await CredentialNotices.SendInAppAsync(notifications, user, CredentialNotices.MfaEnabled, ct);

        var roles = RoleNames(user);
        var restriction = RestrictionFor(user, roles);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, session.DeviceId, restriction, ct), Restriction: restriction);
    }

    public async Task<Result> DisableMfaAsync(MfaDisableRequest request, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user?.PasswordHash is null || session is null)
        {
            return SessionEnded;
        }

        if (RequiresMfa(user, RoleNames(user)))
        {
            return Error.Forbidden("auth.mfa_required", "Your account requires two-step verification.");
        }

        if (!await PasswordConfirmedAsync(user, request.Password, ct))
        {
            return Error.Field("password", "Password is incorrect.");
        }

        var now = time.GetUtcNow();
        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaLastTimeStep = null;

        // Whoever turned it off may not be the account's owner: end the account's other sessions (ASVS V7.4.3).
        var others = await EndSessionsAsync(OtherSessionsOf(user, session), "mfa_disabled", now, ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.mfa_disabled", "Two-step verification disabled") { UserId = user.Id, Severity = AuditSeverity.Warning });
        CredentialNotices.QueueEmail(notifications, user, CredentialNotices.MfaDisabled, now);
        await db.SaveChangesAsync(ct);
        InvalidateAll(others);
        await CredentialNotices.SendInAppAsync(notifications, user, CredentialNotices.MfaDisabled, ct);
        return Result.Success();
    }

    // ---------------------------------------------------------------- sessions

    /// <summary>The signed-in user's sessions that are still open: not ended, expired or left idle too long.</summary>
    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var sessions = (await db.Set<UserSession>()
                .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
                .ToListAsync(ct))
            .Where(s => IsOpen(s, now))
            .OrderByDescending(s => s.LastActiveAt)
            .ToList();
        var deviceIds = sessions.Where(s => s.DeviceId != null).Select(s => s.DeviceId!.Value).Distinct().ToList();
        var devices = await db.Set<Device>().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return sessions.Select(s => new SessionInfo(
            s.Id, s.ClientType, s.CreatedAt, s.LastActiveAt, s.ExpiresAt, s.IpAddress, s.UserAgent,
            s.DeviceId is { } d && devices.TryGetValue(d, out var name) ? name : null,
            s.Id == currentUser.SessionId,
            s.LockedAt is not null)).ToList();
    }

    /// <summary>
    /// Checks the signed-in user's password again before a sensitive action elsewhere, such as making a new backup recovery
    /// key, which is shown on screen.
    /// </summary>
    public async Task<Result> ConfirmPasswordAsync(ReauthenticateRequest request, CancellationToken ct)
    {
        var (user, _) = await CurrentUserAndSessionAsync(ct);
        if (user is null)
        {
            return SessionEnded;
        }

        return await PasswordConfirmedAsync(user, request.Password, ct) ? Result.Success() : Error.Field("password", "Password is incorrect.");
    }

    /// <summary>Signs out one of the user's sessions, once the password is given again (ASVS V7.5.2).</summary>
    public async Task<Result> EndSessionAsync(Guid sessionId, ReauthenticateRequest request, CancellationToken ct)
    {
        var (user, _) = await CurrentUserAndSessionAsync(ct);
        if (user is null)
        {
            return SessionEnded;
        }

        if (!await PasswordConfirmedAsync(user, request.Password, ct))
        {
            return Error.Field("password", "Password is incorrect.");
        }

        var target = db.Set<UserSession>().Where(s => s.Id == sessionId && s.UserId == user.Id);
        if (!await target.AnyAsync(ct))
        {
            return Error.NotFound("Session", sessionId);
        }

        var ended = await EndSessionsAsync(target, "ended_by_user", time.GetUtcNow(), ct);
        audit.Add(new AuditEntry(AuditCategories.Auth, "auth.session_ended", "Signed out one of their sessions") { UserId = user.Id, UserName = user.UserName });
        await db.SaveChangesAsync(ct);
        InvalidateAll(ended);
        return Result.Success();
    }

    /// <summary>Signs out every other session of the user ("sign out everywhere else"), once the password is given again.</summary>
    public async Task<Result<SessionsEnded>> EndOtherSessionsAsync(ReauthenticateRequest request, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return SessionEnded;
        }

        if (!await PasswordConfirmedAsync(user, request.Password, ct))
        {
            return Error.Field("password", "Password is incorrect.");
        }

        var ended = await EndSessionsAsync(OtherSessionsOf(user, session), "ended_by_user", time.GetUtcNow(), ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.other_sessions_ended", $"Signed out everywhere else ({ended.Count} session(s))")
        {
            UserId = user.Id,
            UserName = user.UserName,
        });
        await db.SaveChangesAsync(ct);
        InvalidateAll(ended);
        return new SessionsEnded(ended.Count);
    }

    /// <summary>
    /// Signs out everybody, staff and patients, except the administrator's own session: for a suspected break-in, or
    /// after a leaked password list (ASVS V7.4.5). The administrator's password is asked again first.
    /// </summary>
    public async Task<Result<SessionsEnded>> EndAllSessionsAsync(ReauthenticateRequest request, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return SessionEnded;
        }

        if (!await PasswordConfirmedAsync(user, request.Password, ct))
        {
            return Error.Field("password", "Password is incorrect.");
        }

        var now = time.GetUtcNow();
        var ended = await EndSessionsAsync(db.Set<UserSession>().Where(s => s.Id != session.Id && s.ExpiresAt > now), "ended_by_administrator", now, ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.all_sessions_ended", $"Signed out everybody else: {ended.Count} session(s) ended")
        {
            UserId = user.Id,
            UserName = user.UserName,
            Severity = AuditSeverity.Critical,
        });
        await db.SaveChangesAsync(ct);
        InvalidateAll(ended);
        return new SessionsEnded(ended.Count);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<Result<AuthResult>> IssueSessionAsync(
        User user,
        IReadOnlyList<string> roles,
        Facility? facility,
        Guid? deviceId,
        string clientType,
        string? restriction,
        HttpContext http,
        CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var session = new UserSession
        {
            UserId = user.Id,
            FacilityId = facility?.Id,
            DeviceId = deviceId,
            ClientType = clientType,
            CreatedAt = now,
            LastSeenAt = now,
            LastActiveAt = now,
            ExpiresAt = now.AddHours(Security.SessionHours),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 512),
        };
        db.Add(session);
        var refresh = CreateRefreshToken(session, now);
        var ended = await EnforceSessionLimitAsync(user, session, now, ct);

        user.FailedLoginCount = 0;
        user.LastFailedLoginAt = null;
        user.LockoutEndsAt = null;
        user.LastLoginAt = now;
        audit.Add(new AuditEntry(AuditCategories.Auth, "auth.login", $"Signed in ({clientType})") { UserId = user.Id, UserName = user.UserName });
        await db.SaveChangesAsync(ct);
        InvalidateAll(ended);

        SetRefreshCookie(http, clientType, refresh.Raw, refresh.Entity.ExpiresAt);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, facility?.Id, deviceId, restriction, ct), Restriction: restriction);
    }

    private (string Raw, RefreshToken Entity) CreateRefreshToken(UserSession session, DateTimeOffset now)
    {
        var raw = SecretTokens.NewOpaqueToken();
        var idleExpiry = now + IdleLimit(session);
        var entity = new RefreshToken
        {
            SessionId = session.Id,
            TokenHash = SecretTokens.Hash(raw),
            CreatedAt = now,
            ExpiresAt = idleExpiry < session.ExpiresAt ? idleExpiry : session.ExpiresAt,
        };
        db.Add(entity);
        return (raw, entity);
    }

    private static AccessTokenSubject Subject(User user, IReadOnlyList<string> roles, UserSession session, string? restriction) => new(
        user.Id,
        user.UserName,
        user.DisplayName,
        user.Type == UserType.Patient ? HcClaims.PatientType : HcClaims.StaffType,
        session.Id,
        session.FacilityId,
        session.DeviceId,
        user.PractitionerId,
        user.PatientId,
        roles,
        restriction);

    private async Task<AuthUser> BuildAuthUserAsync(User user, IReadOnlyList<string> roles, Guid? facilityId, Guid? deviceId, string? restriction, CancellationToken ct)
    {
        // A locked session's screens stay as they were behind the lock screen, so its user keeps the permissions; the
        // server refuses the requests themselves (SessionLock).
        var held = restriction is null or RestrictionLocked ? await permissions.GetPermissionsAsync(user.Id, ct) : new HashSet<string>();

        // The app is given what the server allows the session: administration only on an enrolled desktop (SRS SEC-08).
        var granted = deviceId is null && held.Any(PlatformPermissions.IsAdministrative) && await administrationDesktops.RequiredAsync(ct)
            ? AdministrationOnDesktop.Elsewhere(held, permissionRegistry, firstEnrolment: !await administrationDesktops.EnrolledAsync(ct))
            : held;
        var facilities = await AllowedFacilitiesAsync(user, roles, ct);

        return new AuthUser(
            user.Id,
            user.UserName,
            user.DisplayName,
            user.Type.ToString().ToLowerInvariant(),
            facilityId,
            facilities.FirstOrDefault(f => f.Id == facilityId)?.Name,
            user.PractitionerId,
            user.PatientId,
            roles,
            granted,
            facilities,
            user.MfaEnabled,
            user.PinHash is not null,
            user.MustChangePassword,
            restriction,
            user.MustChangePassword && user.PasswordBreachedAt is not null,
            user.Type == UserType.Staff && await BiometricUnlockAllowedAsync(facilityId, ct),
            held.Any(p => PlatformPermissions.IsAdministrative(p) && !granted.Contains(p)));
    }

    /// <summary>
    /// Whether the facility lets staff unlock the lock screen with a device's Windows Hello, fingerprint or face (the
    /// <see cref="BiometricUnlockSetting"/>: off unless an administrator set it to true, for the hospital or the facility).
    /// </summary>
    internal async Task<bool> BiometricUnlockAllowedAsync(Guid? facilityId, CancellationToken ct) =>
        await settings.GetAsync<JsonElement?>(BiometricUnlockSetting, facilityId, ct) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.String } text => string.Equals(text.GetString()?.Trim(), "true", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    /// <summary>The facilities' names and legal names, refused in passwords (ASVS V6.2.11).</summary>
    internal static async Task<List<string?>> FacilityNamesAsync(HospitalDbContext db, CancellationToken ct) =>
        (await db.Set<Facility>().IgnoreQueryFilters().Select(f => new { f.Name, f.LegalName }).ToListAsync(ct))
            .SelectMany(f => new[] { f.Name, f.LegalName })
            .ToList();

    private async Task<List<FacilityOption>> AllowedFacilitiesAsync(User user, IReadOnlyList<string> roles, CancellationToken ct)
    {
        // Patients act only on their own records, never in a facility's name (a facility session would, for one, receive
        // the facility's live events).
        if (user.Type == UserType.Patient)
        {
            return [];
        }

        var query = db.Set<Facility>().Where(f => f.IsActive);
        if (!roles.Contains(SystemRoles.SuperAdmin) && user.Facilities.Count > 0)
        {
            var ids = user.Facilities.Select(f => f.FacilityId).ToList();
            query = query.Where(f => ids.Contains(f.Id));
        }

        return await query.OrderBy(f => f.Name).Select(f => new FacilityOption(f.Id, f.Code, f.Name)).ToListAsync(ct);
    }

    private async Task<Result<Facility?>> ResolveFacilityAsync(User user, IReadOnlyList<string> roles, Guid? requested, CancellationToken ct)
    {
        var allowed = await AllowedFacilitiesAsync(user, roles, ct);
        if (allowed.Count == 0)
        {
            return Result<Facility?>.Ok(null);
        }

        var targetId = requested ?? user.DefaultFacilityId ?? allowed[0].Id;
        if (allowed.All(f => f.Id != targetId))
        {
            if (requested is not null)
            {
                return Error.Forbidden("auth.facility_denied", "You do not have access to that facility.");
            }

            targetId = allowed[0].Id;
        }

        return await db.Set<Facility>().FirstOrDefaultAsync(f => f.Id == targetId, ct);
    }

    private async Task<Guid?> VerifyDeviceAsync(Guid? deviceId, string? secret, CancellationToken ct)
    {
        if (deviceId is not { } id || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        // Only an HC360 Desktop installation counts: a lab agent's credential never satisfies the enrolled-device rule.
        var device = await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == id && !d.IsRevoked, ct);
        var matches = device is not null && SecretTokens.FixedTimeEquals(device.SecretHash, SecretTokens.Hash(secret));
        return matches && device!.Kind.IsDesktop() ? device.Id : null;
    }

    private bool VerifyTotp(User user, string? code, out long timeStep)
    {
        timeStep = 0;
        if (user.MfaSecret is null || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var secret = protector.Unprotect(user.MfaSecret)!;
        var totp = new Totp(Base32Encoding.ToBytes(secret));
        var ok = totp.VerifyTotp(time.GetUtcNow().UtcDateTime, code.Trim(), out timeStep, VerificationWindow.RfcSpecifiedNetworkDelay);
        return ok && timeStep > (user.MfaLastTimeStep ?? 0);
    }

    private string? RestrictionFor(User user, IReadOnlyList<string> roles) =>
        user.MustChangePassword ? RestrictionPasswordChange
        : RequiresMfa(user, roles) && !user.MfaEnabled ? RestrictionMfaSetup
        : null;

    /// <summary>The account must change its password or set up two-step verification before anything else.</summary>
    internal bool IsRestricted(User user) => RestrictionFor(user, RoleNames(user)) is not null;

    /// <summary>The account's restriction, else the session's lock.</summary>
    private string? RestrictionFor(User user, IReadOnlyList<string> roles, UserSession session) =>
        RestrictionFor(user, roles) ?? (session.LockedAt is null ? null : RestrictionLocked);

    private bool RequiresMfa(User user, IReadOnlyList<string> roles) =>
        SignInPolicy.RequiresMfa(Security, roles, user.Roles.SelectMany(r => r.Role?.Permissions ?? []).Select(p => p.Permission));

    /// <summary>
    /// Checks the password a signed-in user gives again to confirm an action: changing it, setting a PIN, two-step
    /// verification, signing out sessions, key and backup actions. A wrong one counts against the account as at sign-in,
    /// because whoever guesses here holds a session, say on a workstation left signed in, but not the password. While the
    /// account is locked no password is accepted, so the lock stops guessing here too (ASVS V6.3.1).
    /// </summary>
    private async Task<bool> PasswordConfirmedAsync(User user, string? password, CancellationToken ct)
    {
        if (user.PasswordHash is null || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var matches = hashing.Verify(user.PasswordHash, password).Succeeded;
        var now = time.GetUtcNow();

        // As at sign-in: attempts sent at once are counted one at a time under the account's row lock.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAndReloadAsync(user, ct);
        if (user.LockoutEndsAt > now)
        {
            await AuditLockedAsync(user, "Password given to confirm an action while locked", ct);
            return false;
        }

        if (matches)
        {
            if (user.FailedLoginCount > 0)
            {
                user.FailedLoginCount = 0;
                user.LastFailedLoginAt = null;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }

            return true;
        }

        var locked = SignInPolicy.RecordFailure(Security, user, now);
        audit.Add(new AuditEntry(AuditCategories.Auth, "auth.reauthentication_failed", "Wrong password given to confirm an action")
        {
            UserId = user.Id,
            UserName = user.UserName,
            Severity = locked ? AuditSeverity.Critical : AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return false;
    }

    /// <summary>Staff sessions (browser and HC360 Desktop) lock; patient portal sessions end instead.</summary>
    private static bool IsStaffSession(UserSession session) => session.ClientType != "portal";

    /// <summary>How long a session may go unused before it ends.</summary>
    private TimeSpan IdleLimit(UserSession session) =>
        TimeSpan.FromMinutes(IsStaffSession(session) ? Security.SessionIdleMinutes : Security.PortalSessionIdleMinutes);

    /// <summary>Not ended, expired, or left unused past the idle limit (which the next refresh would end).</summary>
    private bool IsOpen(UserSession session, DateTimeOffset now) =>
        session.RevokedAt is null && session.ExpiresAt > now && now - session.LastActiveAt <= IdleLimit(session);

    /// <summary>
    /// Takes the app's report of when someone last used it (<see cref="IdleHeader"/>); it can only move forward. An app
    /// that sends none, still open from before the server learned to read it, counts each refresh as use, as it did
    /// then, until the page is reloaded. A client could claim use either way, so this weakens nothing: the idle lock
    /// is there for a workstation left unattended, which runs the hospital's own app.
    /// </summary>
    private static void RecordActivity(UserSession session, HttpContext http, DateTimeOffset now)
    {
        var activeAt = int.TryParse(http.Request.Headers[IdleHeader].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var idleSeconds)
            ? now - TimeSpan.FromSeconds(idleSeconds)
            : now;
        if (activeAt > session.LastActiveAt)
        {
            session.LastActiveAt = activeAt;
        }
    }

    /// <summary>Ends a session nobody has used for its idle limit (ASVS V7.3.1); save, then invalidate it.</summary>
    private void EndIdle(UserSession session, User user, DateTimeOffset now)
    {
        session.RevokedAt = now;
        session.RevokedReason = "idle_timeout";
        audit.Add(new AuditEntry(AuditCategories.Auth, "auth.session_idle_ended",
            $"Session ended after {IdleLimit(session).TotalMinutes:0} minutes without use ({session.ClientType})")
        {
            UserId = user.Id,
            UserName = user.UserName,
        });
    }

    private void Lock(UserSession session, User user, DateTimeOffset now, string description)
    {
        session.LockedAt = now;
        audit.Add(new AuditEntry(AuditCategories.Auth, "auth.screen_locked", description) { UserId = user.Id, UserName = user.UserName });
    }

    /// <summary>
    /// The idle lock time of a staff session (the <see cref="IdleLockMinutesSetting"/> of its facility, else the
    /// hospital's), or 0 when it does not lock: a portal session, or a session that must first change its password or
    /// set up two-step verification (it shows only that screen).
    /// </summary>
    private async Task<int> IdleLockMinutesAsync(User user, IReadOnlyList<string> roles, UserSession session, CancellationToken ct) =>
        IsStaffSession(session) && RestrictionFor(user, roles) is null
            ? await IntSettingAsync(IdleLockMinutesSetting, session.FacilityId, DefaultIdleLockMinutes, ct)
            : 0;

    /// <summary>
    /// A staff account may have <see cref="MaxSessionsSetting"/> sessions at once (ASVS V7.1.2): signing in once more
    /// signs out the one used least recently. Returns the sessions it ended, to invalidate once saved.
    /// </summary>
    /// <remarks>
    /// Two sign-ins of one account at once cannot both find room under the limit: each also updates the account's row
    /// (its last sign-in), whose version (xmin) lets only the first commit. The other fails, its new session with it.
    /// </remarks>
    private async Task<List<UserSession>> EnforceSessionLimitAsync(User user, UserSession session, DateTimeOffset now, CancellationToken ct)
    {
        if (user.Type != UserType.Staff || await IntSettingAsync(MaxSessionsSetting, session.FacilityId, DefaultMaxSessions, ct) is not (> 0 and var max))
        {
            return [];
        }

        // The new session is not saved yet, so it is not among these.
        var open = (await db.Set<UserSession>().Where(s => s.UserId == user.Id && s.RevokedAt == null && s.ExpiresAt > now).ToListAsync(ct))
            .Where(s => IsOpen(s, now))
            .OrderBy(s => s.LastActiveAt)
            .ToList();
        var ended = open.Take(open.Count - (max - 1)).ToList();
        foreach (var oldest in ended)
        {
            oldest.RevokedAt = now;
            oldest.RevokedReason = "session_limit";
            audit.Add(new AuditEntry(AuditCategories.Security, "auth.session_limit",
                $"Signed out the session started {oldest.CreatedAt:yyyy-MM-dd HH:mm} UTC ({oldest.ClientType}, {oldest.IpAddress ?? "unknown address"}): " +
                $"the account may have {max} session(s) at once")
            {
                UserId = user.Id,
                UserName = user.UserName,
                Severity = AuditSeverity.Warning,
            });
        }

        return ended;
    }

    /// <summary>A whole-number setting; a value that is not one (or no value) gives the default.</summary>
    private async Task<int> IntSettingAsync(string key, Guid? facilityId, int fallback, CancellationToken ct)
    {
        var value = await settings.GetAsync<JsonElement?>(key, facilityId, ct);
        return value switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetInt32(out var n) => Math.Max(0, n),
            { ValueKind: JsonValueKind.String } text when int.TryParse(text.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => Math.Max(0, n),
            _ => fallback,
        };
    }

    private IQueryable<UserSession> OtherSessionsOf(User user, UserSession session) =>
        db.Set<UserSession>().Where(s => s.UserId == user.Id && s.Id != session.Id);

    /// <summary>Ends the sessions not ended yet; save, then <see cref="InvalidateAll"/>, so their tokens stop at once.</summary>
    private static async Task<List<UserSession>> EndSessionsAsync(IQueryable<UserSession> sessions, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var open = await sessions.Where(s => s.RevokedAt == null).ToListAsync(ct);
        foreach (var session in open)
        {
            session.RevokedAt = now;
            session.RevokedReason = reason;
        }

        return open;
    }

    private void InvalidateAll(IEnumerable<UserSession> sessions)
    {
        foreach (var session in sessions)
        {
            sessionValidator.Invalidate(session.Id);
        }
    }

    /// <summary>The signed-in user and their session, while it has not ended; nulls otherwise.</summary>
    internal async Task<(User? User, UserSession? Session)> CurrentUserAndSessionAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId || currentUser.SessionId is not { } sessionId)
        {
            return (null, null);
        }

        var user = await LoadUserAsync(u => u.Id == userId, ct);
        var session = await db.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == sessionId && s.RevokedAt == null, ct);
        return (user, session);
    }

    private Task<User?> LoadUserAsync(System.Linq.Expressions.Expression<Func<User, bool>> predicate, CancellationToken ct) =>
        db.Set<User>()
            .Include(u => u.Roles).ThenInclude(r => r.Role!).ThenInclude(r => r.Permissions)
            .Include(u => u.Facilities)
            .AsSplitQuery()
            .FirstOrDefaultAsync(predicate, ct);

    /// <summary>
    /// Locks the account's row until the caller's transaction ends, then reads the account again: it now shows every
    /// failure counted, and every lock set, by attempts that got there first. The lock is the one an update takes, so
    /// rows that refer to the account by foreign key, such as its roles, can still be written meanwhile.
    /// </summary>
    private async Task LockAndReloadAsync(User user, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM identity.users WHERE id = {user.Id} FOR NO KEY UPDATE", ct);
        await db.Entry(user).ReloadAsync(ct);
    }

    private Task AuditLockedAsync(User user, string summary, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditCategories.Auth, "auth.login_locked", summary)
        {
            UserId = user.Id,
            UserName = user.UserName,
            Severity = AuditSeverity.Warning,
        }, ct);

    private static List<string> RoleNames(User user) => user.Roles.Where(r => r.Role is not null).Select(r => r.Role!.Name).ToList();

    /// <summary>A request to the patient portal's address, which refreshes and signs out the portal's own sessions.</summary>
    internal static bool IsPortalRequest(HttpRequest request) => ClientTypeOf(request.HttpContext) == "portal";

    /// <summary>
    /// The app a request comes from. The portal is told by the address the request came to, not by what the request says
    /// (ASVS V3.5.4): browsers send the staff app's cookie to the portal's address too, and script on a portal page must
    /// not be able to present it as the staff app. Nothing on the staff app's address is the portal either.
    /// </summary>
    private static string ClientTypeOf(HttpContext http) =>
        Listeners.Of(http) == Listener.Portal ? "portal"
        : http.Request.Headers[ClientHeader].ToString() == "desktop" ? "desktop"
        : "web";

    /// <summary>The refresh cookie of an app: the portal's, or the staff app's (browser and HC360 Desktop).</summary>
    internal static string RefreshCookieFor(string clientType) => clientType == "portal" ? PortalRefreshCookieName : RefreshCookieName;

    private static void SetRefreshCookie(HttpContext http, string clientType, string token, DateTimeOffset expires) =>
        http.Response.Cookies.Append(RefreshCookieFor(clientType), token, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = RefreshCookiePath,
            Expires = expires,
            IsEssential = true,
        });

    /// <summary>Clears the refresh cookie of the given app, by default the app that sent the request.</summary>
    private static void ClearRefreshCookie(HttpContext http, string? clientType = null) =>
        http.Response.Cookies.Delete(RefreshCookieFor(clientType ?? ClientTypeOf(http)), new CookieOptions { Path = RefreshCookiePath, Secure = true, SameSite = SameSiteMode.Strict, HttpOnly = true });

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
