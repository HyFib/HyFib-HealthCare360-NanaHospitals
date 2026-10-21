using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Platform.Http;

/// <summary>
/// A per-address limit that must hold for the hospital, not for each server: <paramref name="PermitLimit"/> requests per
/// client address in a fixed <paramref name="Window"/>.
/// </summary>
public sealed record AddressRateLimit(string Policy, int PermitLimit, TimeSpan Window);

/// <summary>
/// The per-address limits on sign-in and one-time codes (SEC-06), which ASP.NET Core's rate limiter keeps in each
/// server's memory. Behind a reverse proxy that spreads requests over several API nodes (SCAL-01), each node would allow
/// the whole budget, so a node of several (<see cref="DataDirectories.IsShared"/>) also counts every such request in
/// <see cref="IClusterStore"/>, where the budget is the hospital's: one more statement on the database per sign-in. A
/// single Server Node counts in memory only, as before. The per-account lockout and the per-number limit on one-time
/// codes are in the database already, whichever node answers.
/// </summary>
public static class ClusterRateLimits
{
    /// <summary>Sign-in, a second factor, a one-time code, a token refresh, unlocking: 10 a minute per address.</summary>
    public static readonly AddressRateLimit SignIn = new("auth", 10, TimeSpan.FromMinutes(1));

    /// <summary>Asking for a one-time code: 5 in 15 minutes per address.</summary>
    public static readonly AddressRateLimit OneTimeCode = new("otp", 5, TimeSpan.FromMinutes(15));

    /// <summary>
    /// Limits the endpoint per address with ASP.NET Core's rate limiter (<paramref name="limit"/>'s policy) and, on a node
    /// of several, across every node.
    /// </summary>
    public static TBuilder RequireAddressRateLimit<TBuilder>(this TBuilder builder, AddressRateLimit limit)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(limit);
        builder.RequireRateLimiting(limit.Policy);
        builder.AddEndpointFilter(new ClusterRateLimitFilter(limit));
        return builder;
    }

    /// <summary>
    /// The key a request counts under: the policy and an HMAC of the client's address (as the reverse proxy forwarded it,
    /// <see cref="ReverseProxy"/>) under a key derived from the hospital's data key, so the store holds no address. A
    /// plain hash would not do: every IPv4 address can be hashed in minutes, which would give the addresses back to
    /// anyone who reads the table or a backup of it.
    /// </summary>
    internal static string Key(AddressRateLimit limit, HttpContext http)
    {
        var address = RateLimitPolicies.ClientKey(http);
        var key = http.RequestServices.GetRequiredService<IFieldProtector>().DeriveKey(KeyPurpose);
        return $"rate:{limit.Policy}:" + Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(address)), 0, 16);
    }

    private const string KeyPurpose = "cluster-rate-limit";
}

/// <summary>Refuses a request over the hospital-wide budget with 429 and <c>Retry-After</c>, as the in-memory limiter does.</summary>
internal sealed class ClusterRateLimitFilter(AddressRateLimit limit) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!http.RequestServices.GetRequiredService<DataDirectories>().IsShared)
        {
            return await next(context);
        }

        var store = http.RequestServices.GetRequiredService<IClusterStore>();
        var count = await store.IncrementAsync(ClusterRateLimits.Key(limit, http), limit.Window, http.RequestAborted);
        if (count.Count <= limit.PermitLimit)
        {
            return await next(context);
        }

        // As the in-memory limiter's refusals do: on an endpoint that asks for a proof of work, the address is asked for more.
        await AntiAutomationEndpoints.OnRateLimitedAsync(http);
        var wait = count.WindowEndsAt - http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
}
