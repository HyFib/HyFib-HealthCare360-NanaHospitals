using HyFib.HealthCare360.Modules.Quality.Domain.Hai;
using HyFib.HealthCare360.Modules.Quality.Services;

namespace HyFib.HealthCare360.Modules.Quality.Features.Hai;

/// <summary>
/// /api/v1/quality/hai — hospital-acquired infection surveillance: the device register (nurses), the case review against the
/// NHSN definitions and the monthly rates (the infection control team).
/// </summary>
internal static class HaiEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/quality/hai").WithTags("Quality");

        group.MapGet("/devices", (bool? inPlace, Guid? admissionId, InvasiveDevice? device, HaiService hai, CancellationToken ct) =>
                hai.ListDevicesAsync(inPlace, admissionId, device, ct))
            .RequireAnyPermission(QualityPermissions.HaiDevices, QualityPermissions.HaiManage);
        group.MapPost("/devices", async (InsertDeviceRequest r, HaiService hai, CancellationToken ct) =>
                (await hai.InsertDeviceAsync(r, ct)).ToCreated(d => $"/api/v1/quality/hai/devices/{d.Id}"))
            .RequireAnyPermission(QualityPermissions.HaiDevices, QualityPermissions.HaiManage).WithValidation<InsertDeviceRequest>();
        group.MapPost("/devices/{id:guid}/remove", async (Guid id, RemoveDeviceRequest r, HaiService hai, CancellationToken ct) => (await hai.RemoveDeviceAsync(id, r, ct)).ToOk())
            .RequireAnyPermission(QualityPermissions.HaiDevices, QualityPermissions.HaiManage).WithValidation<RemoveDeviceRequest>();

        group.MapGet("/cases", (HaiCaseStatus? status, HaiType? type, DateOnly? from, DateOnly? to, HaiService hai, CancellationToken ct) =>
                hai.ListCasesAsync(status, type, from, to, ct))
            .RequirePermission(QualityPermissions.HaiManage);
        group.MapGet("/cases/{id:guid}", async (Guid id, HaiService hai, CancellationToken ct) => (await hai.GetCaseAsync(id, ct)).ToOk())
            .RequirePermission(QualityPermissions.HaiManage);
        group.MapGet("/cases/{id:guid}/surgeries", async (Guid id, HaiService hai, CancellationToken ct) => (await hai.SurgeriesAsync(id, ct)).ToOk())
            .RequirePermission(QualityPermissions.HaiManage);
        group.MapPost("/cases", async (RaiseHaiCaseRequest r, HaiService hai, CancellationToken ct) =>
                (await hai.RaiseAsync(r, ct)).ToCreated(c => $"/api/v1/quality/hai/cases/{c.Id}"))
            .RequirePermission(QualityPermissions.HaiManage).WithValidation<RaiseHaiCaseRequest>();
        group.MapPut("/cases/{id:guid}", async (Guid id, ReviewHaiCaseRequest r, HaiService hai, CancellationToken ct) => (await hai.ReviewAsync(id, r, ct)).ToOk())
            .RequirePermission(QualityPermissions.HaiManage).WithValidation<ReviewHaiCaseRequest>();
        group.MapPost("/cases/{id:guid}/confirm", async (Guid id, DecideHaiCaseRequest r, HaiService hai, CancellationToken ct) =>
                (await hai.DecideAsync(id, HaiCaseStatus.Confirmed, r, ct)).ToOk())
            .RequirePermission(QualityPermissions.HaiManage).WithValidation<DecideHaiCaseRequest>();
        group.MapPost("/cases/{id:guid}/rule-out", async (Guid id, DecideHaiCaseRequest r, HaiService hai, CancellationToken ct) =>
                (await hai.DecideAsync(id, HaiCaseStatus.RuledOut, r, ct)).ToOk())
            .RequirePermission(QualityPermissions.HaiManage).WithValidation<DecideHaiCaseRequest>();
        group.MapPost("/cases/{id:guid}/reopen", async (Guid id, DecideHaiCaseRequest r, HaiService hai, CancellationToken ct) =>
                (await hai.DecideAsync(id, HaiCaseStatus.Suspected, r, ct)).ToOk())
            .RequirePermission(QualityPermissions.HaiManage).WithValidation<DecideHaiCaseRequest>();

        group.MapGet("/rates", async (DateOnly? from, DateOnly? to, HaiService hai, CancellationToken ct) => (await hai.RatesAsync(from, to, ct)).ToOk())
            .RequirePermission(QualityPermissions.HaiManage);
    }
}
