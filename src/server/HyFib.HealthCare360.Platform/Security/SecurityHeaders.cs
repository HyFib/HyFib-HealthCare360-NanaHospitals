using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Security;

/// <summary>
/// Adds defensive HTTP headers. The CSP allows only same-origin scripts; inline styles are permitted
/// because Fluent UI (Griffel) injects runtime styles. Pages talk only to the server ('self' also covers its
/// WebSockets), so script injected into a page has nowhere to send what it reads. Camera/microphone are limited to this
/// origin (telehealth, voice assistant). Patient-portal pages may also open Razorpay Standard Checkout, the only
/// third-party script the server allows.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string AppCsp =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; " +
        "font-src 'self' data:; connect-src 'self'; media-src 'self' blob:; worker-src 'self' blob:; " +
        "frame-src 'self' blob:; frame-ancestors 'none'; object-src 'none'; base-uri 'none'; form-action 'self'";

    // Razorpay Standard Checkout: its script, the payment frame it opens, and the two hosts the script itself calls
    // (payments, and its own error reporting).
    private const string PortalCsp =
        "default-src 'self'; script-src 'self' https://checkout.razorpay.com https://cdn.razorpay.com; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob: https://cdn.razorpay.com; font-src 'self' data:; " +
        "connect-src 'self' https://api.razorpay.com https://lumberjack.razorpay.com; media-src 'self' blob:; " +
        "worker-src 'self' blob:; frame-src 'self' blob: https://checkout.razorpay.com https://api.razorpay.com; frame-ancestors 'none'; " +
        "object-src 'none'; base-uri 'none'; form-action 'self'";

    // The interactive API reference (admin-only, disabled by default) loads its assets from a CDN.
    private const string DocsCsp =
        "default-src 'self'; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
        "font-src 'self' data: https://fonts.gstatic.com https://cdn.jsdelivr.net; img-src 'self' data: https:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; " +
        "base-uri 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            var headers = ctx.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            var portal = ctx.Request.Path.StartsWithSegments("/portal");
            headers["Permissions-Policy"] = portal
                ? "camera=(self), microphone=(self), geolocation=(), payment=(self \"https://api.razorpay.com\"), usb=(), serial=(), bluetooth=()"
                : "camera=(self), microphone=(self), geolocation=(), payment=(self), usb=(), serial=(), bluetooth=()";

            // Net banking and some UPI flows open the bank's page in a pop-up that reports back to the checkout.
            headers["Cross-Origin-Opener-Policy"] = portal ? "same-origin-allow-popups" : "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";

            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                headers.ContentSecurityPolicy = portal ? PortalCsp : ctx.Request.Path.StartsWithSegments("/docs") ? DocsCsp : AppCsp;
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
