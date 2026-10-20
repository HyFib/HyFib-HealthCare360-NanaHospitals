using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>
/// Rate-limit policies for anonymous endpoints: <c>.AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public)</c>.
/// Every request, signed in or not, also counts against the global per-user (or per-address) limiter.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Links, QR codes, webhooks and workstation enrolment: 60 requests a minute per client address.</summary>
    public const string Public = "public";

    /// <summary>
    /// Machines that present their own credential (home RPM gateways with <c>X-Device-Key</c>, biometric terminals with
    /// their key or serial number): 120 requests a minute per credential, so many homes behind one mobile-network
    /// address do not share a budget. Requests without a credential fall back to the client address.
    /// </summary>
    public const string Device = "device";

    /// <summary>
    /// HC360 Desktop's self-update (SRS DED-18): 30 requests in 15 minutes per device credential (<c>X-Device-Key</c>),
    /// enough to check for a package and download it in a few pieces, not to pull the package over and over.
    /// </summary>
    public const string DesktopUpdate = "desktop-update";

    private const string DeviceKeyHeader = "X-Device-Key";
    private const string DeviceSerialHeader = "X-Device-Serial";

    /// <summary>
    /// The server's rate limiter: sign-in and one-time codes (also counted across every node of several,
    /// <see cref="ClusterRateLimits"/>), the public and device policies above, and a global limit per user (or address)
    /// on every request. Each server keeps these counts in its own memory.
    /// </summary>
    public static IServiceCollection AddHospitalRateLimiter(this IServiceCollection services) =>
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                AntiAutomationEndpoints.OnRateLimited(context.HttpContext);
                return ValueTask.CompletedTask;
            };
            foreach (var address in new[] { ClusterRateLimits.SignIn, ClusterRateLimits.OneTimeCode })
            {
                limiter.AddPolicy(address.Policy, http => RateLimitPartition.GetFixedWindowLimiter(
                    ClientKey(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = address.PermitLimit, Window = address.Window, QueueLimit = 0 }));
            }

            limiter.AddPolicy(Public, http => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(http), _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            limiter.AddPolicy(Device, http => RateLimitPartition.GetTokenBucketLimiter(
                DevicePartition(http, ClientKey(http)),
                _ => new TokenBucketRateLimiterOptions { TokenLimit = 120, TokensPerPeriod = 60, ReplenishmentPeriod = TimeSpan.FromSeconds(30), QueueLimit = 0 }));
            limiter.AddPolicy(DesktopUpdate, http => RateLimitPartition.GetFixedWindowLimiter(
                DevicePartition(http, ClientKey(http)),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => RateLimitPartition.GetTokenBucketLimiter(
                http.User.FindFirst(HcClaims.UserId)?.Value ?? ClientKey(http),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 400,
                    TokensPerPeriod = 200,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                }));
        });

    /// <summary>
    /// The client's address a per-address limit counts by: the TCP peer's, or behind a reverse proxy named in the settings
    /// the client's the proxy forwarded (<see cref="ReverseProxy"/>, SCAL-01).
    /// </summary>
    internal static string ClientKey(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>Partition for <see cref="Device"/>: a hash of the presented credential, never the raw value.</summary>
    internal static string DevicePartition(HttpContext http, string clientKey)
    {
        var credential = http.Request.Headers[DeviceKeyHeader].ToString();
        if (credential.Length == 0)
        {
            credential = http.Request.Headers[DeviceSerialHeader].ToString();
        }

        if (credential.Length == 0)
        {
            // eSSL/ZKTeco ADMS terminals identify themselves with ?SN=.
            credential = http.Request.Query["SN"].ToString();
        }

        return credential.Length is 0 or > 256
            ? clientKey
            : "device:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)), 0, 12);
    }
}
