using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace HyFib.HealthCare360.Platform.Updates;

/// <summary>
/// The HC360 Desktop package the server offers (<see cref="DesktopPackageStore"/>): its version, manifest and signature,
/// which the workstation verifies itself (base64 of the files' bytes, as they were signed).
/// </summary>
public sealed record DesktopUpdateOfferDto(string Version, DateTimeOffset ReleasedAt, long Size, string Manifest, string Signature);

/// <summary>For Administration › Devices: the version workstations are offered, or why none is.</summary>
public sealed record DesktopUpdateStateDto(string? OfferedVersion, DateTimeOffset? ReleasedAt, string? Problem);

/// <summary>
/// HC360 Desktop's self-update from the Server Node (SRS DED-18), so workstations need no internet access. Only an
/// enrolled, unrevoked HC360 Desktop may ask: it presents its device id (<c>X-Device-Id</c>) and secret
/// (<c>X-Device-Key</c>), as the lab agent does, and anyone else gets 401 and nothing more.
/// <list type="bullet">
/// <item><c>GET /api/v1/devices/desktop-update</c>: the package offered (204 when none), with its signed manifest.</item>
/// <item><c>GET /api/v1/devices/desktop-update/package</c>: the package itself, with ranges so a dropped download resumes,
/// and its SHA-256 as the entity tag.</item>
/// </list>
/// Both are limited per device (<see cref="RateLimitPolicies.DesktopUpdate"/>). The workstation checks the signature, the
/// size and the SHA-256 itself, and the updater again as SYSTEM, so the server is trusted for none of it.
/// </summary>
internal static class DesktopUpdateEndpoints
{
    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceKeyHeader = "X-Device-Key";

    private static readonly string NoDevice = SecretTokens.Hash("no such device");

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/devices/desktop-update", GetOffer)
            .WithTags("Devices")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.DesktopUpdate);
        app.MapGet("/api/v1/devices/desktop-update/package", GetPackage)
            .WithTags("Devices")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.DesktopUpdate);

        app.MapGet("/api/v1/admin/devices/desktop-update", GetState)
            .WithTags("Administration")
            .RequirePermission(PlatformPermissions.DevicesManage)
            .AllowedForFirstDesktopEnrolment();
    }

    /// <summary>
    /// The enrolled HC360 Desktop presenting its credential, or null. The secret is compared in constant time, also when
    /// the device does not exist; lab agents and revoked devices are refused like a wrong secret.
    /// </summary>
    internal static async Task<Device?> AuthenticateAsync(HttpContext http, HospitalDbContext db, CancellationToken cancellationToken)
    {
        var key = http.Request.Headers[DeviceKeyHeader].ToString();
        if (!Guid.TryParse(http.Request.Headers[DeviceIdHeader].ToString(), out var deviceId) || key.Length is 0 or > 256)
        {
            return null;
        }

        var device = await db.Set<Device>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        var matches = SecretTokens.FixedTimeEquals(device?.SecretHash ?? NoDevice, SecretTokens.Hash(key));
        return device is not null && matches && !device.IsRevoked && device.Kind.IsDesktop() ? device : null;
    }

    private static async Task<IResult> GetOffer(HttpContext http, HospitalDbContext db, DesktopPackageStore store, CancellationToken ct)
    {
        if (await AuthenticateAsync(http, db, ct) is null)
        {
            return NotRecognised();
        }

        var offer = await store.GetOfferAsync(SystemHealthService.ProductVersion, ct);
        return offer is null
            ? TypedResults.NoContent()
            : TypedResults.Ok(new DesktopUpdateOfferDto(offer.Manifest.Version, offer.Manifest.ReleasedAt, offer.Manifest.Size,
                Convert.ToBase64String(offer.ManifestBytes), Convert.ToBase64String(offer.SignatureBytes)));
    }

    private static async Task<IResult> GetPackage(
        HttpContext http, HospitalDbContext db, DesktopPackageStore store, ILoggerFactory loggers, CancellationToken ct)
    {
        if (await AuthenticateAsync(http, db, ct) is not { } device)
        {
            return NotRecognised();
        }

        var offer = await store.GetOfferAsync(SystemHealthService.ProductVersion, ct);
        if (offer is null)
        {
            return TypedResults.NotFound();
        }

        if (http.Request.Headers.Range.Count == 0)
        {
            loggers.CreateLogger(typeof(DesktopUpdateEndpoints)).LogInformation(
                "Workstation {DeviceId} downloads HC360 Desktop {Version}", device.Id, offer.Manifest.Version);
        }

        // The SHA-256 is the entity tag: a download resumed with If-Range, or sent with If-Match, gets only this package.
        return TypedResults.PhysicalFile(
            offer.PackagePath,
            "application/zip",
            offer.Manifest.File,
            lastModified: offer.Manifest.ReleasedAt,
            entityTag: new EntityTagHeaderValue($"\"{offer.Manifest.Sha256.ToLowerInvariant()}\""),
            enableRangeProcessing: true);
    }

    private static async Task<IResult> GetState(DesktopPackageStore store, CancellationToken ct)
    {
        var (offer, problem) = await store.CheckAsync(SystemHealthService.ProductVersion, ct);
        return TypedResults.Ok(new DesktopUpdateStateDto(offer?.Manifest.Version, offer?.Manifest.ReleasedAt, problem));
    }

    private static IResult NotRecognised() => TypedResults.Problem(title: "Device not recognised", statusCode: StatusCodes.Status401Unauthorized);
}
