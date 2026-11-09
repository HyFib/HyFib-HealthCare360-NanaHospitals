using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>
/// Rate-limit policies for anonymous endpoints: <c>.AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public)</c>.
/// Every request, signed in or not, also counts against the global per-user (or per-address) limiter.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Links, QR codes, webhooks and workstation enrolment: 60 requests a minute per client address by default.</summary>
    public const string Public = "public";

    /// <summary>
    /// Calls from the hospital's partners, which sign or otherwise prove each call (SMS and WhatsApp delivery receipts,
    /// the payment gateway, the ABDM gateway and the providers that push records to it): 1,200 requests a minute per
    /// client address by default, outside the per-address limit on every request, so a campaign's receipts are taken and
    /// raising this leaves the <see cref="Public"/> budget of QR codes and links as it is.
    /// </summary>
    public const string Partner = "partner";

    /// <summary>
    /// Machines that present their own credential (home RPM gateways with <c>X-Device-Key</c>, biometric terminals with
    /// their key or serial number): by default 120 requests a minute per credential, so many homes behind one
    /// mobile-network address do not share a budget. Requests without a credential fall back to the client address.
    /// </summary>
    public const string Device = "device";

    /// <summary>
    /// HC360 Desktop's self-update (SRS DED-18): by default 30 requests in 15 minutes per device credential
    /// (<c>X-Device-Key</c>), enough to check for a package and download it in a few pieces, not to pull the package over
    /// and over.
    /// </summary>
    public const string DesktopUpdate = "desktop-update";

    private const string DeviceKeyHeader = "X-Device-Key";
    private const string DeviceSerialHeader = "X-Device-Serial";

    /// <summary>
    /// The server's rate limiter: sign-in and one-time codes (also counted across every node of several,
    /// <see cref="ClusterRateLimits"/>), the public and device policies above, and a global limit per user (or address)
    /// on every request, each as <see cref="RateLimitOptions"/> sets it. A single Server Node keeps these counts in its own
    /// memory. Where a shared store is registered (<see cref="ISharedRateLimitStore"/>, on Redis when there are several
    /// servers), every policy but sign-in and one-time codes counts there instead, so a client's budget is the hospital's
    /// whichever server answers, and on each server alone while the store cannot be reached (<see cref="SharedRateLimitState"/>).
    /// </summary>
    public static IServiceCollection AddHospitalRateLimiter(this IServiceCollection services)
    {
        services.TryAddSingleton<ClientAddresses>();
        services.TryAddSingleton<SharedRateLimitState>();
        services.AddScoped<IHealthWarningSource, SharedRateLimitWarnings>();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) => AntiAutomationEndpoints.OnRateLimitedAsync(context.HttpContext);
        });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<PlatformOptions>, IServiceProvider>((limiter, platform, provider) =>
        {
            var limits = platform.Value.RateLimits;
            foreach (var address in new[] { ClusterRateLimits.SignIn, ClusterRateLimits.OneTimeCode })
            {
                var limit = address.Of(limits);
                limiter.AddPolicy(address.Policy, http => RateLimitPartition.GetFixedWindowLimiter(ClientKey(http), _ => FixedWindow(limit)));
            }

            var shared = SharedRateLimits.From(provider);
            limiter.AddPolicy(Public, http => Window(shared, http, Public, ClientKey(http), limits.Public));
            limiter.AddPolicy(Partner, http => Window(shared, http, Partner, ClientKey(http), limits.Partner));
            limiter.AddPolicy(Device, http => Bucket(shared, http, Device, DevicePartition(http, ClientKey(http)), limits.Device));
            limiter.AddPolicy(DesktopUpdate, http => Window(shared, http, DesktopUpdate, DevicePartition(http, ClientKey(http)), limits.DesktopUpdate));
            // A partner's calls count against their own budget alone: the per-address burst would refuse a campaign's receipts.
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http => IsPartnerCall(http)
                ? RateLimitPartition.GetNoLimiter(Partner)
                : Bucket(shared, http, PerUserPolicy, http.User.FindFirst(HcClaims.UserId)?.Value ?? ClientKey(http), limits.PerUser));
        });
        return services;
    }

    /// <summary>The name the global limit per user (or address) is counted under in the shared store.</summary>
    internal const string PerUserPolicy = "user";

    /// <summary>A fixed window on this server, or a sliding window in the shared store when there is one.</summary>
    private static RateLimitPartition<string> Window(SharedRateLimits? shared, HttpContext http, string policy, string partition, FixedWindowLimit limit) =>
        shared is null
            ? RateLimitPartition.GetFixedWindowLimiter(partition, _ => FixedWindow(limit))
            : shared.Partition(http, policy, partition, SharedLimit.From(limit), () => new FixedWindowRateLimiter(Manual(FixedWindow(limit))));

    /// <summary>A token bucket on this server, or in the shared store when there is one.</summary>
    private static RateLimitPartition<string> Bucket(SharedRateLimits? shared, HttpContext http, string policy, string partition, TokenBucketLimit limit) =>
        shared is null
            ? RateLimitPartition.GetTokenBucketLimiter(partition, _ => TokenBucket(limit))
            : shared.Partition(http, policy, partition, SharedLimit.From(limit), () => new TokenBucketRateLimiter(Manual(TokenBucket(limit))));

    // Refilled by the partitioned limiter's one timer, as the partitions RateLimitPartition makes are, not a timer each.
    private static FixedWindowRateLimiterOptions Manual(FixedWindowRateLimiterOptions options)
    {
        options.AutoReplenishment = false;
        return options;
    }

    private static TokenBucketRateLimiterOptions Manual(TokenBucketRateLimiterOptions options)
    {
        options.AutoReplenishment = false;
        return options;
    }

    public static FixedWindowRateLimiterOptions FixedWindow(FixedWindowLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return new FixedWindowRateLimiterOptions { PermitLimit = limit.PermitLimit, Window = limit.Window, QueueLimit = 0 };
    }

    public static TokenBucketRateLimiterOptions TokenBucket(TokenBucketLimit limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return new TokenBucketRateLimiterOptions
        {
            TokenLimit = limit.TokenLimit,
            TokensPerPeriod = limit.TokensPerPeriod,
            ReplenishmentPeriod = limit.ReplenishmentPeriod,
            QueueLimit = 0,
        };
    }

    /// <summary>
    /// The address a per-address limit counts the client by: the TCP peer's, or behind a reverse proxy named in the settings
    /// the client's the proxy forwarded (<see cref="ReverseProxy"/>, SCAL-01), with an IPv6 address from outside the
    /// hospital taken by its /64 (<see cref="ClientAddresses"/>).
    /// </summary>
    public static string ClientKey(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.RequestServices.GetRequiredService<ClientAddresses>().Key(http.Connection.RemoteIpAddress);
    }

    /// <summary>Whether the request is for an endpoint on the <see cref="Partner"/> policy.</summary>
    internal static bool IsPartnerCall(HttpContext http) =>
        http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == Partner;

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
