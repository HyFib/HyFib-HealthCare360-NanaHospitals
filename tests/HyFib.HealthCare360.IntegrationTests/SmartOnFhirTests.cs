using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Interop.Domain;
using HyFib.HealthCare360.Modules.Interop.Features;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SMART on FHIR for partner applications, through the whole server: a client registered by one administrator and
/// approved by another; a backend service's token by client credentials with a signed assertion; an app's token by
/// authorization code with PKCE after a staff member authorizes it for one patient; scopes narrowing what a token reads
/// while each patient's consent still decides; introspection, revocation, suspension; and a partner moved off password
/// sign-in, while partners not yet moved keep signing in.
/// </summary>
public sealed class SmartOnFhirTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_backend_service_reads_with_client_credentials_only_what_consents_and_scopes_allow()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var (legacy, _, _) = await PartnerAsync(server);
        var facilityId = (await partner.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId!.Value;
        using var key = RSA.Create(3072);
        var client = await RegisterAndApproveAsync(new RegisterSmartClientRequest("Referral HIE", SmartClientKind.BackendService, null, "system/Patient.rs system/AllergyIntolerance.read",
            Jwks(key), partnerId, facilityId, ReplacesPasswordSignIn: true, "The HIE moves from a password to signed assertions."));

        // Shared with the partner's account; another patient shares with nobody.
        var shared = await RegisterAsync(frontOffice);
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{shared}/consents", new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], null, null, null, null, null)));
        var notShared = await RegisterAsync(frontOffice);

        var anonymous = server.Anonymous();
        var tokenEndpoint = new Uri(anonymous.Http.BaseAddress!, SmartEndpoints.TokenPath).ToString();

        // A token for the scopes asked that the client is registered for; others are dropped and said so.
        var assertion = Assertion(client.ClientId, tokenEndpoint, key);
        var token = await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "client_credentials", ["scope"] = "system/Patient.rs system/Observation.rs system/Patient.cud",
            ["client_assertion_type"] = SmartAuthorization.ClientAssertionType, ["client_assertion"] = assertion,
        });
        token.Status.ShouldBe(HttpStatusCode.OK);
        token.Body.GetProperty("scope").GetString().ShouldBe("system/Patient.rs");
        token.Body.GetProperty("token_type").GetString().ShouldBe("Bearer");
        var bearer = token.Body.GetProperty("access_token").GetString()!;

        // The same assertion is good once.
        (await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "client_credentials", ["scope"] = "system/Patient.rs",
            ["client_assertion_type"] = SmartAuthorization.ClientAssertionType, ["client_assertion"] = assertion,
        })).Error.ShouldBe("invalid_client");

        // Signed by another key, or for another audience: refused.
        using var other = RSA.Create(2048);
        (await TokenAsync(anonymous, Credentials(Assertion(client.ClientId, tokenEndpoint, other)))).Error.ShouldBe("invalid_client");
        (await TokenAsync(anonymous, Credentials(Assertion(client.ClientId, "https://elsewhere.test/token", key)))).Error.ShouldBe("invalid_client");

        // The consent still decides: the shared patient is read, the other is not.
        var smart = WithBearer(anonymous, bearer);
        (await FhirAsync(smart, $"Patient/{shared}", null)).Status.ShouldBe(200);
        (await FhirAsync(smart, $"Patient/{notShared}", null)).Refusal.ShouldBe((403, "consent-required"));

        // The scopes narrow it further: no Observation, though the consent shares the whole record.
        (await FhirAsync(smart, $"Observation?patient={shared}", null)).Refusal.ShouldBe((403, "scope-insufficient"));

        // A SMART token reads the FHIR API and nothing else.
        using (var elsewhere = await smart.SendRawAsync(HttpMethod.Get, "/api/v1/auth/me"))
        {
            elsewhere.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // The partner moved to SMART no longer reads with its password session; a partner not moved still does.
        (await FhirAsync(partner, $"Patient/{shared}", null)).Refusal.ShouldBe((403, "smart-required"));
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{notShared}/consents", new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null));
        (await FhirAsync(legacy, $"Patient/{notShared}", null)).Status.ShouldBe(200);

        // Introspection, for the client the token is its own; then revoked, it reads nothing.
        var active = await FormAsync(anonymous, SmartEndpoints.IntrospectPath, new()
        {
            ["token"] = bearer, ["client_assertion_type"] = SmartAuthorization.ClientAssertionType, ["client_assertion"] = Assertion(client.ClientId, tokenEndpoint, key),
        });
        (active.Body.GetProperty("active").GetBoolean(), active.Body.GetProperty("scope").GetString(), active.Body.GetProperty("client_id").GetString())
            .ShouldBe((true, "system/Patient.rs", client.ClientId));
        (await FormAsync(anonymous, SmartEndpoints.RevokePath, new()
        {
            ["token"] = bearer, ["client_assertion_type"] = SmartAuthorization.ClientAssertionType, ["client_assertion"] = Assertion(client.ClientId, tokenEndpoint, key),
        })).Status.ShouldBe(HttpStatusCode.OK);
        (await FormAsync(anonymous, SmartEndpoints.IntrospectPath, new()
        {
            ["token"] = bearer, ["client_assertion_type"] = SmartAuthorization.ClientAssertionType, ["client_assertion"] = Assertion(client.ClientId, tokenEndpoint, key),
        })).Body.GetProperty("active").GetBoolean().ShouldBeFalse();
        using (var revoked = await RawFhirAsync(smart, $"Patient/{shared}"))
        {
            revoked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Suspended: its tokens stop and no more are issued.
        var fresh = (await TokenAsync(anonymous, Credentials(Assertion(client.ClientId, tokenEndpoint, key)))).Body.GetProperty("access_token").GetString()!;
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var listed = (await admin.GetAsync<List<SmartClientDto>>("/api/v1/smart/clients")).Single(c => c.Id == client.Id);
        await admin.PostAsync($"/api/v1/smart/clients/{client.Id}/suspend", new SuspendSmartClientRequest(listed.Version, "The HIE contract ended."));
        using (var suspended = await RawFhirAsync(WithBearer(anonymous, fresh), $"Patient/{shared}"))
        {
            suspended.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await TokenAsync(anonymous, Credentials(Assertion(client.ClientId, tokenEndpoint, key)))).Error.ShouldBe("invalid_client");
    }

    [Fact]
    public async Task An_app_reads_one_patient_after_a_staff_member_authorizes_it_with_pkce()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var client = await RegisterAndApproveAsync(new RegisterSmartClientRequest("Referral viewer", SmartClientKind.App, ["https://viewer.test/callback"], "patient/*.rs",
            null, null, null, false, "A referral partner's viewer app."));
        var patient = await RegisterAsync(frontOffice);
        var other = await RegisterAsync(frontOffice);
        foreach (var id in new[] { patient, other })
        {
            await frontOffice.PostAsync<Created>($"/api/v1/patients/{id}/consents", new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null));
        }

        var uhid = (await frontOffice.GetAsync<JsonElement>($"/api/v1/patients/{patient}")).GetProperty("uhid").GetString()!;
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        var challenge = SmartAuthorization.Challenge(verifier);
        var anonymous = server.Anonymous();

        // The authorization request goes to the staff app's screen; a bad redirect URI is answered here, never followed.
        var query = $"?response_type=code&client_id={client.ClientId}&redirect_uri={Uri.EscapeDataString("https://viewer.test/callback")}"
                    + $"&scope={Uri.EscapeDataString("launch/patient patient/*.rs")}&state=xyz&code_challenge={challenge}&code_challenge_method=S256";
        using (var authorize = await anonymous.SendRawAsync(HttpMethod.Get, SmartEndpoints.AuthorizePath + query))
        {
            authorize.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            authorize.Headers.Location!.ToString().ShouldStartWith(SmartEndpoints.StaffScreen + "?");
        }

        using (var bad = await anonymous.SendRawAsync(HttpMethod.Get, SmartEndpoints.AuthorizePath + query.Replace("viewer.test", "evil.test", StringComparison.Ordinal)))
        {
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // A staff member who reads FHIR authorizes it for the patient.
        var staff = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var preview = await staff.GetAsync<SmartAuthorizationPreview>($"/api/v1/smart/authorize/preview{query}");
        (preview.ClientName, preview.NeedsPatient).ShouldBe(("Referral viewer", true));
        var decided = await staff.PostAsync<SmartAuthorizeResult>("/api/v1/smart/authorize",
            new SmartAuthorizeRequest(client.ClientId, "https://viewer.test/callback", "launch/patient patient/*.rs", "xyz", challenge, "S256", uhid, Approve: true));
        var back = QueryHelpers.ParseQuery(new Uri(decided.Redirect).Query);
        back["state"].ToString().ShouldBe("xyz");
        var code = back["code"].ToString();

        // A wrong verifier is refused; the right one gets a token for the patient.
        (await TokenAsync(anonymous, new() { ["grant_type"] = "authorization_code", ["client_id"] = client.ClientId, ["code"] = code,
            ["redirect_uri"] = "https://viewer.test/callback", ["code_verifier"] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48)) })).Error.ShouldBe("invalid_grant");
        var token = await TokenAsync(anonymous, new() { ["grant_type"] = "authorization_code", ["client_id"] = client.ClientId, ["code"] = code,
            ["redirect_uri"] = "https://viewer.test/callback", ["code_verifier"] = verifier });
        token.Status.ShouldBe(HttpStatusCode.OK);
        token.Body.GetProperty("patient").GetString().ShouldBe(patient.ToString("D"));
        token.Body.GetProperty("scope").GetString().ShouldBe("launch/patient patient/*.rs");
        var app = WithBearer(anonymous, token.Body.GetProperty("access_token").GetString()!);

        // Its patient's record, and no other's, though both consented to share with every account.
        (await FhirAsync(app, $"Patient/{patient}", null)).Status.ShouldBe(200);
        (await FhirAsync(app, $"AllergyIntolerance?patient={patient}", null)).Status.ShouldBe(200);
        (await FhirAsync(app, $"Patient/{other}", null)).Refusal.ShouldBe((403, "scope-insufficient"));
        (await FhirAsync(app, $"AllergyIntolerance?patient={other}", null)).Refusal.ShouldBe((403, "scope-insufficient"));
        (await FhirAsync(app, "Patient?name=Sharing", null)).Refusal.ShouldBe((403, "scope-insufficient"));
        (await FhirAsync(app, $"Patient?_id={patient}", null)).Status.ShouldBe(200);

        // The code again: refused, and the token it was exchanged for stops working.
        (await TokenAsync(anonymous, new() { ["grant_type"] = "authorization_code", ["client_id"] = client.ClientId, ["code"] = code,
            ["redirect_uri"] = "https://viewer.test/callback", ["code_verifier"] = verifier })).Error.ShouldBe("invalid_grant");
        using (var stolen = await RawFhirAsync(app, $"Patient/{patient}"))
        {
            stolen.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Turned down: back to the app with access_denied.
        var denied = await staff.PostAsync<SmartAuthorizeResult>("/api/v1/smart/authorize",
            new SmartAuthorizeRequest(client.ClientId, "https://viewer.test/callback", "patient/*.rs", "s2", challenge, "S256", null, Approve: false));
        QueryHelpers.ParseQuery(new Uri(denied.Redirect).Query)["error"].ToString().ShouldBe("access_denied");
    }

    [Fact]
    public async Task A_client_is_issued_nothing_until_a_second_administrator_approves_it()
    {
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using var response = await maker.SendRawAsync(HttpMethod.Post, "/api/v1/smart/clients", new RegisterSmartClientRequest("Pending app", SmartClientKind.App,
            ["https://pending.test/cb"], "user/*.rs", null, null, null, false, "Trying it."));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var registered = (await response.Content.ReadFromJsonAsync<SmartClientRegistered>(ApiClient.Json, Ct))!;
        registered.Client.Status.ShouldBe(SmartClientStatus.PendingApproval);

        // Not approved: no authorization request is taken. Not by the one who registered it.
        using (var authorize = await server.Anonymous().SendRawAsync(HttpMethod.Get,
                   $"{SmartEndpoints.AuthorizePath}?response_type=code&client_id={registered.Client.ClientId}&redirect_uri={Uri.EscapeDataString("https://pending.test/cb")}"))
        {
            authorize.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await BirthsAndDeathsTests.RefusalAsync(maker, HttpMethod.Post, $"/api/v1/approvals/{registered.Approval.Id}/approve",
            new ApproveRequest(null, registered.Approval.Version))).ShouldBe("approval.self");

        // A write scope, a private key or a plain-http redirect cannot be registered.
        using var rsa = RSA.Create(2048);
        var withPrivate = JsonSerializer.SerializeToElement(new { keys = new[] { JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(true))) } });
        foreach (var bad in new[]
                 {
                     new RegisterSmartClientRequest("Bad", SmartClientKind.App, ["https://x.test/cb"], "user/*.cruds", null, null, null, false, "x"),
                     new RegisterSmartClientRequest("Bad", SmartClientKind.App, ["http://x.test/cb"], "user/*.rs", null, null, null, false, "x"),
                     new RegisterSmartClientRequest("Bad", SmartClientKind.App, ["https://x.test/cb"], "user/*.rs", withPrivate, null, null, false, "x"),
                 })
        {
            using var refused = await maker.SendRawAsync(HttpMethod.Post, "/api/v1/smart/clients", bad);
            ((int)refused.StatusCode).ShouldBeInRange(400, 422);
        }

        // The discovery document and the CapabilityStatement say how to get a token.
        var configuration = await server.Anonymous().GetAsync<JsonElement>("/fhir/r4/.well-known/smart-configuration");
        configuration.GetProperty("token_endpoint").GetString()!.ShouldEndWith(SmartEndpoints.TokenPath);
        configuration.GetProperty("code_challenge_methods_supported")[0].GetString().ShouldBe("S256");
        var metadata = (await FhirAsync(server.Anonymous(), "metadata", null)).Body;
        var security = metadata.GetProperty("rest")[0].GetProperty("security");
        security.GetProperty("service")[0].GetProperty("coding")[0].GetProperty("code").GetString().ShouldBe("SMART-on-FHIR");
        security.GetProperty("extension")[0].GetProperty("url").GetString().ShouldBe("http://fhir-registry.smarthealthit.org/StructureDefinition/oauth-uris");
    }

    private async Task<SmartClientDto> RegisterAndApproveAsync(RegisterSmartClientRequest request)
    {
        var maker = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using var response = await maker.SendRawAsync(HttpMethod.Post, "/api/v1/smart/clients", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var registered = (await response.Content.ReadFromJsonAsync<SmartClientRegistered>(ApiClient.Json, Ct))!;
        await (await server.SignInAsync(HospitalServer.Users.Admin)).PostAsync($"/api/v1/approvals/{registered.Approval.Id}/approve",
            new ApproveRequest(null, registered.Approval.Version));
        return registered.Client;
    }

    private static JsonElement Jwks(RSA key) =>
        JsonSerializer.SerializeToElement(new { keys = new[] { new { kty = "RSA", kid = "k1", n = Base64UrlEncoder.Encode(key.ExportParameters(false).Modulus!), e = Base64UrlEncoder.Encode(key.ExportParameters(false).Exponent!) } } });

    private static string Assertion(string clientId, string audience, RSA key)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience,
            Claims = new Dictionary<string, object> { ["sub"] = clientId, ["jti"] = Guid.NewGuid().ToString("N") },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(4),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha384),
        });
    }

    private static Dictionary<string, string> Credentials(string assertion) => new()
    {
        ["grant_type"] = "client_credentials", ["scope"] = "system/Patient.rs",
        ["client_assertion_type"] = SmartAuthorization.ClientAssertionType, ["client_assertion"] = assertion,
    };

    private sealed record OAuthAnswer(HttpStatusCode Status, JsonElement Body)
    {
        public string? Error => Body.TryGetProperty("error", out var error) ? error.GetString() : null;
    }

    private static Task<OAuthAnswer> TokenAsync(ApiClient client, Dictionary<string, string> form) => FormAsync(client, SmartEndpoints.TokenPath, form);

    private static async Task<OAuthAnswer> FormAsync(ApiClient client, string path, Dictionary<string, string> form)
    {
        using var response = await client.SendContentAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form));
        var text = await response.Content.ReadAsStringAsync(Ct);
        return new OAuthAnswer(response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private ApiClient WithBearer(ApiClient anonymous, string token)
    {
        var client = server.Anonymous();
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<HttpResponseMessage> RawFhirAsync(ApiClient client, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/fhir/r4/" + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/fhir+json"));
        return await client.Http.SendAsync(request, Ct);
    }
}
