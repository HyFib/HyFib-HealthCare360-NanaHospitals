using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using Microsoft.AspNetCore.WebUtilities;

namespace HyFib.HealthCare360.Modules.Interop.Features;

/// <summary>What the staff app's authorization screen shows of a request: the app, what it asks for, and whether it needs a patient.</summary>
public sealed record SmartAuthorizationPreview(string ClientName, string ClientId, IReadOnlyList<string> Scopes, bool NeedsPatient);

/// <summary>A staff member's decision on an app's authorization request, as the staff app's screen sends it.</summary>
public sealed record SmartAuthorizeRequest(
    string ClientId, string RedirectUri, string? Scope, string? State, string CodeChallenge, string CodeChallengeMethod, string? PatientUhid, bool Approve);

/// <summary>Where the browser goes next: back to the app with a code, or with <c>error=access_denied</c>.</summary>
public sealed record SmartAuthorizeResult(string Redirect);

public sealed class SmartAuthorizeRequestValidator : AbstractValidator<SmartAuthorizeRequest>
{
    public SmartAuthorizeRequestValidator()
    {
        RuleFor(x => x.ClientId).NotEmpty().MaximumLength(64);
        RuleFor(x => x.RedirectUri).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Scope).MaximumLength(2000);
        RuleFor(x => x.State).MaximumLength(500);
        RuleFor(x => x.CodeChallenge).NotEmpty().Length(43, 128);
        RuleFor(x => x.CodeChallengeMethod).Equal("S256").WithMessage("Only PKCE with S256 is accepted.");
        RuleFor(x => x.PatientUhid).MaximumLength(20);
    }
}

/// <summary>
/// SMART on FHIR (App Launch 2.0, Backend Services) and OAuth 2.0 for partner applications:
/// <list type="bullet">
/// <item><c>GET /fhir/r4/.well-known/smart-configuration</c>: the discovery document (anonymous).</item>
/// <item><c>GET /oauth2/authorize</c>: an app's authorization request, checked and handed to the staff app's screen,
/// where a staff member who reads FHIR signs in and decides (<c>POST /api/v1/smart/authorize</c>).</item>
/// <item><c>POST /oauth2/token</c>: <c>authorization_code</c> (PKCE S256) or <c>client_credentials</c> with a JWT client
/// assertion; <c>POST /oauth2/introspect</c> (RFC 7662) and <c>POST /oauth2/revoke</c> (RFC 7009), for the client the
/// token was issued to.</item>
/// <item><c>/api/v1/smart/clients</c>: registration (a second administrator approves), the list and suspension.</item>
/// </list>
/// </summary>
internal static class SmartEndpoints
{
    public const string AuthorizePath = "/oauth2/authorize";
    public const string TokenPath = "/oauth2/token";
    public const string IntrospectPath = "/oauth2/introspect";
    public const string RevokePath = "/oauth2/revoke";

    /// <summary>The staff app's screen where a staff member authorizes an app.</summary>
    public const string StaffScreen = "/smart/authorize";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(FhirEndpoints.BasePath + "/.well-known/smart-configuration", Configuration)
            .AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public).WithTags("SMART on FHIR");

        var oauth = app.MapGroup("/oauth2").WithTags("SMART on FHIR").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Partner).DisableAntiforgery();
        oauth.MapGet("/authorize", Authorize);
        oauth.MapPost("/token", Token).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024));
        oauth.MapPost("/introspect", Introspect).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024));
        oauth.MapPost("/revoke", Revoke).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(16 * 1024));

        var smart = app.MapGroup("/api/v1/smart").WithTags("SMART on FHIR");
        smart.MapGet("/authorize/preview", Preview).RequirePermission(InteropPermissions.FhirRead);
        smart.MapPost("/authorize", Decide).RequirePermission(InteropPermissions.FhirRead).WithValidation<SmartAuthorizeRequest>();

        var clients = smart.MapGroup("/clients").RequirePermission(InteropPermissions.SmartClientsManage);
        clients.MapGet("/", async (SmartClientRegistry registry, CancellationToken ct) => TypedResults.Ok(await registry.ListAsync(ct)));
        clients.MapPost("/", async (RegisterSmartClientRequest request, SmartClientRegistry registry, CancellationToken ct) =>
                (await registry.RegisterAsync(request, ct)).ToHttpResult(registered => TypedResults.Accepted($"/api/v1/approvals/{registered.Approval.Id}", registered)))
            .WithValidation<RegisterSmartClientRequest>();
        clients.MapPost("/{id:guid}/suspend", async (Guid id, SuspendSmartClientRequest request, SmartClientRegistry registry, CancellationToken ct) =>
                (await registry.SuspendAsync(id, request, ct)).ToNoContent())
            .WithValidation<SuspendSmartClientRequest>();
    }

    public static string Origin(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    private static IResult Configuration(HttpContext http)
    {
        var origin = Origin(http.Request);
        return TypedResults.Json(new Dictionary<string, object>
        {
            ["issuer"] = origin + FhirEndpoints.BasePath,
            ["authorization_endpoint"] = origin + AuthorizePath,
            ["token_endpoint"] = origin + TokenPath,
            ["introspection_endpoint"] = origin + IntrospectPath,
            ["revocation_endpoint"] = origin + RevokePath,
            ["grant_types_supported"] = new[] { "authorization_code", "client_credentials" },
            ["token_endpoint_auth_methods_supported"] = new[] { "private_key_jwt", "none" },
            ["token_endpoint_auth_signing_alg_values_supported"] = SmartAuthorization.AssertionAlgorithms,
            ["scopes_supported"] = new[] { "patient/*.rs", "patient/*.read", "user/*.rs", "user/*.read", "system/*.rs", "system/*.read", SmartScopes.LaunchPatient },
            ["response_types_supported"] = new[] { "code" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["capabilities"] = new[]
            {
                "launch-standalone", "client-public", "client-confidential-asymmetric", "context-standalone-patient",
                "permission-patient", "permission-user", "permission-v1", "permission-v2",
            },
        });
    }

    /// <summary>
    /// An app's authorization request. Until the client and redirect URI are known good, problems are answered here (never
    /// by redirecting to an unchecked address); after, by redirecting back with the error. A good request goes to the
    /// staff app's screen with its parameters.
    /// </summary>
    private static async Task<IResult> Authorize(HttpContext http, SmartAuthorization smart, CancellationToken ct)
    {
        var q = http.Request.Query;
        var client = await smart.ActiveClientAsync(q["client_id"], ct);
        var redirect = q["redirect_uri"].ToString();
        if (client is null || client.Kind != SmartClientKind.App || !RedirectUris(client).Contains(redirect, StringComparer.Ordinal))
        {
            return TypedResults.Problem(title: "Unknown client or redirect URI",
                detail: "The client_id is not an active app registered here, or the redirect_uri is not one registered for it.", statusCode: StatusCodes.Status400BadRequest);
        }

        var state = q["state"].ToString();
        if (q["response_type"] != "code")
        {
            return TypedResults.Redirect(WithError(redirect, "unsupported_response_type", state));
        }

        if (q["code_challenge_method"] != "S256" || q["code_challenge"].ToString().Length is < 43 or > 128)
        {
            return TypedResults.Redirect(WithError(redirect, "invalid_request", state, "PKCE with code_challenge_method S256 is required."));
        }

        var grant = SmartScopes.Narrow(q["scope"], [SmartContext.Patient, SmartContext.User], SmartScopes.ParseStored(client.Scopes));
        if (grant.Scopes.Count == 0)
        {
            return TypedResults.Redirect(WithError(redirect, "invalid_scope", state, "Ask for patient/ or user/ read scopes this app is registered for."));
        }

        var aud = q["aud"].ToString();
        if (aud.Length > 0 && !string.Equals(aud.TrimEnd('/'), Origin(http.Request) + FhirEndpoints.BasePath, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Redirect(WithError(redirect, "invalid_request", state, "aud is not this FHIR server."));
        }

        return TypedResults.Redirect(StaffScreen + http.Request.QueryString.Value);
    }

    private static async Task<IResult> Preview(
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "client_id")] string? clientId, [Microsoft.AspNetCore.Mvc.FromQuery(Name = "redirect_uri")] string? redirectUri,
        string? scope, SmartAuthorization smart, CancellationToken ct)
    {
        var client = await smart.ActiveClientAsync(clientId, ct);
        if (client is null || client.Kind != SmartClientKind.App || !RedirectUris(client).Contains(redirectUri ?? string.Empty, StringComparer.Ordinal))
        {
            return Error.Rule("smart.unknown_client", "This app is not registered here, or it asked to return to an address not registered for it.").ToProblem();
        }

        var grant = SmartScopes.Narrow(scope, [SmartContext.Patient, SmartContext.User], SmartScopes.ParseStored(client.Scopes));
        return TypedResults.Ok(new SmartAuthorizationPreview(client.Name, client.ClientId, [.. grant.Scopes.Select(s => s.ToString())],
            grant.LaunchPatient || grant.Scopes.Any(s => s.Context == SmartContext.Patient)));
    }

    /// <summary>
    /// The signed-in staff member authorizes the app (a code, bound to its PKCE challenge, for them at their facility, and
    /// for the patient chosen when a patient/ scope was asked) or turns it down. Either way the browser goes back to the app.
    /// </summary>
    private static async Task<IResult> Decide(
        SmartAuthorizeRequest request, SmartAuthorization smart, HospitalDbContext db, ICurrentUser user, IAuditLogger audit, CancellationToken ct)
    {
        var client = await smart.ActiveClientAsync(request.ClientId, ct);
        if (client is null || client.Kind != SmartClientKind.App || !RedirectUris(client).Contains(request.RedirectUri, StringComparer.Ordinal))
        {
            return Error.Rule("smart.unknown_client", "This app is not registered here, or it asked to return to an address not registered for it.").ToProblem();
        }

        if (!request.Approve)
        {
            return TypedResults.Ok(new SmartAuthorizeResult(WithError(request.RedirectUri, "access_denied", request.State)));
        }

        if (user.UserId is not { } userId || user.FacilityId is not { } facilityId || !await smart.MayReadAsync(userId, facilityId, ct))
        {
            return Error.Rule("smart.not_reader", "You must be assigned to this facility, with a role that reads FHIR, to authorize an app.").ToProblem();
        }

        var grant = SmartScopes.Narrow(request.Scope, [SmartContext.Patient, SmartContext.User], SmartScopes.ParseStored(client.Scopes));
        if (grant.Scopes.Count == 0)
        {
            return TypedResults.Ok(new SmartAuthorizeResult(WithError(request.RedirectUri, "invalid_scope", request.State)));
        }

        Guid? patientId = null;
        if (grant.LaunchPatient || grant.Scopes.Any(s => s.Context == SmartContext.Patient))
        {
            var uhid = request.PatientUhid?.Trim().ToUpperInvariant();
            patientId = string.IsNullOrEmpty(uhid) ? null : await db.Set<Patient>().Where(p => p.Uhid == uhid).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
            if (patientId is null)
            {
                return Error.Field("patientUhid", "Enter the UHID of the patient whose record the app may read.").ToProblem();
            }
        }

        var code = await smart.IssueCodeAsync(client, userId, facilityId, patientId, grant, request.RedirectUri, request.CodeChallenge, ct);
        audit.Add(new AuditEntry(AuditCategories.Security, "smart.app_authorized", $"App {client.Name} authorized by {user.DisplayName ?? user.UserName}")
        {
            EntityType = nameof(SmartClient),
            EntityId = client.Id.ToString(),
            PatientId = patientId,
            Details = new { client.ClientId, scopes = SmartScopes.Format(grant.Scopes, grant.LaunchPatient) },
        });
        await db.SaveChangesAsync(ct);
        var query = new Dictionary<string, string?> { ["code"] = code, ["state"] = request.State };
        return TypedResults.Ok(new SmartAuthorizeResult(QueryHelpers.AddQueryString(request.RedirectUri, query.Where(p => p.Value is not null)!)));
    }

    private static async Task<IResult> Token(HttpContext http, SmartAuthorization smart, IAuditLogger audit, HospitalDbContext db, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
        {
            return OAuth(OAuthError.InvalidRequest("Send the parameters as application/x-www-form-urlencoded."));
        }

        var form = await http.Request.ReadFormAsync(ct);
        var tokenEndpoint = Origin(http.Request) + TokenPath;
        var grantType = form["grant_type"].ToString();
        SmartClient? client;
        OAuthError? error;
        if (form.ContainsKey("client_assertion"))
        {
            (client, error) = await smart.AuthenticateAssertionAsync(form["client_assertion_type"], form["client_assertion"], NullIfEmpty(form["client_id"]), tokenEndpoint, ct);
        }
        else
        {
            // A public app names itself; its code's PKCE verifier proves it is the one that asked.
            client = grantType == "authorization_code" ? await smart.ActiveClientAsync(form["client_id"], ct) : null;
            error = client is null || client.Kind != SmartClientKind.App || client.JwksJson is not null
                ? OAuthError.InvalidClient("Authenticate with a client assertion, or name a public app's client_id.")
                : null;
        }

        if (error is not null)
        {
            return OAuth(error);
        }

        var (issued, refused) = grantType switch
        {
            "client_credentials" => await smart.ClientCredentialsAsync(client!, form["scope"], ct),
            "authorization_code" when client!.Kind == SmartClientKind.App => await smart.RedeemCodeAsync(client, form["code"], form["redirect_uri"], form["code_verifier"], ct),
            "authorization_code" => (null, OAuthError.UnauthorizedClient("A backend service uses client credentials.")),
            _ => (null, new OAuthError("unsupported_grant_type", "Use authorization_code or client_credentials.")),
        };
        if (refused is not null)
        {
            audit.Add(new AuditEntry(AuditCategories.Security, "smart.token_refused", $"SMART token refused for {client!.Name}: {refused.Code}")
            {
                EntityType = nameof(SmartClient),
                EntityId = client.Id.ToString(),
                Severity = AuditSeverity.Warning,
            });
            await db.SaveChangesAsync(ct);
            return OAuth(refused);
        }

        audit.Add(new AuditEntry(AuditCategories.Security, "smart.token_issued", $"SMART access token issued to {client!.Name} ({grantType})")
        {
            EntityType = nameof(SmartClient),
            EntityId = client.Id.ToString(),
            PatientId = issued!.PatientId,
            Details = new { grantType, scope = issued.Scope },
        });
        await db.SaveChangesAsync(ct);
        var body = new Dictionary<string, object>
        {
            ["access_token"] = issued.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = issued.ExpiresIn,
            ["scope"] = issued.Scope,
        };
        if (issued.PatientId is { } patient)
        {
            body["patient"] = patient.ToString("D");
        }

        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        return TypedResults.Json(body);
    }

    /// <summary>RFC 7662, for the client the token was issued to (it authenticates with its assertion); any other token is inactive to it.</summary>
    private static async Task<IResult> Introspect(HttpContext http, SmartAuthorization smart, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
        {
            return OAuth(OAuthError.InvalidRequest("Send the parameters as application/x-www-form-urlencoded."));
        }

        var form = await http.Request.ReadFormAsync(ct);
        var (client, error) = await smart.AuthenticateAssertionAsync(form["client_assertion_type"], form["client_assertion"], NullIfEmpty(form["client_id"]),
            Origin(http.Request) + TokenPath, ct);
        if (error is not null)
        {
            return OAuth(error);
        }

        var info = await smart.FindActiveAsync(form["token"], ct);
        if (info is null || info.Client.Id != client!.Id)
        {
            return TypedResults.Json(new { active = false });
        }

        var body = new Dictionary<string, object>
        {
            ["active"] = true,
            ["scope"] = info.Token.Scopes,
            ["client_id"] = info.Client.ClientId,
            ["token_type"] = "Bearer",
            ["iat"] = info.Token.IssuedAt.ToUnixTimeSeconds(),
            ["exp"] = info.Token.ExpiresAt.ToUnixTimeSeconds(),
            ["sub"] = info.Token.UserId.ToString("D"),
        };
        if (info.Token.PatientId is { } patient)
        {
            body["patient"] = patient.ToString("D");
        }

        return TypedResults.Json(body);
    }

    /// <summary>RFC 7009: the client the token was issued to revokes it (a public app names itself); the answer is the same either way.</summary>
    private static async Task<IResult> Revoke(HttpContext http, SmartAuthorization smart, CancellationToken ct)
    {
        if (!http.Request.HasFormContentType)
        {
            return OAuth(OAuthError.InvalidRequest("Send the parameters as application/x-www-form-urlencoded."));
        }

        var form = await http.Request.ReadFormAsync(ct);
        SmartClient? client;
        if (form.ContainsKey("client_assertion"))
        {
            (client, var error) = await smart.AuthenticateAssertionAsync(form["client_assertion_type"], form["client_assertion"], NullIfEmpty(form["client_id"]),
                Origin(http.Request) + TokenPath, ct);
            if (error is not null)
            {
                return OAuth(error);
            }
        }
        else
        {
            client = await smart.ActiveClientAsync(form["client_id"], ct);
            if (client is null || client.Kind != SmartClientKind.App || client.JwksJson is not null)
            {
                return OAuth(OAuthError.InvalidClient("Authenticate with a client assertion, or name a public app's client_id."));
            }
        }

        await smart.RevokeAsync(client!, form["token"], ct);
        return TypedResults.Ok();
    }

    private static IReadOnlyList<string> RedirectUris(SmartClient client) => JsonSerializer.Deserialize<List<string>>(client.RedirectUrisJson) ?? [];

    private static string WithError(string redirect, string error, string? state, string? description = null)
    {
        var query = new Dictionary<string, string?> { ["error"] = error, ["error_description"] = description, ["state"] = state };
        return QueryHelpers.AddQueryString(redirect, query.Where(p => !string.IsNullOrEmpty(p.Value))!);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static IResult OAuth(OAuthError error)
    {
        var body = new { error = error.Code, error_description = error.Description };
        return TypedResults.Json(body, statusCode: error.Status);
    }
}
