using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Platform.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HyFib.HealthCare360.Modules.Interop.Services;

/// <summary>An OAuth 2.0 error (RFC 6749 §5.2): the code, a sentence for the developer, and the HTTP status.</summary>
internal sealed record OAuthError(string Code, string Description, int Status = StatusCodes.Status400BadRequest)
{
    public static OAuthError InvalidRequest(string description) => new("invalid_request", description);

    public static OAuthError InvalidClient(string description) => new("invalid_client", description, StatusCodes.Status401Unauthorized);

    public static OAuthError InvalidGrant(string description) => new("invalid_grant", description);

    public static OAuthError InvalidScope(string description) => new("invalid_scope", description);

    public static OAuthError UnauthorizedClient(string description) => new("unauthorized_client", description);
}

/// <summary>A token issued: the value (shown to the client once), its lifetime, the scopes granted and the patient in context.</summary>
internal sealed record SmartTokenIssued(string AccessToken, int ExpiresIn, string Scope, Guid? PatientId);

/// <summary>What a valid access token carries, for the FHIR API and for introspection.</summary>
internal sealed record SmartTokenInfo(SmartAccessToken Token, SmartClient Client);

/// <summary>
/// SMART on FHIR (App Launch 2.0 and Backend Services) for partner applications: authorization codes with PKCE issued to
/// an app a staff member authorizes, client credentials for a backend service authenticating with a JWT signed by its
/// registered key, opaque access tokens (only their SHA-256 is stored) that are introspected and revoked, and the client
/// assertions' ids kept so none is accepted twice. A token never reads more than the account it acts as could.
/// </summary>
internal sealed class SmartAuthorization(HospitalDbContext db, IPermissionService permissions, TimeProvider time)
{
    public const string ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>A client assertion may live at most this long (SMART Backend Services: five minutes).</summary>
    public static readonly TimeSpan MaxAssertionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>The algorithms a client may sign its assertion with (SMART asks servers to support RS384 and ES384).</summary>
    public static readonly string[] AssertionAlgorithms =
        [SecurityAlgorithms.RsaSha384, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256];

    public static string Hash(string value) => SecretTokens.Hash(value);

    /// <summary>The PKCE S256 challenge of a verifier (RFC 7636 §4.2).</summary>
    public static string Challenge(string verifier) => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public async Task<SmartClient?> ActiveClientAsync(string? clientId, CancellationToken ct) =>
        string.IsNullOrEmpty(clientId) ? null
            : await db.Set<SmartClient>().FirstOrDefaultAsync(c => c.ClientId == clientId && c.Status == SmartClientStatus.Active, ct);

    /// <summary>
    /// Checks a client assertion (RFC 7523, SMART Backend Services): signed by one of the client's registered keys with an
    /// allowed algorithm, <c>iss</c> and <c>sub</c> the client id, <c>aud</c> this token endpoint, expiring within five
    /// minutes, and a <c>jti</c> never seen before, which is then kept until the assertion expires.
    /// </summary>
    public async Task<(SmartClient? Client, OAuthError? Error)> AuthenticateAssertionAsync(string? assertionType, string? assertion, string? clientId, string tokenEndpoint, CancellationToken ct)
    {
        if (assertionType != ClientAssertionType || string.IsNullOrEmpty(assertion) || assertion.Length > 8000)
        {
            return (null, OAuthError.InvalidClient("Authenticate with a client assertion: client_assertion_type " + ClientAssertionType + " and a signed client_assertion."));
        }

        var handler = new JsonWebTokenHandler();
        if (!handler.CanReadToken(assertion))
        {
            return (null, OAuthError.InvalidClient("The client assertion is not a JWT."));
        }

        var unverified = handler.ReadJsonWebToken(assertion);
        var issuer = unverified.Issuer;
        if (clientId is not null && clientId != issuer)
        {
            return (null, OAuthError.InvalidClient("client_id and the assertion's issuer differ."));
        }

        var client = await ActiveClientAsync(issuer, ct);
        if (client?.JwksJson is not { } jwks)
        {
            return (null, OAuthError.InvalidClient("No active client with registered keys has this id."));
        }

        var now = time.GetUtcNow();
        var result = await handler.ValidateTokenAsync(assertion, new TokenValidationParameters
        {
            ValidIssuer = client.ClientId,
            ValidAudience = tokenEndpoint,
            IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(),
            ValidAlgorithms = AssertionAlgorithms,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (_, expires, _, _) => expires is { } e && e > now.UtcDateTime.AddSeconds(-30) && e <= now.UtcDateTime.Add(MaxAssertionLifetime).AddSeconds(30),
        });
        if (!result.IsValid)
        {
            return (null, OAuthError.InvalidClient("The client assertion is not valid: its signature, audience, issuer or lifetime."));
        }

        var token = (JsonWebToken)result.SecurityToken;
        if (token.Subject != client.ClientId || string.IsNullOrEmpty(token.Id) || token.Id.Length > 200)
        {
            return (null, OAuthError.InvalidClient("The assertion's sub must be the client id, and it must carry a jti."));
        }

        // An assertion is good once: its jti is kept until it expires (the unique index catches two at the same moment).
        await db.Set<SmartAssertionUse>().Where(u => u.ExpiresAt < now).ExecuteDeleteAsync(ct);
        if (await db.Set<SmartAssertionUse>().AnyAsync(u => u.SmartClientId == client.Id && u.Jti == token.Id, ct))
        {
            return (null, OAuthError.InvalidClient("This client assertion was used before."));
        }

        db.Add(new SmartAssertionUse { SmartClientId = client.Id, Jti = token.Id, ExpiresAt = new DateTimeOffset(token.ValidTo, TimeSpan.Zero) });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return (null, OAuthError.InvalidClient("This client assertion was used before."));
        }

        return (client, null);
    }

    /// <summary>
    /// A backend service's token (client credentials): the scopes asked for, narrowed to its registered <c>system/</c>
    /// scopes, read as its integration account at its facility, if that account may still read FHIR there.
    /// </summary>
    public async Task<(SmartTokenIssued? Issued, OAuthError? Error)> ClientCredentialsAsync(SmartClient client, string? scope, CancellationToken ct)
    {
        if (client.Kind != SmartClientKind.BackendService || client.ServiceUserId is not { } userId || client.FacilityId is not { } facilityId)
        {
            return (null, OAuthError.UnauthorizedClient("Only a backend service may use client credentials."));
        }

        var grant = SmartScopes.Narrow(scope, [SmartContext.System], SmartScopes.ParseStored(client.Scopes));
        if (grant.Scopes.Count == 0)
        {
            return (null, OAuthError.InvalidScope("Ask for system/ scopes this client is registered for, such as system/*.rs (this API is read-only)."));
        }

        if (!await MayReadAsync(userId, facilityId, ct))
        {
            return (null, OAuthError.UnauthorizedClient("The client's integration account may no longer read FHIR at its facility."));
        }

        return (await IssueAsync(client, "client_credentials", userId, facilityId, null, grant.Scopes, false, ct), null);
    }

    /// <summary>The code an app gets once a staff member authorizes it (single use, ten minutes, bound to the PKCE challenge).</summary>
    public async Task<string> IssueCodeAsync(SmartClient client, Guid userId, Guid facilityId, Guid? patientId, SmartGrantRequest grant, string redirectUri, string challenge, CancellationToken ct)
    {
        var code = SecretTokens.NewOpaqueToken();
        var now = time.GetUtcNow();
        db.Add(new SmartAuthorizationCode
        {
            SmartClientId = client.Id,
            CodeHash = Hash(code),
            RedirectUri = redirectUri,
            CodeChallenge = challenge,
            UserId = userId,
            FacilityId = facilityId,
            PatientId = patientId,
            Scopes = SmartScopes.Format(grant.Scopes, false),
            LaunchPatient = grant.LaunchPatient,
            IssuedAt = now,
            ExpiresAt = now + SmartAuthorizationCode.Lifetime,
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>
    /// Exchanges an authorization code (RFC 6749 §4.1.3 with PKCE): the client and redirect URI it was issued for, the
    /// verifier of its challenge, unexpired and unused. A code presented twice revokes the token it was exchanged for.
    /// </summary>
    public async Task<(SmartTokenIssued? Issued, OAuthError? Error)> RedeemCodeAsync(SmartClient client, string? code, string? redirectUri, string? verifier, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(redirectUri) || verifier is not { Length: >= 43 and <= 128 })
        {
            return (null, OAuthError.InvalidRequest("Send code, redirect_uri and code_verifier (43 to 128 characters)."));
        }

        var hash = Hash(code);
        var issued = await db.Set<SmartAuthorizationCode>().FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        var now = time.GetUtcNow();
        if (issued is null || issued.SmartClientId != client.Id)
        {
            return (null, OAuthError.InvalidGrant("The code is not valid for this client."));
        }

        if (issued.RedeemedAt is not null)
        {
            // RFC 6749 §4.1.2: a code used twice may have been stolen, so what it was exchanged for stops working.
            await db.Set<SmartAccessToken>().Where(t => t.Id == issued.TokenId && t.RevokedAt == null)
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, now), ct);
            return (null, OAuthError.InvalidGrant("The code was used before."));
        }

        if (issued.ExpiresAt <= now || issued.RedirectUri != redirectUri
            || !SecretTokens.FixedTimeEquals(Challenge(verifier), issued.CodeChallenge))
        {
            return (null, OAuthError.InvalidGrant("The code has expired, or the redirect_uri or code_verifier does not match it."));
        }

        issued.RedeemedAt = now;
        if (!await MayReadAsync(issued.UserId, issued.FacilityId, ct))
        {
            await db.SaveChangesAsync(ct);
            return (null, OAuthError.InvalidGrant("The staff member who authorized the app may no longer read FHIR at this facility."));
        }

        var token = await IssueAsync(client, "authorization_code", issued.UserId, issued.FacilityId, issued.PatientId,
            SmartScopes.ParseStored(issued.Scopes), issued.LaunchPatient, ct, issued);
        return (token, null);
    }

    private async Task<SmartTokenIssued> IssueAsync(
        SmartClient client, string grantType, Guid userId, Guid facilityId, Guid? patientId, IReadOnlyList<SmartScope> scopes, bool launchPatient,
        CancellationToken ct, SmartAuthorizationCode? code = null)
    {
        var value = SmartAccessToken.Prefix + SecretTokens.NewOpaqueToken();
        var now = time.GetUtcNow();
        var token = new SmartAccessToken
        {
            SmartClientId = client.Id,
            TokenHash = Hash(value),
            GrantType = grantType,
            UserId = userId,
            FacilityId = facilityId,
            PatientId = patientId,
            Scopes = SmartScopes.Format(scopes, false),
            IssuedAt = now,
            ExpiresAt = now + SmartAccessToken.Lifetime,
        };
        db.Add(token);
        if (code is not null)
        {
            code.TokenId = token.Id;
        }

        await db.SaveChangesAsync(ct);
        return new SmartTokenIssued(value, (int)SmartAccessToken.Lifetime.TotalSeconds, SmartScopes.Format(scopes, launchPatient), patientId);
    }

    /// <summary>The token behind a bearer value, if it is active and its client still is.</summary>
    public async Task<SmartTokenInfo?> FindActiveAsync(string? value, CancellationToken ct)
    {
        if (value is null || !value.StartsWith(SmartAccessToken.Prefix, StringComparison.Ordinal) || value.Length > 200)
        {
            return null;
        }

        var hash = Hash(value);
        var now = time.GetUtcNow();
        var found = await (from t in db.Set<SmartAccessToken>()
                           join c in db.Set<SmartClient>() on t.SmartClientId equals c.Id
                           where t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now && c.Status == SmartClientStatus.Active
                           select new { t, c }).AsNoTracking().FirstOrDefaultAsync(ct);
        return found is null ? null : new SmartTokenInfo(found.t, found.c);
    }

    /// <summary>RFC 7009: revokes the token if the client it was issued to asks; says nothing either way.</summary>
    public async Task RevokeAsync(SmartClient client, string? value, CancellationToken ct)
    {
        if (value is null || value.Length > 200)
        {
            return;
        }

        var hash = Hash(value);
        await db.Set<SmartAccessToken>().Where(t => t.TokenHash == hash && t.SmartClientId == client.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, time.GetUtcNow()), ct);
    }

    /// <summary>Every token of the client stops working (its suspension).</summary>
    public async Task<int> RevokeAllAsync(Guid clientId, CancellationToken ct) =>
        await db.Set<SmartAccessToken>().Where(t => t.SmartClientId == clientId && t.RevokedAt == null)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RevokedAt, time.GetUtcNow()), ct);

    /// <summary>Whether the account is an active staff account assigned to the facility that holds <c>fhir.read</c>.</summary>
    public async Task<bool> MayReadAsync(Guid userId, Guid facilityId, CancellationToken ct) =>
        await db.Set<User>().AnyAsync(u => u.Id == userId && u.IsActive && u.Type == UserType.Staff, ct)
        && await db.Set<UserFacility>().AnyAsync(f => f.UserId == userId && f.FacilityId == facilityId, ct)
        && await permissions.HasPermissionAsync(userId, InteropPermissions.FhirRead, ct);

    /// <summary>
    /// Whether an account's own (password) session tokens are refused by the FHIR API: it was migrated to a SMART backend
    /// service that is active and replaces password sign-in.
    /// </summary>
    public async Task<bool> MigratedAsync(Guid userId, CancellationToken ct) =>
        await db.Set<SmartClient>().AnyAsync(c => c.ServiceUserId == userId && c.ReplacesPasswordSignIn && c.Status == SmartClientStatus.Active, ct);
}
