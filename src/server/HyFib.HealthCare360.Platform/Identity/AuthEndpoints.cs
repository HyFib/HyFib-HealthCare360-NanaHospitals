using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Identity;

internal static class AuthEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/login", async (LoginRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.LoginAsync(request, http, ct)).ToOk())
            .AllowAnonymous()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<LoginRequest>();

        group.MapPost("/mfa", async (MfaVerifyRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.VerifyMfaAsync(request, http, ct)).ToOk())
            .AllowAnonymous()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<MfaVerifyRequest>();

        // The patient portal's sign-in, which also registers a new number: off while the portal is switched off. Both
        // steps ask for a proof of work (SRS SEC-06); failures for a number raise the work asked for it from any address.
        // A number that is not a mobile number is answered first, so it neither spends the proof nor counts as a failure.
        group.MapPost("/otp/request", async (OtpRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.RequestOtpAsync(request, ct)).ToOk())
            .AllowAnonymous()
            .RequireAddressRateLimit(ClusterRateLimits.OneTimeCode)
            .RequireFeature(FeatureKeys.PatientPortal)
            .WithValidation<OtpRequest>()
            .RequireProofOfWork(AntiAutomationActions.Otp, context => context.Arguments.OfType<OtpRequest>().FirstOrDefault()?.Phone);

        // Giving the code names its challenge, not the number: the number is looked up only when the attempt fails (a wrong
        // or expired code, a refused proof), so that it counts against the number as well as the address.
        group.MapPost("/otp/verify", async (OtpVerifyRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.VerifyOtpAsync(request, http, ct)).ToOk())
            .AllowAnonymous()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .RequireFeature(FeatureKeys.PatientPortal)
            .WithValidation<OtpVerifyRequest>()
            .RequireProofOfWork(AntiAutomationActions.Otp, subjectOnFailure: PhoneOfChallengeAsync);

        // A portal session can only be refreshed as the portal (its cookie is read by the client header), so the portal's
        // own refreshes stop while it is switched off and staff sessions are not affected.
        group.MapPost("/refresh", async (AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.RefreshAsync(http, ct)).ToOk())
            .AllowAnonymous()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .RequireFeature(FeatureKeys.PatientPortal, AuthService.IsPortalRequest);

        group.MapPost("/logout", async (AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.LogoutAsync(http, ct)).ToNoContent())
            .AllowAnonymous();

        group.MapGet("/me", async (AuthService auth, CancellationToken ct) => (await auth.GetMeAsync(ct)).ToOk())
            .RequireAuthorization();

        group.MapPost("/switch-facility", async (SwitchFacilityRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.SwitchFacilityAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .WithValidation<SwitchFacilityRequest>();

        group.MapPost("/change-password", async (ChangePasswordRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.ChangePasswordAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<ChangePasswordRequest>();

        group.MapPost("/pin", async (SetPinRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.SetPinAsync(request, ct)).ToNoContent())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<SetPinRequest>();

        // Locking and unlocking are among the few things a locked session may do (SessionLock).
        group.MapPost("/lock", async (AuthService auth, CancellationToken ct) => (await auth.LockAsync(ct)).ToNoContent())
            .RequireAuthorization();

        group.MapPost("/unlock", async (UnlockRequest request, AuthService auth, HttpContext http, CancellationToken ct) =>
                (await auth.UnlockAsync(request, http, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<UnlockRequest>();

        // Quick unlock with the device's Windows Hello, fingerprint or face (WebAuthn), allowed while locked too. It only
        // resumes the caller's own session (WebAuthnUnlockService).
        group.MapPost("/unlock/webauthn/options", async (WebAuthnUnlockService webAuthn, HttpContext http, CancellationToken ct) =>
                (await webAuthn.BeginUnlockAsync(http, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn);

        group.MapPost("/unlock/webauthn", async (WebAuthnUnlockRequest request, WebAuthnUnlockService webAuthn, HttpContext http, CancellationToken ct) =>
                (await webAuthn.UnlockAsync(request, http, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<WebAuthnUnlockRequest>();

        // The devices set up for it: listed, added after the password is given again, and removed by their owner.
        group.MapGet("/webauthn/credentials", async (WebAuthnUnlockService webAuthn, ICurrentUser user, HttpContext http, CancellationToken ct) =>
                TypedResults.Ok(await webAuthn.ListAsync(user.UserId, http, ct)))
            .RequireAuthorization();

        group.MapPost("/webauthn/credentials/options", async (WebAuthnRegistrationRequest request, WebAuthnUnlockService webAuthn, HttpContext http, CancellationToken ct) =>
                (await webAuthn.BeginRegistrationAsync(request, http, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<WebAuthnRegistrationRequest>();

        group.MapPost("/webauthn/credentials", async (WebAuthnRegisterRequest request, WebAuthnUnlockService webAuthn, HttpContext http, CancellationToken ct) =>
                (await webAuthn.RegisterAsync(request, http, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<WebAuthnRegisterRequest>();

        group.MapDelete("/webauthn/credentials/{id:guid}", async (Guid id, WebAuthnUnlockService webAuthn, CancellationToken ct) =>
                (await webAuthn.RemoveAsync(id, ct)).ToNoContent())
            .RequireAuthorization();

        group.MapPost("/mfa/setup", async (MfaSetupRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.BeginMfaSetupAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<MfaSetupRequest>();

        group.MapPost("/mfa/enable", async (MfaEnableRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.EnableMfaAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<MfaEnableRequest>();

        group.MapPost("/mfa/disable", async (MfaDisableRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.DisableMfaAsync(request, ct)).ToNoContent())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<MfaDisableRequest>();

        group.MapGet("/sessions", async (AuthService auth, CancellationToken ct) => TypedResults.Ok(await auth.ListSessionsAsync(ct)))
            .RequireAuthorization();

        // Ending sessions asks for the password again (ASVS V7.5.2), so these share sign-in's limit on guesses.
        group.MapPost("/sessions/{id:guid}/end", async (Guid id, ReauthenticateRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.EndSessionAsync(id, request, ct)).ToNoContent())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<ReauthenticateRequest>();

        group.MapPost("/sessions/end-others", async (ReauthenticateRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.EndOtherSessionsAsync(request, ct)).ToOk())
            .RequireAuthorization()
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<ReauthenticateRequest>();

        // Signs out everybody but the administrator who asks: for a suspected break-in (ASVS V7.4.5).
        app.MapPost("/api/v1/admin/sessions/end-all", async (ReauthenticateRequest request, AuthService auth, CancellationToken ct) =>
                (await auth.EndAllSessionsAsync(request, ct)).ToOk())
            .WithTags("Administration")
            .RequirePermission(PlatformPermissions.SystemManage)
            .RequireAddressRateLimit(ClusterRateLimits.SignIn)
            .WithValidation<ReauthenticateRequest>();
    }

    private static Task<string?> PhoneOfChallengeAsync(EndpointFilterInvocationContext context, CancellationToken ct) =>
        context.Arguments.OfType<OtpVerifyRequest>().FirstOrDefault() is { } request
            ? context.HttpContext.RequestServices.GetRequiredService<AuthService>().OtpPhoneAsync(request.ChallengeId, ct)
            : Task.FromResult<string?>(null);
}
