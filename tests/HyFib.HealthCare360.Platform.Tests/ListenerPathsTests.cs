using System.Net;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// ASVS V3.5.4: the staff app, the patient portal and the API reference each have an address of their own, and each address
/// serves only its own app (<see cref="ListenerPaths"/>).
/// </summary>
public sealed class ListenerPathsTests
{
    [Theory]
    [InlineData("/portal/")]
    [InlineData("/portal/bills/0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f")]
    [InlineData("/portal/assets/index-1a2b.js")]
    [InlineData("/api/v1/portal/profiles")]
    [InlineData("/api/v1/auth/otp/request")]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/auth/logout")]
    [InlineData("/api/v1/public/unsubscribe/abc")]
    [InlineData("/api/v1/public/rate-list/0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f")]
    [InlineData("/api/v1/radiology/reports/verify/ABC123")]
    [InlineData("/api/v1/system/info")]
    [InlineData("/api/v1/billing/webhooks/payment-gateway")]
    [InlineData("/api/v1/integrations/webhooks/whatsapp/")]
    [InlineData("/hubs/telehealth/negotiate")]
    [InlineData("/health/live")]
    public void The_portals_address_serves_the_portal_and_what_it_shares_with_the_staff_app(string path) =>
        ListenerPaths.Decide(Listener.Portal, path).ShouldBe(ListenerRoute.Serve);

    [Theory]
    [InlineData("/index.html")]
    [InlineData("/assets/index-1a2b.js")]
    [InlineData("/billing/bills")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/mfa")]
    [InlineData("/api/v1/auth/me")]
    [InlineData("/api/v1/admin/users")]
    [InlineData("/api/v1/settings/public")]
    [InlineData("/api/v1/radiology/reports/0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f/verify")]
    [InlineData("/api/v1/portalx")]
    [InlineData("/portalx")]
    [InlineData("/hubs/events")]
    [InlineData("/docs")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/iclock/cdata")]
    [InlineData("/fhir/r4/metadata")]
    public void Nothing_of_the_staff_apps_answers_on_the_portals_address(string path) =>
        ListenerPaths.Decide(Listener.Portal, path).ShouldBe(ListenerRoute.NotFound);

    [Fact]
    public void The_portals_address_opens_on_the_portal() =>
        ListenerPaths.Decide(Listener.Portal, "/").ShouldBe(ListenerRoute.ToPortalHome);

    [Theory]
    [InlineData("/portal")]
    [InlineData("/portal/")]
    [InlineData("/portal/feedback/abc")]
    [InlineData("/portal/assets/index-1a2b.js")]
    public void Portal_pages_asked_for_on_the_staff_apps_address_go_to_the_portals(string path) =>
        ListenerPaths.Decide(Listener.Staff, path).ShouldBe(ListenerRoute.ToPortal);

    [Theory]
    [InlineData("/api/v1/portal/profiles")]
    [InlineData("/api/v1/auth/otp/request")]
    [InlineData("/api/v1/auth/otp/verify")]
    public void The_staff_apps_address_does_not_serve_the_portals_own_api(string path) =>
        ListenerPaths.Decide(Listener.Staff, path).ShouldBe(ListenerRoute.NotFound);

    [Theory]
    [InlineData("/")]
    [InlineData("/billing/bills")]
    [InlineData("/assets/index-1a2b.js")]
    [InlineData("/api/v1/auth/login")]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/public/doctors/")]
    [InlineData("/api/v1/radiology/reports/0199b0c4-4b1c-7c3e-9d2f-2a1b3c4d5e6f/verify")]
    [InlineData("/hubs/events")]
    [InlineData("/hubs/telehealth")]
    [InlineData("/iclock/cdata")]
    [InlineData("/fhir/r4/metadata")]
    public void The_staff_apps_address_serves_everything_else(string path) =>
        ListenerPaths.Decide(Listener.Staff, path).ShouldBe(ListenerRoute.Serve);

    [Theory]
    [InlineData("/docs")]
    [InlineData("/openapi/v1.json")]
    public void The_api_reference_asked_for_on_the_staff_apps_address_goes_to_its_own(string path) =>
        ListenerPaths.Decide(Listener.Staff, path).ShouldBe(ListenerRoute.ToApiReference);

    [Theory]
    [InlineData("/docs", ListenerRoute.Serve)]
    [InlineData("/docs/scalar.js", ListenerRoute.Serve)]
    [InlineData("/openapi/v1.json", ListenerRoute.Serve)]
    [InlineData("/api/v1/patients", ListenerRoute.Serve)]
    [InlineData("/", ListenerRoute.ToApiReferenceHome)]
    [InlineData("/portal/", ListenerRoute.NotFound)]
    [InlineData("/index.html", ListenerRoute.NotFound)]
    [InlineData("/hubs/events", ListenerRoute.NotFound)]
    public void The_api_references_address_serves_the_reference_and_the_api_it_describes(string path, ListenerRoute route) =>
        ListenerPaths.Decide(Listener.ApiReference, path).ShouldBe(route);

    [Fact]
    public void An_address_is_told_by_the_port_it_listens_on_not_by_what_the_client_says()
    {
        var options = new PlatformOptions();

        Listeners.ForPort(7360, options).ShouldBe(Listener.Staff);
        Listeners.ForPort(7361, options).ShouldBe(Listener.Portal);
        Listeners.ForPort(7362, options).ShouldBe(Listener.ApiReference);
        Listeners.ForPort(8080, options).ShouldBe(Listener.Staff);
    }

    [Fact]
    public void Each_address_needs_a_port_of_its_own()
    {
        Listeners.PortProblem(new PlatformOptions(), apiReferenceOn: true).ShouldBeNull();
        Listeners.PortProblem(new PlatformOptions { PortalHttpsPort = 7360 }, apiReferenceOn: false)
            .ShouldBe("HealthCare360:HttpsPort and HealthCare360:PortalHttpsPort are both 7360: each needs a port of its own.");
        Listeners.PortProblem(new PlatformOptions { HttpRedirectPort = 7361 }, apiReferenceOn: false).ShouldNotBeNull();
        Listeners.PortProblem(new PlatformOptions { PortalHttpsPort = 0 }, apiReferenceOn: false)
            .ShouldBe("HealthCare360:PortalHttpsPort must be a port between 1 and 65535, not 0.");

        // The reference's port is only listened on while the reference is on.
        Listeners.PortProblem(new PlatformOptions { ApiReferencePort = 7361 }, apiReferenceOn: false).ShouldBeNull();
        Listeners.PortProblem(new PlatformOptions { ApiReferencePort = 7361 }, apiReferenceOn: true).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(7360, "the staff app")]
    [InlineData(7361, "the patient portal")]
    [InlineData(7362, "the API reference")]
    [InlineData(5100, null)]
    [InlineData(0, null)]
    public void Names_the_part_of_the_server_that_listens_on_a_port(int port, string? use) =>
        Listeners.ServerPortUse(port, new PlatformOptions(), apiReferenceOn: true).ShouldBe(use);

    [Fact]
    public void Names_only_the_ports_the_server_listens_on_with_its_settings()
    {
        Listeners.ServerPortUse(8443, new PlatformOptions { PortalHttpsPort = 8443 }, apiReferenceOn: false).ShouldBe("the patient portal");
        Listeners.ServerPortUse(7361, new PlatformOptions { PortalHttpsPort = 8443 }, apiReferenceOn: false).ShouldBeNull();

        // The API reference's port while the reference is on, and the redirect from plain HTTP's when IT gives it one.
        Listeners.ServerPortUse(7362, new PlatformOptions(), apiReferenceOn: false).ShouldBeNull();
        Listeners.ServerPortUse(80, new PlatformOptions(), apiReferenceOn: false).ShouldBeNull();
        Listeners.ServerPortUse(80, new PlatformOptions { HttpRedirectPort = 80 }, apiReferenceOn: false).ShouldBe("the redirect from plain HTTP");
    }

    [Fact]
    public async Task A_portal_page_asked_for_on_the_staff_apps_address_is_sent_to_the_same_page_on_the_portals()
    {
        var context = Request(7360, "[::1]:7360", "/portal/feedback/abc", "?kiosk=true");

        (await RunAsync(context)).ShouldBeFalse();

        context.Response.StatusCode.ShouldBe(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().ShouldBe("https://[::1]:7361/portal/feedback/abc?kiosk=true");
    }

    [Fact]
    public async Task What_an_address_does_not_serve_is_not_found()
    {
        var context = Request(7361, "hc360-server:7361", "/api/v1/auth/login");

        (await RunAsync(context)).ShouldBeFalse();

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task The_address_is_recorded_for_the_rest_of_the_request()
    {
        var context = Request(7361, "hc360-server:7361", "/api/v1/portal/profiles");

        (await RunAsync(context)).ShouldBeTrue();

        Listeners.Of(context).ShouldBe(Listener.Portal);
        Listeners.Of(new DefaultHttpContext()).ShouldBe(Listener.Staff);
    }

    [Fact]
    public async Task The_api_references_address_takes_no_cookies()
    {
        var context = Request(7362, "localhost:7362", "/api/v1/auth/refresh");
        context.Request.Headers.Cookie = "hc360_rt=a-staff-session";

        (await RunAsync(context)).ShouldBeTrue();

        context.Request.Cookies.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("127.0.0.1", "Production", true, StatusCodes.Status302Found)]
    [InlineData("10.20.0.5", "Production", true, StatusCodes.Status404NotFound)]
    [InlineData("127.0.0.1", "Production", false, StatusCodes.Status404NotFound)]
    [InlineData("127.0.0.1", "Development", false, StatusCodes.Status302Found)]
    public async Task The_api_reference_on_the_staff_apps_address_is_sent_on_only_while_it_is_on_and_from_the_server_machine(
        string client, string environment, bool enabled, int status)
    {
        var context = Request(7360, "localhost:7360", "/docs");
        context.Connection.RemoteIpAddress = IPAddress.Parse(client);

        (await RunAsync(context, environment, enabled)).ShouldBeFalse();

        context.Response.StatusCode.ShouldBe(status);
        if (status == StatusCodes.Status302Found)
        {
            context.Response.Headers.Location.ToString().ShouldBe("https://localhost:7362/docs");
        }
    }

    private static DefaultHttpContext Request(int port, string host, string path, string query = "")
    {
        var context = new DefaultHttpContext();
        context.Connection.LocalPort = port;
        context.Request.Host = new HostString(host);
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context;
    }

    /// <summary>Runs the gate; true when it passed the request on.</summary>
    private static async Task<bool> RunAsync(HttpContext context, string environment = "Development", bool enableApiReference = false)
    {
        var reached = false;
        var gate = new ListenerGateMiddleware(
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            },
            Options.Create(new PlatformOptions { EnableApiReference = enableApiReference }),
            new TestEnvironment(environment));

        await gate.InvokeAsync(context);
        return reached;
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "HC360";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
