using System.Text.Json;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Services;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;

/// <summary>How devices and gateways reach the ingestion endpoint.</summary>
public static class DeviceIngestion
{
    public const string Path = "/api/v1/rpm/ingest";
    public const string KeyHeader = "X-Device-Key";
    public const string RateLimitPolicy = RateLimitPolicies.Device;

    /// <summary>A full batch of 500 readings is well under 100 KB; anything much larger is refused before parsing.</summary>
    public const long MaxBodyBytes = 1024 * 1024;
}

/// <summary>
/// POST /api/v1/rpm/ingest — device and gateway uploads (RPM-02). Anonymous at the HTTP level and rate-limited; the
/// device proves itself with its <c>X-Device-Key</c>, checked against the stored hash in constant time. Unknown or
/// wrong keys get the same 401 so the endpoint cannot be used to discover devices.
/// </summary>
internal static class RpmIngestEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost(DeviceIngestion.Path, Ingest)
            .WithTags("Remote Monitoring")
            .AllowAnonymous()
            .RequireRateLimiting(DeviceIngestion.RateLimitPolicy)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(DeviceIngestion.MaxBodyBytes));

    private static async Task<IResult> Ingest(
        JsonElement body,
        HttpRequest request,
        HospitalDbContext db,
        RpmIngestionService ingestion,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var key = request.Headers[DeviceIngestion.KeyHeader].ToString();
        if (!DeviceApiKeys.TryGetDeviceId(key, out var deviceId))
        {
            return NotRecognised();
        }

        // Anonymous request: no facility scope applies, the device decides the facility.
        var device = await db.Set<RpmDevice>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null || !device.Accepts(key))
        {
            loggers.CreateLogger(typeof(RpmIngestEndpoints)).LogWarning("Rejected RPM upload with an invalid device key");
            return NotRecognised();
        }

        var enrollment = device.EnrollmentId is { } enrollmentId
            ? await db.Set<RpmEnrollment>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(e => e.Id == enrollmentId, ct)
            : null;
        if (enrollment is null)
        {
            return Error.Rule("rpm.device_unassigned", "This device is not assigned to a patient. Ask the hospital to assign it.").ToProblem();
        }

        var payload = DeviceReadingParser.Parse(body);
        if (payload.Readings.Count == 0)
        {
            return Error.Validation("rpm.no_readings", "No readings were found in the request.",
                new Dictionary<string, string[]> { ["readings"] = payload.Errors.DefaultIfEmpty("Send at least one reading.").ToArray() }).ToProblem();
        }

        var result = await ingestion.IngestAsync(enrollment, device, payload.Readings, payload.Source, null, ct);
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        var outcome = result.Value with { Rejected = [.. payload.Errors, .. result.Value.Rejected] };
        return outcome.Accepted == 0 && outcome.Duplicates == 0
            ? Error.Validation("rpm.all_rejected", "None of the readings could be accepted.",
                new Dictionary<string, string[]> { ["readings"] = outcome.Rejected.ToArray() }).ToProblem()
            : TypedResults.Ok(outcome);
    }

    private static IResult NotRecognised() =>
        TypedResults.Problem(title: "Device not recognised", detail: $"Send a valid {DeviceIngestion.KeyHeader} header.", statusCode: StatusCodes.Status401Unauthorized);
}
