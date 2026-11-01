using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Platform.Identity;

public enum UserType
{
    Staff = 0,
    Patient = 1,
    Service = 2,
}

/// <summary>A person who signs in: hospital staff (password + optional MFA) or a patient (mobile OTP).</summary>
public sealed class User : AuditableEntity
{
    public string UserName { get; set; } = string.Empty;
    public string NormalizedUserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public UserType Type { get; set; }
    public string? Email { get; set; }

    /// <summary>E.164 mobile number (used for OTP and alerts).</summary>
    public string? Phone { get; set; }

    [SensitiveData]
    public string? PasswordHash { get; set; }

    public DateTimeOffset? PasswordChangedAt { get; set; }
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// When the temporary password an administrator gave out stops working, unless it has been replaced by then. Null for
    /// any other password, including the initial administrator's one-time password.
    /// </summary>
    public DateTimeOffset? TemporaryPasswordExpiresAt { get; set; }

    /// <summary>
    /// When a sign-in found the password published from a data breach or on the common-password list (SRS SEC-01): it
    /// must be changed before anything else. Cleared when it is changed or reset.
    /// </summary>
    public DateTimeOffset? PasswordBreachedAt { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Wrong passwords and authenticator codes since the last success, or since failures were last forgotten.</summary>
    public int FailedLoginCount { get; set; }

    /// <summary>When the last of <see cref="FailedLoginCount"/> happened.</summary>
    public DateTimeOffset? LastFailedLoginAt { get; set; }
    public DateTimeOffset? LockoutEndsAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public bool MfaEnabled { get; set; }

    /// <summary>TOTP secret, encrypted with <c>IFieldProtector</c>.</summary>
    [SensitiveData]
    public string? MfaSecret { get; set; }

    /// <summary>Last accepted TOTP time-step (prevents code replay).</summary>
    public long? MfaLastTimeStep { get; set; }

    /// <summary>Quick-unlock PIN for the idle lock screen (hashed).</summary>
    [SensitiveData]
    public string? PinHash { get; set; }

    public Guid? DefaultFacilityId { get; set; }
    public Guid? PractitionerId { get; set; }
    public Guid? PatientId { get; set; }

    public List<UserRole> Roles { get; set; } = [];
    public List<UserFacility> Facilities { get; set; } = [];

    public static string Normalize(string userName) => userName.Trim().ToLowerInvariant();
}

public sealed class Role : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>Built-in roles cannot be deleted or renamed.</summary>
    public bool IsSystem { get; set; }

    public List<RolePermission> Permissions { get; set; } = [];
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public string Permission { get; set; } = string.Empty;
}

/// <summary>
/// A role granted to a user. Each grant and withdrawal is in the user's data audit event (<c>roles</c>, from/to), in the
/// transaction that makes it, whichever way it is made: administration, a portal account's first sign-in, the seeder.
/// </summary>
[AuditedWithParent]
public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

/// <summary>A facility a user may work in, audited with the user as their roles are (<c>facilities</c>).</summary>
[AuditedWithParent]
public sealed class UserFacility
{
    public Guid UserId { get; set; }
    public Guid FacilityId { get; set; }
}

/// <summary>Records which default role grants were already applied, so admin revocations are respected on upgrade.</summary>
public sealed class SeededGrant
{
    public string RoleName { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
    public DateTimeOffset SeededAt { get; set; }
}

/// <summary>A sign-in session (refresh-token family). One per device/browser sign-in.</summary>
public sealed class UserSession : Entity
{
    public Guid UserId { get; set; }
    public Guid? FacilityId { get; set; }
    public Guid? DeviceId { get; set; }
    public string ClientType { get; set; } = "web";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The last token refresh, which the app does in the background whether or not anyone is using it.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// The last time someone used the app (a key press, click or touch, as the app reports at each refresh), or signed in
    /// or unlocked. The idle lock and the idle end of the session count from it, not from <see cref="LastSeenAt"/>.
    /// </summary>
    public DateTimeOffset LastActiveAt { get; set; }

    /// <summary>
    /// When the screen was locked, by the app or by the server after a quiet spell; null while unlocked. A locked session
    /// can do nothing but unlock or sign out until its PIN or password is entered.
    /// </summary>
    public DateTimeOffset? LockedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedReason { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public int FailedUnlockAttempts { get; set; }
}

/// <summary>Opaque rotating refresh token (stored hashed).</summary>
public sealed class RefreshToken : Entity
{
    public Guid SessionId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? ReplacedById { get; set; }

    /// <summary>
    /// Set when a retry within the grace re-issued the token this one replaced: the client was not expected to have
    /// received it, so it being presented means someone holds a copy.
    /// </summary>
    public DateTimeOffset? RetiredAt { get; set; }
}

public enum AuthChallengeKind
{
    Mfa = 0,
    Otp = 1,

    /// <summary>Setting up a device's Windows Hello, fingerprint or face to unlock the lock screen (WebAuthn registration).</summary>
    WebAuthnRegistration = 2,

    /// <summary>Unlocking the lock screen with a device set up for it (WebAuthn assertion).</summary>
    WebAuthnUnlock = 3,
}

/// <summary>
/// Pending second step of a sign-in (TOTP after password, or SMS OTP for the patient portal), or a WebAuthn ceremony of
/// a signed-in session (<see cref="WebAuthnCredential"/>).
/// </summary>
public sealed class AuthChallenge : Entity
{
    public AuthChallengeKind Kind { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>The session a WebAuthn ceremony was started by: no other session may finish it.</summary>
    public Guid? SessionId { get; set; }

    /// <summary>A WebAuthn ceremony's options as the browser was given them, its random challenge among them.</summary>
    public string? Options { get; set; }

    public string? Phone { get; set; }
    public string? CodeHash { get; set; }
    public Guid? FacilityId { get; set; }
    public Guid? DeviceId { get; set; }
    public string ClientType { get; set; } = "web";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
}

/// <summary>
/// A device's built-in authenticator (Windows Hello, a fingerprint reader or face camera) that a staff member set up to
/// unlock their lock screen (SRS UX-11, <see cref="WebAuthnUnlockService"/>). It holds only the public half of a key
/// pair whose private half never leaves the device, which uses it only after checking the person's face, finger or
/// Windows Hello PIN. It resumes a locked session of its owner and never signs anyone in.
/// </summary>
public sealed class WebAuthnCredential : Entity
{
    public Guid UserId { get; set; }

    /// <summary>The credential's id, as the device made it.</summary>
    public byte[] CredentialId { get; set; } = [];

    /// <summary>The public key (COSE), which checks the device's signatures.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>
    /// The signature counter last seen. A device that counts never goes back: a count at or below this one means a copy
    /// of the key, and is refused.
    /// </summary>
    public long SignCount { get; set; }

    /// <summary>
    /// The server name it was set up under (the WebAuthn relying party id, such as <c>hc360-srv</c>). The device uses
    /// it only for pages opened by that name.
    /// </summary>
    public string RelyingPartyId { get; set; } = string.Empty;

    /// <summary>What its owner called it, such as "OPD Reception 2".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The make of authenticator, as the device states it (all zeros when it does not say).</summary>
    public Guid AaGuid { get; set; }

    /// <summary>How the browser reaches it (<c>internal</c> for one built into the device), comma-separated.</summary>
    public string? Transports { get; set; }

    /// <summary>The key may be copied to the owner's other devices by the platform (a synced passkey).</summary>
    public bool IsBackupEligible { get; set; }

    /// <summary>The HC360 Desktop it was set up on, when it was.</summary>
    public Guid? DeviceId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

public enum DeviceKind
{
    Workstation = 0,
    Kiosk,
    DisplayBoard,
    Tablet,

    /// <summary>The HC360 lab agent: a Windows service on the PC a serial analyser is cabled to (INT-LAB).</summary>
    LabAgent,
}

public static class DeviceKinds
{
    /// <summary>
    /// The kinds that are HC360 Desktop installations, where staff sign in and that send the desktop heartbeat. A lab
    /// agent's credential sits on an unattended PC, so it never counts as an enrolled workstation.
    /// </summary>
    public static bool IsDesktop(this DeviceKind kind) =>
        kind is DeviceKind.Workstation or DeviceKind.Kiosk or DeviceKind.DisplayBoard or DeviceKind.Tablet;
}

/// <summary>An enrolled HC360 Desktop installation (workstation, kiosk or queue display) or HC360 lab agent.</summary>
public sealed class Device : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? MachineName { get; set; }
    public DeviceKind Kind { get; set; }
    public string? OsVersion { get; set; }
    public string? AppVersion { get; set; }

    [SensitiveData]
    public string SecretHash { get; set; } = string.Empty;

    public DateTimeOffset EnrolledAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public string? LastIpAddress { get; set; }
    public string? LastWifiSsid { get; set; }
    public int? LastWifiSignal { get; set; }
    public bool IsRevoked { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? FacilityId { get; set; }
    public Guid? DepartmentId { get; set; }
    public string? Location { get; set; }
}

public sealed class DeviceEnrollmentCode : Entity
{
    public string CodeHash { get; set; } = string.Empty;
    public DeviceKind Kind { get; set; }
    public Guid? FacilityId { get; set; }
    public string? Note { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public Guid? UsedByDeviceId { get; set; }
}
