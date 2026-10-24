using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Services;

namespace HyFib.HealthCare360.Modules.VirtualCare.Features.Rpm;

/// <summary>/api/v1/rpm/devices — device registration, key rotation and assignment (RPM-01/02).</summary>
internal static class RpmDeviceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rpm/devices").WithTags("Remote Monitoring");

        group.MapGet("/", List).RequirePermission(VirtualCarePermissions.RpmRead);
        group.MapPost("/", Register).RequirePermission(VirtualCarePermissions.RpmManage).WithValidation<RegisterDeviceRequest>();
        group.MapPost("/{id:guid}/rotate-key", RotateKey).RequirePermission(VirtualCarePermissions.RpmManage);
        group.MapPost("/{id:guid}/assign", Assign).RequirePermission(VirtualCarePermissions.RpmManage).WithValidation<AssignDeviceRequest>();
        group.MapPost("/{id:guid}/deactivate", Deactivate).RequirePermission(VirtualCarePermissions.RpmManage);
    }

    internal static RpmDeviceDto ToDto(RpmDevice d) => new(
        d.Id, d.SerialNumber, d.Type, d.Manufacturer, d.Model, d.EnrollmentId, d.PatientId, d.AssignedAt, d.LastSeenAt, d.ApiKeyIssuedAt,
        d.IsActive, d.SilenceAlertRaisedAt is not null, d.Version);

    private static async Task<IResult> List(Guid? enrollmentId, bool? activeOnly, bool? unassigned, HospitalDbContext db, CancellationToken ct)
    {
        var devices = db.Set<RpmDevice>().AsNoTracking();
        if (activeOnly != false)
        {
            devices = devices.Where(d => d.IsActive);
        }

        if (enrollmentId is not null)
        {
            devices = devices.Where(d => d.EnrollmentId == enrollmentId);
        }

        if (unassigned == true)
        {
            devices = devices.Where(d => d.EnrollmentId == null);
        }

        var rows = await devices.OrderBy(d => d.SerialNumber).Take(500).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(ToDto).ToList());
    }

    /// <summary>Registers a device and returns its API key once (configure it on the device or gateway immediately).</summary>
    private static async Task<IResult> Register(RegisterDeviceRequest request, RpmEnrollmentService service, CancellationToken ct) =>
        (await service.RegisterDeviceAsync(request, ct)).ToCreated(issued => $"/api/v1/rpm/devices/{issued.DeviceId}");

    private static async Task<IResult> RotateKey(Guid id, RpmEnrollmentService service, CancellationToken ct) =>
        (await service.RotateKeyAsync(id, ct)).ToOk();

    private static async Task<IResult> Assign(Guid id, AssignDeviceRequest request, RpmEnrollmentService service, CancellationToken ct) =>
        (await service.AssignDeviceAsync(id, request.EnrollmentId, ct)).ToNoContent();

    private static async Task<IResult> Deactivate(Guid id, RpmEnrollmentService service, CancellationToken ct) =>
        (await service.DeactivateDeviceAsync(id, ct)).ToNoContent();
}
