using HyFib.HealthCare360.Platform.Http;
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

        group.MapPost("/otp/request", async (OtpRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.RequestOtpAsync(request, ct)).ToOk())
            .AllowAnonymous()
            .RequireRateLimiting(OtpRateLimit);

        group.MapPost("/otp/verify", async (OtpVerifyRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.VerifyOtpAsync(request, http, ct)).ToOk())
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimit);

        group.MapPost("/refresh", async (AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.RefreshAsync(http, ct)).ToOk())
            .AllowAnonymous()
            .RequireRateLimiting(AuthRateLimit);

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

        group.MapPost("/unlock", async (UnlockRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.UnlockAsync(request, http, ct)).ToNoContent())
            .RequireAuthorization()
            .RequireRateLimiting(AuthRateLimit);

        group.MapPost("/mfa/setup", async (AuthService auth, CancellationToken ct) => (await auth.BeginMfaSetupAsync(ct)).ToOk())
            .RequireAuthorization();

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

        group.MapDelete("/sessions/{id:guid}", async (Guid id, AuthService auth, CancellationToken ct) =>
                (await auth.RevokeSessionAsync(id, ct)).ToNoContent())
            .RequireAuthorization();
    }
}
