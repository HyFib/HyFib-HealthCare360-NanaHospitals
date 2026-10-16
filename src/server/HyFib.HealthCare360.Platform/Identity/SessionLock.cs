using Microsoft.AspNetCore.Http;

namespace HyFib.HealthCare360.Platform.Identity;

/// <summary>
/// What a session with a locked screen may still do, and the answer to everything else (SRS UX-11; ASVS V7.3.1). The
/// lock is kept by the server, so hiding the lock screen in the browser opens nothing: every other request is refused
/// with 423 and <see cref="ErrorCode"/>, which the app takes as "show the lock screen".
/// </summary>
internal static class SessionLock
{
    public const string ErrorCode = "auth.session_locked";

    private const string RefusedKey = "hc360.session-locked";

    // Who is signed in (for the lock screen), unlocking (with the PIN or password, or with the device's Windows Hello or
    // fingerprint), locking again, and signing out.
    private static readonly string[] AllowedPaths =
    [
        "/api/v1/auth/me",
        "/api/v1/auth/unlock",
        "/api/v1/auth/unlock/webauthn/options",
        "/api/v1/auth/unlock/webauthn",
        "/api/v1/auth/lock",
        "/api/v1/auth/logout",
    ];

    public static bool Allows(HttpRequest request) =>
        AllowedPaths.Any(path => request.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Notes that the request was refused for the lock, for <see cref="TryRefuseAsync"/>.</summary>
    public static void MarkRefused(HttpContext http) => http.Items[RefusedKey] = true;

    /// <summary>Answers 423 when the request was refused for the lock; false for any other sign-in failure.</summary>
    public static async Task<bool> TryRefuseAsync(HttpContext http)
    {
        if (!http.Items.ContainsKey(RefusedKey))
        {
            return false;
        }

        await TypedResults.Problem(
            detail: "The screen is locked. Unlock it with your PIN or password to continue.",
            statusCode: StatusCodes.Status423Locked,
            title: "Screen locked",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCode }).ExecuteAsync(http);
        return true;
    }
}
