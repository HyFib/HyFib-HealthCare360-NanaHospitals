using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

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
