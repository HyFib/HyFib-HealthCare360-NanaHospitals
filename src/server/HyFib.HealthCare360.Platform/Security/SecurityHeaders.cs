using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// Adds defensive HTTP headers. The CSP allows only same-origin scripts; inline styles are permitted
/// because Fluent UI (Griffel) injects runtime styles. Camera/microphone are limited to this origin
/// (telehealth, voice assistant).
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string AppCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self' data:; connect-src 'self' wss: https:; media-src 'self' blob:; worker-src 'self' blob:; " +
        "frame-src 'self' blob:; frame-ancestors 'none'; object-src 'none'; base-uri 'self'; form-action 'self'";

    // The interactive API reference (admin-only, disabled by default) loads its assets from a CDN.
    private const string DocsCsp =
        "default-src 'self'; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
        "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net; img-src 'self' data: https:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            var headers = ctx.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(self), microphone=(self), geolocation=(), payment=(self), usb=(), serial=(), bluetooth=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                headers.ContentSecurityPolicy = ctx.Request.Path.StartsWithSegments("/docs") ? DocsCsp : AppCsp;
            }

            if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/fhir"))
            {
                headers.CacheControl = "no-store";
                headers.Pragma = "no-cache";
            }

            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
