using System.Globalization;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OtpNet;

namespace HyFib.HealthCare360.Platform.Identity;

public sealed record LoginRequest(string UserName, string Password, Guid? FacilityId = null, Guid? DeviceId = null, string? DeviceSecret = null);

public sealed record MfaVerifyRequest(Guid ChallengeId, string Code);

public sealed record OtpRequest(string Phone);

public sealed record OtpVerifyRequest(Guid ChallengeId, string Code);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record SetPinRequest(string Pin, string Password);

public sealed record UnlockRequest(string? Pin, string? Password);

public sealed record SwitchFacilityRequest(Guid FacilityId);

public sealed record MfaEnableRequest(string Code);

public sealed record MfaDisableRequest(string Password);

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
    string? Restriction);

public sealed record AuthResult(
    string? AccessToken,
    DateTimeOffset? ExpiresAt,
    AuthUser? User,
    bool MfaRequired = false,
    Guid? ChallengeId = null,
    string? Restriction = null);

public sealed record OtpChallengeResponse(Guid ChallengeId, DateTimeOffset ExpiresAt, string MaskedPhone);

public sealed record SessionInfo(Guid Id, string ClientType, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt, string? IpAddress, string? UserAgent, string? DeviceName, bool IsCurrent);

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
    ISessionValidator sessionValidator,
    IAuditLogger audit,
    INotificationService notifications,
    IPortalAccountResolver portalAccounts,
    ICurrentUser currentUser,
    IOptions<PlatformOptions> options,
    TimeProvider time)
{
    public const string RefreshCookieName = "hc360_rt";

    /// <summary>
    /// The patient portal's refresh cookie. The staff app and the portal are served from one origin, so each keeps its
    /// own cookie: signing in to one never replaces the other's session in the same browser.
    /// </summary>
    public const string PortalRefreshCookieName = "hc360_prt";
    public const string RefreshCookiePath = "/api/v1/auth";
    public const string ClientHeader = "X-HC360-Client";
    public const string RestrictionPasswordChange = "password";
    public const string RestrictionMfaSetup = "mfa-setup";

    private const int MaxChallengeAttempts = 5;
    private static readonly TimeSpan MfaChallengeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OtpLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshReuseGrace = TimeSpan.FromSeconds(60);
    private static string? _dummyHash;

    private static readonly Error InvalidCredentials = new("auth.invalid_credentials", "Invalid username or password.", ErrorType.Unauthorized);
    private static readonly Error SessionEnded = new("auth.session_ended", "Your session has ended. Please sign in again.", ErrorType.Unauthorized);
    private static readonly Error ChallengeExpired = new("auth.challenge_expired", "This sign-in attempt has expired. Please start again.", ErrorType.Unauthorized);

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
            var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalMinutes));
            await audit.WriteAsync(new AuditEntry(AuditCategories.Auth, "auth.login_locked", "Sign-in attempted while locked")
            {
                UserId = user.Id,
                UserName = user.UserName,
                Severity = AuditSeverity.Warning,
            }, ct);
            return Error.Rule("auth.locked", $"Too many failed attempts. Try again in {minutes} minute(s) or ask an administrator to unlock the account.");
        }

        var check = hashing.Verify(user.PasswordHash, request.Password ?? string.Empty);
        if (!check.Succeeded)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount % Security.MaxFailedLogins == 0)
            {
                var multiplier = Math.Pow(2, (user.FailedLoginCount / Security.MaxFailedLogins) - 1);
                user.LockoutEndsAt = now + TimeSpan.FromMinutes(Math.Min(Security.LockoutMinutes * multiplier, 24 * 60));
            }

            audit.Add(new AuditEntry(AuditCategories.Auth, "auth.login_failed", "Wrong password")
            {
                UserId = user.Id,
                UserName = user.UserName,
                Severity = user.LockoutEndsAt > now ? AuditSeverity.Critical : AuditSeverity.Warning,
            });
            await db.SaveChangesAsync(ct);
            return InvalidCredentials;
        }

        if (check.NeedsRehash)
        {
            user.PasswordHash = hashing.Hash(request.Password!);
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
        if (user is null || !user.IsActive)
        {
            await db.SaveChangesAsync(ct);
            return ChallengeExpired;
        }

        if (!VerifyTotp(user, request.Code, out var timeStep))
        {
            audit.Add(new AuditEntry(AuditCategories.Auth, "auth.mfa_failed", "Wrong authenticator code")
            {
                UserId = user.Id,
                UserName = user.UserName,
                Severity = AuditSeverity.Warning,
            });
            await db.SaveChangesAsync(ct);
            return Error.Field("code", "The code is incorrect or has expired.");
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

        var now = time.GetUtcNow();
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
        return new OtpChallengeResponse(challenge.Id, challenge.ExpiresAt, SharedKernel.Phone.Mask(phone));
    }

    public async Task<Result<AuthResult>> VerifyOtpAsync(OtpVerifyRequest request, HttpContext http, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var challenge = await db.Set<AuthChallenge>().FirstOrDefaultAsync(c => c.Id == request.ChallengeId && c.Kind == AuthChallengeKind.Otp, ct);
        if (challenge?.Phone is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= now || challenge.Attempts >= MaxChallengeAttempts)
        {
            return ChallengeExpired;
        }

        challenge.Attempts++;
        var expected = SecretTokens.Hash($"{challenge.Id:N}:{(request.Code ?? string.Empty).Trim()}");
        if (challenge.CodeHash is null || !SecretTokens.FixedTimeEquals(challenge.CodeHash, expected))
        {
            await db.SaveChangesAsync(ct);
            return Error.Field("code", "The code is incorrect.");
        }

        challenge.ConsumedAt = now;
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
            || now - session.LastSeenAt > TimeSpan.FromMinutes(Security.SessionIdleMinutes)
            || RefreshCookieFor(session.ClientType) != RefreshCookieFor(ClientTypeOf(http)))
        {
            ClearRefreshCookie(http);
            return SessionEnded;
        }

        if (token.UsedAt is { } usedAt)
        {
            if (now - usedAt > RefreshReuseGrace)
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

            // The previous response was probably lost on the WiFi link: retire the replacement the client never saw.
            if (token.ReplacedById is { } replacedId)
            {
                await db.Set<RefreshToken>().Where(t => t.Id == replacedId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, now), ct);
            }
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

        token.UsedAt = now;
        var next = CreateRefreshToken(session, now);
        token.ReplacedById = next.Entity.Id;
        session.LastSeenAt = now;
        await db.SaveChangesAsync(ct);

        SetRefreshCookie(http, session.ClientType, next.Raw, next.Entity.ExpiresAt);
        var roles = RoleNames(user);
        var restriction = RestrictionFor(user, roles);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, restriction, ct), Restriction: restriction);
    }

    public async Task<Result> LogoutAsync(HttpContext http, CancellationToken ct)
    {
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
        return await BuildAuthUserAsync(user, roles, currentUser.FacilityId, RestrictionFor(user, roles), ct);
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
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, restriction, ct), Restriction: restriction);
    }

    public async Task<Result<AuthResult>> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user?.PasswordHash is null || session is null)
        {
            return SessionEnded;
        }

        if (!hashing.Verify(user.PasswordHash, request.CurrentPassword ?? string.Empty).Succeeded)
        {
            return Error.Field("currentPassword", "Current password is incorrect.");
        }

        var errors = passwordPolicy.Validate(request.NewPassword, user.UserName, user.DisplayName);
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

        // Sign out every other session of this user.
        var others = await db.Set<UserSession>().Where(s => s.UserId == user.Id && s.Id != session.Id && s.RevokedAt == null).ToListAsync(ct);
        foreach (var other in others)
        {
            other.RevokedAt = now;
            other.RevokedReason = "password_changed";
            sessionValidator.Invalidate(other.Id);
        }

        audit.Add(new AuditEntry(AuditCategories.Security, "auth.password_changed", "Password changed") { UserId = user.Id });
        await db.SaveChangesAsync(ct);

        var roles = RoleNames(user);
        var restriction = RestrictionFor(user, roles);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, restriction, ct), Restriction: restriction);
    }

    // ---------------------------------------------------------------- idle lock screen

    public async Task<Result> SetPinAsync(SetPinRequest request, CancellationToken ct)
    {
        var (user, _) = await CurrentUserAndSessionAsync(ct);
        if (user?.PasswordHash is null)
        {
            return SessionEnded;
        }

        if (!hashing.Verify(user.PasswordHash, request.Password ?? string.Empty).Succeeded)
        {
            return Error.Field("password", "Password is incorrect.");
        }

        var pin = request.Pin ?? string.Empty;
        if (pin.Length is < 4 or > 6 || !pin.All(char.IsAsciiDigit) || pin.Distinct().Count() == 1 || "0123456789".Contains(pin, StringComparison.Ordinal)
            || "9876543210".Contains(pin, StringComparison.Ordinal))
        {
            return Error.Field("pin", "Use 4–6 digits that are not all the same or in sequence.");
        }

        user.PinHash = hashing.Hash(pin);
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.pin_set", "Quick-unlock PIN set") { UserId = user.Id });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> UnlockAsync(UnlockRequest request, HttpContext http, CancellationToken ct)
    {
        var (user, session) = await CurrentUserAndSessionAsync(ct);
        if (user is null || session is null)
        {
            return SessionEnded;
        }

        var ok = request.Pin is { Length: > 0 } pin && user.PinHash is not null
            ? hashing.Verify(user.PinHash, pin).Succeeded
            : request.Password is { Length: > 0 } password && user.PasswordHash is not null && hashing.Verify(user.PasswordHash, password).Succeeded;

        if (ok)
        {
            session.FailedUnlockAttempts = 0;
            await db.SaveChangesAsync(ct);
            return Result.Success();
        }

        session.FailedUnlockAttempts++;
        if (session.FailedUnlockAttempts >= Security.MaxPinAttempts)
        {
            session.RevokedAt = time.GetUtcNow();
            session.RevokedReason = "unlock_attempts_exceeded";
            audit.Add(new AuditEntry(AuditCategories.Security, "auth.unlock_locked_out", "Too many unlock attempts; session ended")
            {
                UserId = user.Id,
                Severity = AuditSeverity.Warning,
            });
            await db.SaveChangesAsync(ct);
            sessionValidator.Invalidate(session.Id);
            ClearRefreshCookie(http, session.ClientType);
            return SessionEnded;
        }

        await db.SaveChangesAsync(ct);
        return Error.Field(request.Pin is { Length: > 0 } ? "pin" : "password", "That did not match. Try again.");
    }

    // ---------------------------------------------------------------- MFA enrolment

    public async Task<Result<MfaSetupResponse>> BeginMfaSetupAsync(CancellationToken ct)
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

        user.MfaEnabled = true;
        user.MfaLastTimeStep = timeStep;
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.mfa_enabled", "Two-step verification enabled") { UserId = user.Id });
        await db.SaveChangesAsync(ct);

        var roles = RoleNames(user);
        var restriction = RestrictionFor(user, roles);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, session.FacilityId, restriction, ct), Restriction: restriction);
    }

    public async Task<Result> DisableMfaAsync(MfaDisableRequest request, CancellationToken ct)
    {
        var (user, _) = await CurrentUserAndSessionAsync(ct);
        if (user?.PasswordHash is null)
        {
            return SessionEnded;
        }

        if (RequiresMfa(RoleNames(user)))
        {
            return Error.Forbidden("auth.mfa_required", "Your role requires two-step verification.");
        }

        if (!hashing.Verify(user.PasswordHash, request.Password ?? string.Empty).Succeeded)
        {
            return Error.Field("password", "Password is incorrect.");
        }

        user.MfaEnabled = false;
        user.MfaSecret = null;
        user.MfaLastTimeStep = null;
        audit.Add(new AuditEntry(AuditCategories.Security, "auth.mfa_disabled", "Two-step verification disabled") { UserId = user.Id, Severity = AuditSeverity.Warning });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ---------------------------------------------------------------- sessions

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return [];
        }

        var now = time.GetUtcNow();
        var sessions = await db.Set<UserSession>()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(ct);
        var deviceIds = sessions.Where(s => s.DeviceId != null).Select(s => s.DeviceId!.Value).Distinct().ToList();
        var devices = await db.Set<Device>().Where(d => deviceIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return sessions.Select(s => new SessionInfo(
            s.Id, s.ClientType, s.CreatedAt, s.LastSeenAt, s.ExpiresAt, s.IpAddress, s.UserAgent,
            s.DeviceId is { } d && devices.TryGetValue(d, out var name) ? name : null,
            s.Id == currentUser.SessionId)).ToList();
    }

    public async Task<Result> RevokeSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Set<UserSession>().FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == currentUser.UserId, ct);
        if (session is null)
        {
            return Error.NotFound("Session", sessionId);
        }

        session.RevokedAt ??= time.GetUtcNow();
        session.RevokedReason ??= "revoked_by_user";
        await db.SaveChangesAsync(ct);
        sessionValidator.Invalidate(session.Id);
        return Result.Success();
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
            ExpiresAt = now.AddHours(Security.SessionHours),
            IpAddress = http.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(http.Request.Headers.UserAgent.ToString(), 512),
        };
        db.Add(session);
        var refresh = CreateRefreshToken(session, now);

        user.FailedLoginCount = 0;
        user.LockoutEndsAt = null;
        user.LastLoginAt = now;
        audit.Add(new AuditEntry(AuditCategories.Auth, "auth.login", $"Signed in ({clientType})") { UserId = user.Id, UserName = user.UserName });
        await db.SaveChangesAsync(ct);

        SetRefreshCookie(http, clientType, refresh.Raw, refresh.Entity.ExpiresAt);
        var access = tokens.CreateAccessToken(Subject(user, roles, session, restriction));
        return new AuthResult(access.Token, access.ExpiresAt, await BuildAuthUserAsync(user, roles, facility?.Id, restriction, ct), Restriction: restriction);
    }

    private (string Raw, RefreshToken Entity) CreateRefreshToken(UserSession session, DateTimeOffset now)
    {
        var raw = SecretTokens.NewOpaqueToken();
        var idleExpiry = now.AddMinutes(Security.SessionIdleMinutes);
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

    private async Task<AuthUser> BuildAuthUserAsync(User user, IReadOnlyList<string> roles, Guid? facilityId, string? restriction, CancellationToken ct)
    {
        IReadOnlyCollection<string> granted = restriction is null
            ? (await permissions.GetPermissionsAsync(user.Id, ct)).ToArray()
            : [];
        var facilities = user.Type == UserType.Patient ? [] : await AllowedFacilitiesAsync(user, roles, ct);

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
            restriction);
    }

    private async Task<List<FacilityOption>> AllowedFacilitiesAsync(User user, IReadOnlyList<string> roles, CancellationToken ct)
    {
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

        var device = await db.Set<Device>().FirstOrDefaultAsync(d => d.Id == id && !d.IsRevoked, ct);
        return device is not null && SecretTokens.FixedTimeEquals(device.SecretHash, SecretTokens.Hash(secret)) ? device.Id : null;
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
        : RequiresMfa(roles) && !user.MfaEnabled ? RestrictionMfaSetup
        : null;

    private bool RequiresMfa(IReadOnlyList<string> roles) =>
        Security.EnforceMfa && roles.Any(r => Security.MfaRequiredRoles.Contains(r, StringComparer.OrdinalIgnoreCase));

    private async Task<(User? User, UserSession? Session)> CurrentUserAndSessionAsync(CancellationToken ct)
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
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .Include(u => u.Facilities)
            .AsSplitQuery()
            .FirstOrDefaultAsync(predicate, ct);

    private static List<string> RoleNames(User user) => user.Roles.Where(r => r.Role is not null).Select(r => r.Role!.Name).ToList();

    private static string ClientTypeOf(HttpContext http) =>
        http.Request.Headers[ClientHeader].ToString() switch
        {
            "desktop" => "desktop",
            "portal" => "portal",
            _ => "web",
        };

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
