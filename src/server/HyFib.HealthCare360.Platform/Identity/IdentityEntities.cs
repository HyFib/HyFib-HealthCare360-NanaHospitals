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
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
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

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

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
    public DateTimeOffset LastSeenAt { get; set; }
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
}

/// <summary>Pending second step of a sign-in (TOTP after password, or SMS OTP for the patient portal).</summary>
public sealed class AuthChallenge : Entity
{
    public AuthChallengeKind Kind { get; set; }
    public Guid? UserId { get; set; }
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
