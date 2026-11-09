using System.Security.Claims;
using System.Text.Encodings.Web;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Platform.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>The claims a SMART access token adds to the account it acts as.</summary>
internal static class SmartClaims
{
    public const string Client = "smart_client";
    public const string Scope = "smart_scope";
    public const string Patient = "smart_patient";

    /// <summary>What the token lets its bearer read, or null for a staff session (or a password-signed-in partner).</summary>
    public static SmartAccess? Of(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(Client), out var client)
            ? new SmartAccess(client, SmartScopes.ParseStored(user.FindFirstValue(Scope) ?? string.Empty),
                Guid.TryParse(user.FindFirstValue(Patient), out var patient) ? patient : null)
            : null;
}

/// <summary>
/// The <c>Smart</c> authentication scheme: an opaque SMART access token (<see cref="SmartAccessToken.Prefix"/>) in
/// <c>Authorization: Bearer</c>, on the FHIR API only. The token signs the request in as the account it was issued for
/// (the staff member who authorized the app, or the backend service's integration account) at its facility, so the
/// account's permissions, its facility and every patient's consent apply as they do to a session, and the token's
/// scopes and patient narrow what it reads further. Staff session tokens (JWTs) go on to the platform's scheme as before.
/// </summary>
internal sealed class SmartAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Smart";

    /// <summary>True for a request whose bearer token is a SMART one (the JWT scheme forwards those here).</summary>
    public static bool CarriesSmartToken(HttpContext http) => BearerOf(http)?.StartsWith(SmartAccessToken.Prefix, StringComparison.Ordinal) == true;

    private static string? BearerOf(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!CarriesSmartToken(Context))
        {
            return AuthenticateResult.NoResult();
        }

        // A SMART token reads the FHIR API, and nothing else of the server.
        if (!FhirEndpoints.IsFhirRequest(Request.Path))
        {
            return AuthenticateResult.Fail("SMART access tokens are accepted by the FHIR API only.");
        }

        var smart = Context.RequestServices.GetRequiredService<SmartAuthorization>();
        if (await smart.FindActiveAsync(BearerOf(Context), Context.RequestAborted) is not { } info)
        {
            return AuthenticateResult.Fail("The access token is not active.");
        }

        var db = Context.RequestServices.GetRequiredService<HospitalDbContext>();
        var account = await db.Set<User>().AsNoTracking().Where(u => u.Id == info.Token.UserId && u.IsActive)
            .Select(u => new { u.UserName, u.DisplayName }).FirstOrDefaultAsync(Context.RequestAborted);
        if (account is null)
        {
            return AuthenticateResult.Fail("The account the token reads as is no longer active.");
        }

        List<Claim> claims =
        [
            new(HcClaims.UserId, info.Token.UserId.ToString()),
            new(HcClaims.UserName, account.UserName),
            new(HcClaims.DisplayName, account.DisplayName ?? account.UserName),
            new(HcClaims.Facility, info.Token.FacilityId.ToString()),
            new(HcClaims.UserType, HcClaims.StaffType),
            new(SmartClaims.Client, info.Client.Id.ToString()),
            new(SmartClaims.Scope, info.Token.Scopes),
        ];
        if (info.Token.PatientId is { } patient)
        {
            claims.Add(new Claim(SmartClaims.Patient, patient.ToString()));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, HcClaims.UserName, HcClaims.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers the scheme and has the platform's JWT scheme forward to it the requests that carry a SMART token, so the
    /// endpoints' permission policies work with either.
    /// </summary>
    public static void Register(IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, SmartAuthenticationHandler>(SchemeName, null);
        services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme,
            jwt => jwt.ForwardDefaultSelector = http => CarriesSmartToken(http) ? SchemeName : null);
    }
}
