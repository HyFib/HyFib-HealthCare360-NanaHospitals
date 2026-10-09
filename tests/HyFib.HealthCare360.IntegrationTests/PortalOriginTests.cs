using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// ASVS V3.5.4: the staff app, the patient portal and the API reference each have an address of their own, and each address
/// serves only its own app. Browsers send a host's cookies to every port of it, so these tests send the other app's cookie
/// on purpose: the server, not the browser, keeps the sessions apart.
/// </summary>
public sealed class PortalOriginTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Script_on_a_portal_page_cannot_use_a_staff_members_session()
    {
        // A staff member is signed in on this computer, so the browser sends their cookie to the portal's address too.
        var staffCookie = await server.SignInForRefreshCookieAsync(HospitalServer.Users.Nurse);

        // A script on a portal page asks for a staff token with it, claiming to be the staff app.
        var portalPage = server.AnonymousOnPortal(ownAddress: true);
        portalPage.Http.DefaultRequestHeaders.Remove(AuthService.ClientHeader);
        portalPage.Http.DefaultRequestHeaders.Add(AuthService.ClientHeader, "web");
        using (var stolen = await PostWithCookieAsync(portalPage, "/api/v1/auth/refresh", staffCookie))
        {
            stolen.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await stolen.Content.ReadAsStringAsync(Ct)).ShouldNotContain("accessToken");
        }

        // It cannot sign in as staff there, or end the staff member's session.
        using (var signIn = await portalPage.SendRawAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(HospitalServer.Users.Nurse, server.Password)))
        {
            signIn.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var signOut = await PostWithCookieAsync(portalPage, "/api/v1/auth/logout", staffCookie))
        {
            signOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // The staff member's session is untouched, and renews on the staff app's address as before.
        using var renewed = await server.RefreshAsync(staffCookie);
        renewed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_portal_signs_in_and_renews_on_its_own_address_only()
    {
        var (patient, portalCookie) = await server.SignInToPortalWithCookieAsync(NewMobile());

        using (var renewed = await PostWithCookieAsync(server.AnonymousOnPortal(ownAddress: true), "/api/v1/auth/refresh", portalCookie))
        {
            renewed.StatusCode.ShouldBe(HttpStatusCode.OK);
            portalCookie = HospitalServer.PortalRefreshCookieOf(renewed).ShouldNotBeNull();
        }

        // On the staff app's address the portal's cookie renews nothing, and neither the portal's sign-in nor its API answers.
        var staffPage = server.Anonymous(ownAddress: true);
        using (var refused = await PostWithCookieAsync(staffPage, "/api/v1/auth/refresh", portalCookie))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var code = await staffPage.SendRawAsync(HttpMethod.Post, "/api/v1/auth/otp/request", new OtpRequest(NewMobile())))
        {
            code.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using (var profiles = await server.OnStaffAddress(patient).SendRawAsync(HttpMethod.Get, "/api/v1/portal/profiles"))
        {
            profiles.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // On its own address the portal carries on.
        (await patient.GetAsync<JsonElement>("/api/v1/portal/profiles")).ValueKind.ShouldBe(JsonValueKind.Array);
        using var again = await PostWithCookieAsync(server.AnonymousOnPortal(ownAddress: true), "/api/v1/auth/refresh", portalCookie);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Portal_links_to_the_staff_apps_address_go_to_the_same_page_on_the_portals()
    {
        // As in an SMS sent, or a QR code printed, before the portal had an address of its own.
        using var response = await server.Anonymous().SendRawAsync(HttpMethod.Get, "/portal/feedback/abc?kiosk=true");

        response.StatusCode.ShouldBe(HttpStatusCode.Found);
        response.Headers.Location.ShouldBe(new Uri(server.PortalAddress, "/portal/feedback/abc?kiosk=true"));
    }

    [Fact]
    public async Task The_portals_address_serves_the_portal_and_nothing_of_the_staff_apps()
    {
        var visitor = server.AnonymousOnPortal();

        using (var home = await visitor.SendRawAsync(HttpMethod.Get, "/"))
        {
            home.StatusCode.ShouldBe(HttpStatusCode.Found);
            home.Headers.Location.ShouldBe(new Uri("/portal/", UriKind.Relative));
        }

        foreach (var staffOnly in new[] { "/api/v1/settings/public", "/api/v1/admin/users", "/hubs/events/negotiate", "/assets/index.js", "/fhir/r4/metadata" })
        {
            using var refused = await visitor.SendRawAsync(HttpMethod.Get, staffOnly);
            refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, staffOnly);
        }

        // What the portal shares with the staff app answers on both.
        foreach (var shared in new[] { "/api/v1/system/info", "/api/v1/public/privacy", "/health/live" })
        {
            using var answered = await visitor.SendRawAsync(HttpMethod.Get, shared);
            answered.StatusCode.ShouldBe(HttpStatusCode.OK, shared);
        }
    }

    [Fact]
    public async Task The_api_references_address_takes_no_cookies_and_sets_none()
    {
        var staffCookie = await server.SignInForRefreshCookieAsync(HospitalServer.Users.Nurse);
        var reference = server.AnonymousAt(server.ApiReferenceAddress);

        using (var refresh = await PostWithCookieAsync(reference, "/api/v1/auth/refresh", staffCookie))
        {
            refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Trying the API there signs in with a token only.
        using var signIn = await reference.SendRawAsync(HttpMethod.Post, "/api/v1/auth/login", new LoginRequest(HospitalServer.Users.Nurse, server.Password));
        signIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await signIn.Content.ReadAsStringAsync(Ct)).ShouldContain("accessToken");
        signIn.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    private static async Task<HttpResponseMessage> PostWithCookieAsync(ApiClient client, string path, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Cookie", cookie);
        return await client.Http.SendAsync(request, Ct);
    }

    private static string NewMobile() => "9" + Random.Shared.Next(100_000_000, 999_999_999);
}
