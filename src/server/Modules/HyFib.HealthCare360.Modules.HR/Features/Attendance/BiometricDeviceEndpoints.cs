using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Features.Attendance;

/// <summary>
/// /api/v1/hr/biometric-devices — terminal registry — and the JSON punch API for non-ADMS terminals/middleware
/// (INT-BIO). The JSON API is anonymous at the HTTP level but authenticated by serial number + device key.
/// </summary>
internal static class BiometricDeviceEndpoints
{
    public const string SerialHeader = "X-Device-Serial";
    public const string KeyHeader = "X-Device-Key";

    /// <summary>A terminal that has not been heard from for this long is shown as offline.</summary>
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(10);

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr/biometric-devices").WithTags("HR");

        group.MapGet("/", List).RequirePermission(HRPermissions.AttendanceManage);
        group.MapPost("/", Register).RequirePermission(HRPermissions.AttendanceManage).WithValidation<SaveBiometricDeviceRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(HRPermissions.AttendanceManage).WithValidation<SaveBiometricDeviceRequest>();
        group.MapPost("/{id:guid}/rotate-key", RotateKey).RequirePermission(HRPermissions.AttendanceManage);

        app.MapPost("/api/v1/hr/attendance/device-punches", ReceivePunches)
            .WithTags("Biometric devices")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Device)
            .WithValidation<DevicePunchBatch>();
    }

    private static async Task<IResult> List(HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var devices = await db.Set<BiometricDevice>().AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);
        return TypedResults.Ok(devices.Select(d => new BiometricDeviceDto(
            d.Id, d.SerialNumber, d.Name, d.Location, d.DepartmentId, d.Model, d.AllowedIpAddress, d.ApiKeyHash is not null, d.IsActive,
            d.LastPushAt, d.LastHandshakeAt, d.LastIpAddress, d.LastHandshakeAt is { } seen && now - seen <= OnlineWindow, d.Version)).ToList());
    }

    private static async Task<IResult> Register(SaveBiometricDeviceRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var serial = request.SerialNumber.Trim();
        if (await db.Set<BiometricDevice>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(d => d.SerialNumber == serial, ct))
        {
            return Error.Conflict("device.serial_in_use", "A terminal with this serial number is already registered.").ToProblem();
        }

        var key = BiometricIngestionService.NewApiKey();
        var device = new BiometricDevice { FacilityId = facilityId, SerialNumber = serial, ApiKeyHash = BiometricIngestionService.HashApiKey(key) };
        Apply(device, request);
        db.Add(device);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/hr/biometric-devices/{device.Id}", new DeviceKeyIssued(device.Id, device.SerialNumber, key));
    }

    private static async Task<IResult> Update(Guid id, SaveBiometricDeviceRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var device = await db.Set<BiometricDevice>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Error.NotFound(nameof(BiometricDevice), id).ToProblem();
        }

        if (!string.Equals(device.SerialNumber, request.SerialNumber.Trim(), StringComparison.Ordinal))
        {
            return Error.Field("serialNumber", "The serial number of a registered terminal cannot be changed; register the new terminal instead.").ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(device, version);
        }

        Apply(device, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RotateKey(Guid id, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var device = await db.Set<BiometricDevice>().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (device is null)
        {
            return Error.NotFound(nameof(BiometricDevice), id).ToProblem();
        }

        var key = BiometricIngestionService.NewApiKey();
        device.ApiKeyHash = BiometricIngestionService.HashApiKey(key);
        audit.Add(new AuditEntry(AuditCategories.Security, "biometric_device.key_rotated", $"Device key of terminal {device.SerialNumber} rotated")
        {
            EntityType = nameof(BiometricDevice),
            EntityId = device.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new DeviceKeyIssued(device.Id, device.SerialNumber, key));
    }

    private static async Task<IResult> ReceivePunches(DevicePunchBatch batch, HttpRequest request, BiometricIngestionService ingestion, CancellationToken ct)
    {
        var device = await ingestion.FindDeviceAsync(request.Headers[SerialHeader].ToString(), request.HttpContext.Connection.RemoteIpAddress, ct);
        if (device is null || !BiometricIngestionService.VerifyApiKey(device, request.Headers[KeyHeader].ToString()))
        {
            // Same answer for unknown serials and wrong keys: nothing to learn by probing.
            return TypedResults.Problem(title: "Device not authorised", statusCode: StatusCodes.Status401Unauthorized);
        }

        var candidates = batch.Punches
            .Select(p => new PunchCandidate(p.Pin.Trim(), p.PunchedAt.ToUniversalTime(), p.Direction ?? PunchDirection.Unknown, null, p.VerifyMode, null))
            .ToList();
        var result = await ingestion.IngestAsync(device, candidates, PunchSource.DeviceApi, ct);
        await ingestion.RecordPushAsync(device, request.HttpContext.Connection.RemoteIpAddress, null, ct);
        return TypedResults.Ok(result);
    }

    private static void Apply(BiometricDevice device, SaveBiometricDeviceRequest request)
    {
        device.Name = request.Name.Trim();
        device.Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim();
        device.DepartmentId = request.DepartmentId;
        device.Model = string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim();
        device.AllowedIpAddress = string.IsNullOrWhiteSpace(request.AllowedIpAddress) ? null : request.AllowedIpAddress.Trim();
        device.IsActive = request.IsActive;
    }
}
