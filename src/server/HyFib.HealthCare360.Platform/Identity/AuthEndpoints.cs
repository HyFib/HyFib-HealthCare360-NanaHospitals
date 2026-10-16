using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace HyFib.HealthCare360.Platform.Identity;

internal static class AuthEndpoints
{
    public const string AuthRateLimit = "auth";
    public const string OtpRateLimit = "otp";

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/login", async (LoginRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.LoginAsync(request, http, ct)).ToOk())
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimit);

        group.MapPost("/mfa", async (MfaVerifyRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.VerifyMfaAsync(request, http, ct)).ToOk())
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimit);

        // The patient portal's sign-in, which also registers a new number: off while the portal is switched off. Both
        // steps ask for a proof of work (SRS SEC-06); failures for a number raise the work asked for it from any address.
        group.MapPost("/otp/request", async (OtpRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.RequestOtpAsync(request, ct)).ToOk())
            .AllowAnonymous()
            .RequireRateLimiting(OtpRateLimit)
            .RequireFeature(FeatureKeys.PatientPortal)
            .RequireProofOfWork(AntiAutomationActions.Otp, context => context.Arguments.OfType<OtpRequest>().FirstOrDefault()?.Phone);

        group.MapPost("/otp/verify", async (OtpVerifyRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.VerifyOtpAsync(request, http, ct)).ToOk())
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimit)
            .RequireFeature(FeatureKeys.PatientPortal)
            .RequireProofOfWork(AntiAutomationActions.Otp);

        // A portal session can only be refreshed as the portal (its cookie is read by the client header), so the portal's
        // own refreshes stop while it is switched off and staff sessions are not affected.
        group.MapPost("/refresh", async (AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.RefreshAsync(http, ct)).ToOk())
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimit)
            .RequireFeature(FeatureKeys.PatientPortal, AuthService.IsPortalRequest);

        group.MapPost("/logout", async (AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.LogoutAsync(http, ct)).ToNoContent())
            .AllowAnonymous();

        group.MapGet("/me", async (AuthService auth, CancellationToken ct) => (await auth.GetMeAsync(ct)).ToOk())
            .RequireAuthorization();

        group.MapPost("/switch-facility", async (SwitchFacilityRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.SwitchFacilityAsync(request, ct)).ToOk())
            .RequireAuthorization();

        group.MapPost("/change-password", async (ChangePasswordRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.ChangePasswordAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        group.MapPost("/pin", async (SetPinRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.SetPinAsync(request, ct)).ToNoContent())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        // Locking and unlocking are among the few things a locked session may do (SessionLock).
        group.MapPost("/lock", async (AuthService auth, CancellationToken ct) => (await auth.LockAsync(ct)).ToNoContent())
            .RequireAuthorization();

        group.MapPost("/unlock", async (UnlockRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.UnlockAsync(request, http, ct)).ToOk())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        group.MapPost("/mfa/setup", async (MfaSetupRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.BeginMfaSetupAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        group.MapPost("/mfa/enable", async (MfaEnableRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.EnableMfaAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        group.MapPost("/mfa/disable", async (MfaDisableRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.DisableMfaAsync(request, ct)).ToNoContent())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        group.MapGet("/sessions", async (AuthService auth, CancellationToken ct) => TypedResults.Ok(await auth.ListSessionsAsync(ct)))
            .RequireAuthorization();

        // Ending sessions asks for the password again (ASVS V7.5.2), so these share sign-in's limit on guesses.
        group.MapPost("/sessions/{id:guid}/end", async (Guid id, ReauthenticateRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.EndSessionAsync(id, request, ct)).ToNoContent())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        group.MapPost("/sessions/end-others", async (ReauthenticateRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.EndOtherSessionsAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        // Signs out everybody but the administrator who asks: for a suspected break-in (ASVS V7.4.5).
        app.MapPost("/api/v1/admin/sessions/end-all", async (ReauthenticateRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.EndAllSessionsAsync(request, ct)).ToOk())
            .WithTags("Administration")
            .RequirePermission(PlatformPermissions.SystemManage)
            .RequireRateLimiting(AuthRateLimit);
    }
}
