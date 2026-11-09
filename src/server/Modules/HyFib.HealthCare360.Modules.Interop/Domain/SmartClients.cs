namespace HyFib.HealthCare360.Modules.Interop.Domain;

/// <summary>How a SMART client gets its tokens.</summary>
public enum SmartClientKind
{
    /// <summary>
    /// An app a person launches: OAuth 2.0 authorization code with PKCE (S256), at a redirect URI registered for it. A
    /// staff member who may read FHIR signs in and authorizes it; its token reads as that staff member.
    /// </summary>
    App,

    /// <summary>
    /// A backend service (SMART Backend Services): client credentials, the client authenticating with a JWT signed by a key
    /// registered for it. Its token reads as the integration account it was registered for, at that account's facility.
    /// </summary>
    BackendService,
}

public enum SmartClientStatus
{
    /// <summary>Registered by one administrator; no token is issued until another approves it.</summary>
    PendingApproval,

    Active,

    /// <summary>Stopped by an administrator: its tokens were revoked and no more are issued.</summary>
    Suspended,

    /// <summary>The approver turned the registration down.</summary>
    Refused,
}

/// <summary>
/// A partner application registered to read the FHIR API with SMART on FHIR (OAuth 2.0) tokens. An administrator
/// registers it; it is issued tokens only once a second administrator has approved it (the approvals framework). Its
/// tokens never read more than the account they act as could: the account's permissions, its facility and every
/// patient's record-sharing consent still apply to every read.
/// </summary>
internal sealed class SmartClient : AuditableEntity
{
    /// <summary>The public client identifier (<c>client_id</c>), generated at registration.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public SmartClientKind Kind { get; set; }

    public SmartClientStatus Status { get; set; }

    /// <summary>The exact redirect URIs an app may be sent back to (https, or http on the loopback address for a native app).</summary>
    public string RedirectUrisJson { get; set; } = "[]";

    /// <summary>
    /// The client's public keys as a JSON Web Key Set (RSA or EC). Required for a backend service, which signs its
    /// client assertions with the private half; an app may register one to authenticate at the token endpoint too.
    /// </summary>
    public string? JwksJson { get; set; }

    /// <summary>The scopes the client may be granted, as formatted by <see cref="SmartScopes.Format"/>.</summary>
    public string Scopes { get; set; } = string.Empty;

    /// <summary>A backend service's integration account: the account its tokens read as (a role with <c>fhir.read</c>).</summary>
    public Guid? ServiceUserId { get; set; }

    /// <summary>A backend service's facility: one its account is assigned to.</summary>
    public Guid? FacilityId { get; set; }

    /// <summary>
    /// Migrates the integration account off password sign-in: once the client is active, the account's own session
    /// tokens are refused by the FHIR API (403 <c>smart-required</c>), so only this client's tokens read.
    /// </summary>
    public bool ReplacesPasswordSignIn { get; set; }

    public Guid RegisteredBy { get; set; }

    public DateTimeOffset RegisteredAt { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset? ApprovedAt { get; set; }

    public Guid? SuspendedBy { get; set; }

    public DateTimeOffset? SuspendedAt { get; set; }

    public string? SuspendedReason { get; set; }
}

/// <summary>An authorization code issued to an app: single use, ten minutes, bound to its PKCE challenge and redirect URI.</summary>
[NotAudited]
internal sealed class SmartAuthorizationCode : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public Guid SmartClientId { get; set; }

    /// <summary>SHA-256 of the code; the code itself is never stored.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>The PKCE challenge (S256, base64url).</summary>
    public string CodeChallenge { get; set; } = string.Empty;

    /// <summary>The staff member who authorized the app, and the facility of their session.</summary>
    public Guid UserId { get; set; }

    public Guid FacilityId { get; set; }

    public Guid? PatientId { get; set; }

    public string Scopes { get; set; } = string.Empty;

    public bool LaunchPatient { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>The token the code was exchanged for: revoked if the code is presented again (RFC 6749 §4.1.2).</summary>
    public Guid? TokenId { get; set; }
}

/// <summary>An opaque access token issued to a SMART client. Only its SHA-256 is stored.</summary>
[NotAudited]
internal sealed class SmartAccessToken : Entity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>The prefix that tells a SMART token from a staff session token (a JWT) at the FHIR API.</summary>
    public const string Prefix = "hc360smart.";

    public Guid SmartClientId { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public string GrantType { get; set; } = string.Empty;

    /// <summary>The account the token reads as: the staff member who authorized an app, or a backend service's account.</summary>
    public Guid UserId { get; set; }

    public Guid FacilityId { get; set; }

    public Guid? PatientId { get; set; }

    public string Scopes { get; set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}

/// <summary>
/// A client assertion's <c>jti</c>, kept until the assertion expires so the same assertion is never accepted twice
/// (SMART Backend Services).
/// </summary>
[NotAudited]
internal sealed class SmartAssertionUse : Entity
{
    public Guid SmartClientId { get; set; }

    public string Jti { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
}
