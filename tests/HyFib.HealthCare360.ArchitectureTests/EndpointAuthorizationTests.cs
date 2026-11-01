using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Server;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.ArchitectureTests;

/// <summary>
/// Every endpoint says who may call it: holders of a permission, any signed-in user, or anyone. The last two are
/// reviewed lists below, so a new endpoint open to every signed-in user (patients on the portal included) or to the
/// internet is a deliberate, visible decision. An endpoint that says nothing fails the first test; at run time the
/// server's fallback policy would still require sign-in. What each way in takes, and what stops guessing it, is in
/// docs/security/SECURITY.md (Sign-in pathways): a new anonymous endpoint belongs there too.
/// </summary>
public sealed class EndpointAuthorizationTests
{
    /// <summary>The server's endpoints, composed once for every endpoint test.</summary>
    internal static readonly Lazy<WebApplication> App = new(BuildApp);

    private static readonly Lazy<IReadOnlyList<EndpointAccess>> Endpoints = new(() =>
        [.. ((IEndpointRouteBuilder)App.Value).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().SelectMany(EndpointAccess.Of)]);

    /// <summary>Endpoints anyone may call: sign-in, public links and pages, device and provider callbacks.</summary>
    private static readonly string[] Anonymous =
    [
        // Signing in (rate-limited). Logout and refresh work from the refresh cookie alone.
        "POST /api/v1/auth/login",
        "POST /api/v1/auth/logout",
        "POST /api/v1/auth/mfa",
        "POST /api/v1/auth/otp/request",
        "POST /api/v1/auth/otp/verify",
        "POST /api/v1/auth/refresh",

        // Workstation set-up, the sign-in page and the portal: the server's public CA certificate, name and version, and
        // whether the patient portal and video consultations are switched on (hospital-wide, nothing per person). While the
        // hospital CA is rolled over, the next CA and the one it replaced: public certificates too (CA-ROLLOVER.md).
        "GET /api/v1/system/ca-certificate",
        "GET /api/v1/system/ca-certificate/next",
        "GET /api/v1/system/ca-certificate/previous",
        "GET /api/v1/system/info",
        "POST /api/v1/devices/enroll",
        "POST /api/v1/devices/heartbeat",

        // HC360 Desktop's self-update (SRS DED-18): an enrolled, unrevoked HC360 Desktop's device id and secret (hash
        // compared in constant time); anyone else gets 401. The package is HyFib's signed release, which holds no secret.
        "GET /api/v1/devices/desktop-update",
        "GET /api/v1/devices/desktop-update/package",

        // Patient links from SMS/WhatsApp and printed QR codes: an unguessable token or code. Report checks name the patient
        // by initials only; the others return no patient data.
        "GET /api/v1/public/feedback/forms/{token}",
        "POST /api/v1/public/feedback/forms/{token}",
        "GET /api/v1/public/feedback/invitations/{token}",
        "POST /api/v1/public/feedback/invitations/{token}",
        "GET /api/v1/public/unsubscribe/{token}",
        "POST /api/v1/public/unsubscribe/{token}",
        "GET /api/v1/public/prescriptions/verify/{token}",
        "GET /api/v1/public/prescriptions/verify/{token}/signature.p7s",
        "GET /api/v1/lab/reports/verify/{code}",
        "GET /api/v1/radiology/reports/verify/{code}",
        "GET /api/v1/cardiology/reports/verify/{code}",

        // The public doctor directory for online booking.
        "GET /api/v1/public/doctors/",
        "GET /api/v1/public/doctors/filters",
        "GET /api/v1/public/doctors/{id:guid}",
        "GET /api/v1/public/doctors/{id:guid}/slots",

        // The rate list the hospital displays, for anyone to read (Clinical Establishments Rules 2012, r.9(i)): the
        // facilities that publish it, a facility's list, and its PDF.
        "GET /api/v1/public/rate-list/",
        "GET /api/v1/public/rate-list/{facilityId:guid}",
        "GET /api/v1/public/rate-list/{facilityId:guid}/pdf",

        // A proof-of-work challenge for the portal's sign-in and feedback forms (SRS SEC-06): a signed random value, no data.
        "POST /api/v1/public/challenge",

        // The privacy notice names the grievance officer and the response period for everyone (DPDP Rules 2025, rules 9
        // and 14): published contact details only.
        "GET /api/v1/public/privacy",

        // Telehealth: every hub call is checked against a signed-in patient or a guest join token.
        "ANY /hubs/telehealth",
        "ANY /hubs/telehealth/negotiate",

        // Provider callbacks, each verified by the provider's signature or shared secret.
        "POST /api/v1/billing/webhooks/payment-gateway",
        "GET /api/v1/integrations/webhooks/sms/generic",
        "POST /api/v1/integrations/webhooks/sms/generic",
        "POST /api/v1/integrations/webhooks/sms/msg91",
        "POST /api/v1/integrations/webhooks/sms/twilio",
        "GET /api/v1/integrations/webhooks/whatsapp/",
        "POST /api/v1/integrations/webhooks/whatsapp/",

        // The ABDM gateway's callbacks, at the paths the gateway calls (only while ABDM is switched on): each verified by a
        // bearer token signed with a key the gateway publishes (AbdmGatewayKeys).
        "POST /api/v3/hip/token/on-generate-token",
        "POST /api/v3/link/on_carecontext",
        "POST /api/v3/hip/patient/care-context/discover",
        "POST /api/v3/hip/link/care-context/init",
        "POST /api/v3/hip/link/care-context/confirm",
        "POST /api/v3/consent/request/hip/notify",
        "POST /api/v3/hip/health-information/request",
        "POST /api/v3/hip/patient/share",
        "POST /api/v3/hiu/consent/request/on-init",
        "POST /api/v3/hiu/consent/request/notify",
        "POST /api/v3/hiu/consent/on-fetch",
        "POST /api/v3/hiu/health-information/on-request",

        // Where other providers post the records the hospital asked for (ABDM's data push carries no token): admitted only
        // for a transaction awaiting pages, and readable only with that request's own key (AbdmHealthInformationService).
        "POST /api/v3/hiu/health-information/transfer",

        // Devices that authenticate with their own key or serial number: attendance terminals and patient monitors.
        "GET /iclock/cdata",
        "POST /iclock/cdata",
        "GET /iclock/getrequest",
        "POST /api/v1/hr/attendance/device-punches",
        "POST /api/v1/rpm/ingest",

        // HC360 lab agents: an enrolled, unrevoked lab agent's device id and secret (hash compared in constant time); the
        // relay serves only the analysers assigned to that agent.
        "POST /api/v1/integrations/lab/agent/heartbeat",
        "GET /api/v1/integrations/lab/agent/relay/{code}",

        // FHIR requires a public capability statement; the catch-all only answers "not found" in FHIR's format.
        "GET /fhir/r4/metadata",
        "ANY /fhir/{**rest}",

        // Health checks for the service manager and monitoring: they say only whether the server and database are up.
        "ANY /health/live",
        "ANY /health/ready",

        // An unknown API address answers "not found" instead of the staff app's page.
        "ANY /api/{**rest}",
    ];

    /// <summary>Endpoints any signed-in user may call, each of which must limit itself to the caller's own data.</summary>
    private static readonly string[] AnySignedInUser =
    [
        // The caller's own account, sessions, sign-in factors, PIN, lock screen and facility.
        "GET /api/v1/auth/me",
        "GET /api/v1/auth/sessions",
        "POST /api/v1/auth/sessions/{id:guid}/end",
        "POST /api/v1/auth/sessions/end-others",
        "POST /api/v1/auth/change-password",
        "POST /api/v1/auth/mfa/setup",
        "POST /api/v1/auth/mfa/enable",
        "POST /api/v1/auth/mfa/disable",
        "POST /api/v1/auth/pin",
        "POST /api/v1/auth/lock",
        "POST /api/v1/auth/unlock",

        // Quick unlock with the device's Windows Hello (WebAuthn): only the caller's own devices, and only the caller's
        // own session is unlocked; setting a device up asks for the password.
        "POST /api/v1/auth/unlock/webauthn/options",
        "POST /api/v1/auth/unlock/webauthn",
        "GET /api/v1/auth/webauthn/credentials",
        "POST /api/v1/auth/webauthn/credentials/options",
        "POST /api/v1/auth/webauthn/credentials",
        "DELETE /api/v1/auth/webauthn/credentials/{id:guid}",
        "POST /api/v1/auth/switch-facility",

        // The caller's own notifications.
        "GET /api/v1/notifications/",
        "GET /api/v1/notifications/unread-count",
        "POST /api/v1/notifications/{id:guid}/read",
        "POST /api/v1/notifications/read-all",

        // Approvals (maker-checker), staff only: patients are refused. A caller sees the requests they asked for and those
        // of the kinds they hold the approving permission for; a decision checks that permission again and refuses the
        // person who asked, and only the person who asked may withdraw.
        "GET /api/v1/approvals/",
        "GET /api/v1/approvals/{id:guid}",
        "POST /api/v1/approvals/{id:guid}/approve",
        "POST /api/v1/approvals/{id:guid}/reject",
        "POST /api/v1/approvals/{id:guid}/withdraw",

        // A record's history (the audit trail of it), staff only: patients are refused. The module that owns the record
        // names the permissions that read it and opens it by the rules its own screen does (facility, restricted record).
        "GET /api/v1/history/{entityType}/{entityId:guid}",

        // Branding and feature switches (non-secret "ui.*" and "features.*" settings only; every switch as the server
        // enforces it for the caller's facility).
        "GET /api/v1/settings/public",

        // The letterhead and print templates of the caller's facility, for the staff app's own prints (staff only;
        // patients are refused). It holds nothing that is not printed on the documents the hospital hands out.
        "GET /api/v1/print/letterhead",

        // Live updates: every topic subscription is authorized separately (RealtimeTopicAuthorizer).
        "ANY /hubs/events",
        "ANY /hubs/events/negotiate",
    ];

    [Fact]
    public void Every_endpoint_says_who_may_call_it()
    {
        var undeclared = Endpoints.Value.Where(e => e.Access == Access.Undeclared).Select(e => e.Key).Order().ToList();

        undeclared.ShouldBeEmpty(
            "These endpoints have no authorization of their own. Add RequirePermission(...), or AllowAnonymous() / " +
            "RequireAuthorization() together with an entry in the reviewed lists of this test:\n" + string.Join('\n', undeclared));
    }

    [Fact]
    public void Only_the_reviewed_endpoints_are_anonymous() =>
        Compare(Access.Anonymous, Anonymous, nameof(Anonymous));

    [Fact]
    public void Only_the_reviewed_endpoints_are_open_to_any_signed_in_user() =>
        Compare(Access.AnySignedInUser, AnySignedInUser, nameof(AnySignedInUser));

    private static void Compare(Access access, string[] reviewed, string listName)
    {
        var actual = Endpoints.Value.Where(e => e.Access == access).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var unreviewed = actual.Except(reviewed, StringComparer.Ordinal).Order().ToList();
        var stale = reviewed.Except(actual, StringComparer.Ordinal).Order().ToList();

        unreviewed.ShouldBeEmpty($"Not in the reviewed list {listName}; add them only after checking they are safe:\n" + string.Join('\n', unreviewed));
        stale.ShouldBeEmpty($"In {listName} but no longer {access}; remove them from the list:\n" + string.Join('\n', stale));
    }

    [Theory]
    [InlineData("/api/v1/admin/backups/recovery-key")]
    [InlineData("/api/v1/admin/backups/recovery-key/{keyId}/confirm")]
    public void Making_a_backup_recovery_key_needs_the_system_permission_as_well(string route)
    {
        // Whoever runs backups sees which recovery key they use; making a new one shows it, so it needs more (ASVS V13.3.2).
        var endpoint = ((IEndpointRouteBuilder)App.Value).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Single(e => "/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/') == route
                         && e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ShouldBeSubsetOf(
            [PermissionPolicyProvider.Prefix + PlatformPermissions.BackupsManage, PermissionPolicyProvider.Prefix + PlatformPermissions.SystemManage]);
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ShouldContain(PermissionPolicyProvider.Prefix + PlatformPermissions.SystemManage);
    }

    [Fact]
    public async Task A_request_whose_endpoint_states_nothing_needs_a_signed_in_user()
    {
        var provider = App.Value.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await provider.GetFallbackPolicyAsync();

        fallback.ShouldNotBeNull().Requirements.ShouldContain(r => r is DenyAnonymousAuthorizationRequirement);
    }

    /// <summary>Builds the server's endpoints as Program.cs composes them, without starting it or opening the database.</summary>
    private static WebApplication BuildApp()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "hc360-tests", "endpoints-" + Guid.NewGuid().ToString("N"));
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Development,
                ContentRootPath = AppContext.BaseDirectory,
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Hospital"] = "Host=localhost;Database=hc360_endpoint_tests",
                ["HealthCare360:DataDirectory"] = dataDirectory,
            });
            // As Program.cs does; the certificates themselves are only created when the server starts.
            var options = new PlatformOptions { DataDirectory = dataDirectory };
            var certificates = new LanCertificateManager(new DataDirectories(options), options, new EphemeralDataProtectionProvider(), NullLogger<LanCertificateManager>.Instance);
            builder.AddServerApplication(certificates);

            var app = builder.Build();
            app.UseServerApplication();
            return app;
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private enum Access
    {
        Undeclared,
        Anonymous,
        AnySignedInUser,
        Permission,
    }

    private sealed record EndpointAccess(string Key, Access Access)
    {
        public static IEnumerable<EndpointAccess> Of(RouteEndpoint endpoint)
        {
            var metadata = endpoint.Metadata;
            var authorize = metadata.GetOrderedMetadata<IAuthorizeData>();
            var policies = metadata.GetOrderedMetadata<AuthorizationPolicy>();
            var access = metadata.GetMetadata<IAllowAnonymous>() is not null ? Access.Anonymous
                : authorize.Any(a => a.Policy?.StartsWith(PermissionPolicyProvider.Prefix, StringComparison.Ordinal) == true)
                  || policies.Any(p => p.Requirements.OfType<PermissionRequirement>().Any()) ? Access.Permission
                : authorize.Count > 0 || policies.Count > 0 ? Access.AnySignedInUser
                : Access.Undeclared;

            var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            var methods = metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            return methods is { Count: > 0 }
                ? methods.Select(method => new EndpointAccess($"{method} {route}", access))
                : [new EndpointAccess($"ANY {route}", access)];
        }
    }
}
