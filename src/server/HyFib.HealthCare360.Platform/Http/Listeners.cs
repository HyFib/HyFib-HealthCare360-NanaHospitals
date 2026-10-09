using System.Net;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>The server's addresses, each an origin of its own (ASVS V3.5.4).</summary>
public enum Listener
{
    /// <summary>The staff app, HC360 Desktop and the integrations (<see cref="PlatformOptions.HttpsPort"/>).</summary>
    Staff,

    /// <summary>The patient portal (<see cref="PlatformOptions.PortalHttpsPort"/>).</summary>
    Portal,

    /// <summary>The interactive API reference, on the server machine only (<see cref="PlatformOptions.ApiReferencePort"/>).</summary>
    ApiReference,
}

/// <summary>What <see cref="ListenerPaths.Decide"/> does with a request.</summary>
public enum ListenerRoute
{
    Serve,
    NotFound,

    /// <summary>To the same page on the patient portal's address.</summary>
    ToPortal,

    /// <summary>To the portal's home page, on the same address.</summary>
    ToPortalHome,

    /// <summary>To the same page on the API reference's address.</summary>
    ToApiReference,

    /// <summary>To the API reference's home page, on the same address.</summary>
    ToApiReferenceHome,
}

/// <summary>
/// What each of the server's addresses serves (ASVS V3.5.4). The patient portal runs third-party script, the payment
/// checkout, and the API reference is a third-party app. On the staff app's origin either could use a staff member's
/// session in the same browser, so each has an origin of its own, and each origin serves only its own app:
/// <list type="bullet">
/// <item>The portal's address serves the portal's pages and API and the endpoints it shares with the staff app; nothing
/// else, so no staff sign-in or staff endpoint answers there.</item>
/// <item>The staff app's address sends portal pages to the portal's address, so links made before the portal moved keep
/// working, and does not serve the portal's own API.</item>
/// <item>The API reference's address, on the server machine only, serves the reference and the API it describes.</item>
/// </list>
/// Browsers send a host's cookies to every port of it, so the server keeps the sessions apart, not the browser: the
/// portal's address renews only portal sessions (<see cref="Identity.AuthService"/>), and the API reference's address
/// takes no cookies and sets none.
/// </summary>
public static class ListenerPaths
{
    /// <summary>
    /// Shared by the staff app and the portal: renewing and ending a session (on each address its own app's), the public
    /// endpoints of patient links, report and prescription checks from printed QR codes, the server's name and switches,
    /// video consultations, health probes, and the webhooks outside services call, whichever address a hospital opens to
    /// them.
    /// </summary>
    private static readonly string[] Shared =
    [
        "/api/v1/auth/refresh",
        "/api/v1/auth/logout",
        "/api/v1/public",
        "/api/v1/lab/reports/verify",
        "/api/v1/radiology/reports/verify",
        "/api/v1/cardiology/reports/verify",
        "/api/v1/system/info",
        "/api/v1/billing/webhooks",
        "/api/v1/integrations/webhooks",
        "/hubs/telehealth",
        "/health",
    ];

    /// <summary>The portal's own: its pages, its API, and its sign-in with a one-time code.</summary>
    private static readonly string[] PortalOnly = ["/portal", "/api/v1/portal", "/api/v1/auth/otp"];

    /// <summary>The API reference and the OpenAPI description it shows.</summary>
    private static readonly string[] ApiReference = ["/docs", "/openapi"];

    public static ListenerRoute Decide(Listener listener, PathString path) => listener switch
    {
        Listener.Portal => IsRoot(path) ? ListenerRoute.ToPortalHome
            : Under(path, PortalOnly) || Under(path, Shared) ? ListenerRoute.Serve
            : ListenerRoute.NotFound,
        Listener.ApiReference => IsRoot(path) ? ListenerRoute.ToApiReferenceHome
            : Under(path, ApiReference) || path.StartsWithSegments("/api") ? ListenerRoute.Serve
            : ListenerRoute.NotFound,
        _ => path.StartsWithSegments("/portal") ? ListenerRoute.ToPortal
            : Under(path, PortalOnly) ? ListenerRoute.NotFound
            : Under(path, ApiReference) ? ListenerRoute.ToApiReference
            : ListenerRoute.Serve,
    };

    private static bool IsRoot(PathString path) => !path.HasValue || path == "/";

    // Whole segments only: /api/v1/radiology/reports/{id}/verify, a radiologist signing a report, is not a report check.
    private static bool Under(PathString path, string[] prefixes) => prefixes.Any(prefix => path.StartsWithSegments(prefix));
}

/// <summary>Which of the server's addresses a request came to, recorded by <see cref="ListenerGateMiddleware"/>.</summary>
public static class Listeners
{
    /// <summary>The address a request came to; the staff app's when nothing recorded one.</summary>
    public static Listener Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Features.Get<ListenerFeature>()?.Listener ?? Listener.Staff;
    }

    /// <summary>
    /// The address of a connection, by the port it arrived on: the port is the server's own, while the Host header is
    /// whatever the client sent.
    /// </summary>
    public static Listener ForPort(int localPort, PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return localPort == options.PortalHttpsPort ? Listener.Portal
            : localPort == options.ApiReferencePort ? Listener.ApiReference
            : Listener.Staff;
    }

    /// <summary>The API reference is served in development, and elsewhere only when IT turns it on.</summary>
    public static bool ApiReferenceOn(PlatformOptions options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment() || options.EnableApiReference;
    }

    /// <summary>Why the configured ports cannot be used together, or null when they can.</summary>
    public static string? PortProblem(PlatformOptions options, bool apiReferenceOn)
    {
        ArgumentNullException.ThrowIfNull(options);
        var ports = new List<(string Name, int Port)>
        {
            (nameof(PlatformOptions.HttpsPort), options.HttpsPort),
            (nameof(PlatformOptions.PortalHttpsPort), options.PortalHttpsPort),
        };
        if (apiReferenceOn)
        {
            ports.Add((nameof(PlatformOptions.ApiReferencePort), options.ApiReferencePort));
        }

        if (options.HttpRedirectPort > 0)
        {
            ports.Add((nameof(PlatformOptions.HttpRedirectPort), options.HttpRedirectPort));
        }

        if (ports.FirstOrDefault(p => p.Port is < 1 or > 65535) is { Name: not null } invalid)
        {
            return $"HealthCare360:{invalid.Name} must be a port between 1 and 65535, not {invalid.Port}.";
        }

        return ports.GroupBy(p => p.Port).FirstOrDefault(g => g.Count() > 1) is { } shared
            ? $"HealthCare360:{string.Join(" and HealthCare360:", shared.Select(p => p.Name))} are {(shared.Count() == 2 ? "both" : "all")} " +
              $"{shared.Key}: each needs a port of its own."
            : null;
    }
}

/// <summary>The address a request came to (<see cref="Listeners.Of"/>).</summary>
public sealed record ListenerFeature(Listener Listener);

/// <summary>
/// Records which address a request came to, and applies <see cref="ListenerPaths"/>: what the address does not serve is a
/// 404, and portal pages asked for on the staff app's address are sent to the portal's. Runs before static files, so the
/// app shells and their assets are kept apart too.
/// </summary>
public sealed class ListenerGateMiddleware(RequestDelegate next, IOptions<PlatformOptions> options, IHostEnvironment environment)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = options.Value;
        var listener = Listeners.ForPort(context.Connection.LocalPort, settings);
        context.Features.Set(new ListenerFeature(listener));

        if (listener == Listener.ApiReference)
        {
            // The reference runs third-party script: here it gets no session cookie, and can leave none behind.
            context.Request.Headers.Remove(HeaderNames.Cookie);
            context.Response.OnStarting(static state =>
            {
                ((HttpResponse)state).Headers.Remove(HeaderNames.SetCookie);
                return Task.CompletedTask;
            }, context.Response);
        }

        var request = context.Request;
        switch (ListenerPaths.Decide(listener, request.Path))
        {
            case ListenerRoute.Serve:
                return next(context);
            case ListenerRoute.ToPortal when request.Host.HasValue:
                return Redirect(context, UriHelper.BuildAbsolute("https", new HostString(request.Host.Host, settings.PortalHttpsPort), PathString.Empty, request.Path, request.QueryString));
            case ListenerRoute.ToPortalHome:
                return Redirect(context, "/portal/");
            case ListenerRoute.ToApiReference when request.Host.HasValue && Listeners.ApiReferenceOn(settings, environment)
                                                   && IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None):
                return Redirect(context, UriHelper.BuildAbsolute("https", new HostString(request.Host.Host, settings.ApiReferencePort), PathString.Empty, request.Path, request.QueryString));
            case ListenerRoute.ToApiReferenceHome:
                return Redirect(context, "/docs");
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
        }
    }

    // Temporary, so a browser does not remember it if IT later moves the portal to another port.
    private static Task Redirect(HttpContext context, string location)
    {
        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = location;
        return Task.CompletedTask;
    }
}

public static class ListenerGateExtensions
{
    /// <summary>
    /// Keeps the staff app, the patient portal and the API reference on addresses of their own (<see cref="ListenerPaths"/>).
    /// Call after <c>UseHospitalSecurityHeaders</c>, so its redirects and refusals carry them, and before static files.
    /// </summary>
    public static IApplicationBuilder UseListenerGate(this IApplicationBuilder app) => app.UseMiddleware<ListenerGateMiddleware>();
}
