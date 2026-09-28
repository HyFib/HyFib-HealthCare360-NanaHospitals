using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Shifts;

/// <summary>/api/v1/billing/shifts — cashier shifts and the day-end collection report (OPB-07).</summary>
internal static class ShiftEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/shifts").WithTags("Billing - Cash counter");
        group.MapGet("/current", Current).RequirePermission(BillingPermissions.Shifts);
        group.MapGet("/", List).RequirePermission(BillingPermissions.Shifts);
        group.MapGet("/{id:guid}", Get).RequirePermission(BillingPermissions.Shifts);
        group.MapPost("/open", Open).RequirePermission(BillingPermissions.Shifts).WithValidation<OpenShiftRequest>();
        group.MapPost("/{id:guid}/close", Close).RequirePermission(BillingPermissions.Shifts).WithValidation<CloseShiftRequest>();

        app.MapGet("/api/v1/billing/reports/day-end", DayEnd).WithTags("Billing - Cash counter").RequirePermission(BillingPermissions.Shifts);
    }

    /// <summary>The signed-in cashier's open shift with running totals, or 204 when none is open.</summary>
    private static async Task<IResult> Current(ShiftService shifts, CancellationToken ct) =>
        await shifts.CurrentAsync(ct) is { } shift ? Results.Ok(await shifts.WithLiveTotalsAsync(shift, ct)) : Results.NoContent();

    private static async Task<IResult> List(
        [AsParameters] PageQuery query, DateOnly? date, Guid? userId, string? counter, ShiftStatus? status, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var page = query.ToRequest();
        var (start, end) = BillingTime.Range(date ?? BillingTime.Today(time), date ?? BillingTime.Today(time));
        var shifts = db.Set<CashierShift>().AsNoTracking().Include(s => s.Totals).Where(s => s.OpenedAt < end && (s.ClosedAt == null || s.ClosedAt >= start));
        if (userId is not null)
        {
            shifts = shifts.Where(s => s.UserId == userId);
        }

        if (!string.IsNullOrWhiteSpace(counter))
        {
            shifts = shifts.Where(s => s.Counter == counter);
        }

        if (status is not null)
        {
            shifts = shifts.Where(s => s.Status == status);
        }

        var result = await shifts.OrderByDescending(s => s.OpenedAt).ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(s => ShiftService.ToDto(s, 0)));
    }

    private static async Task<IResult> Get(Guid id, ShiftService shifts, CancellationToken ct) =>
        await shifts.GetAsync(id, ct) is { } shift ? TypedResults.Ok(shift) : Error.NotFound(nameof(CashierShift), id).ToProblem();

    private static async Task<IResult> Open(OpenShiftRequest request, ShiftService shifts, CancellationToken ct) =>
        (await shifts.OpenAsync(request, ct)).ToCreated(s => $"/api/v1/billing/shifts/{s.Id}");

    private static async Task<IResult> Close(Guid id, CloseShiftRequest request, ShiftService shifts, CancellationToken ct) =>
        (await shifts.CloseAsync(id, request, ct)).ToOk();

    private static async Task<IResult> DayEnd(DateOnly? date, string? counter, ShiftService shifts, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(await shifts.DayEndAsync(date ?? BillingTime.Today(time), counter, ct));
}
