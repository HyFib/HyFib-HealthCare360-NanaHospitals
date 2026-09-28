using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.Tally;

/// <summary>/api/v1/billing/tally — day-book posting log, (re)queue, voucher preview and the manual import file (INT-TALLY).</summary>
internal static class TallyEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/tally").WithTags("Billing - Tally");
        group.MapGet("/exports", List).RequirePermission(BillingPermissions.TallyExport);
        group.MapPost("/exports", Queue).RequirePermission(BillingPermissions.TallyExport).WithValidation<PostDayBookRequest>();
        group.MapGet("/day-book", Vouchers).RequirePermission(BillingPermissions.TallyExport);
        group.MapGet("/export-file", ExportFile).RequirePermission(BillingPermissions.TallyExport);
    }

    private static async Task<IResult> List(DateOnly? from, DateOnly? to, HospitalDbContext db, ICurrentUser currentUser, TimeProvider time, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var end = to ?? BillingTime.Today(time);
        var start = from ?? end.AddDays(-30);
        var rows = await db.Set<TallyExport>().AsNoTracking()
            .Where(e => e.FacilityId == facilityId && e.Date >= start && e.Date <= end)
            .OrderByDescending(e => e.Date)
            .Select(e => new TallyExportDto(
                e.Id, e.Date, e.Status, e.VoucherCount, e.Attempts, e.CreatedCount, e.AlteredCount, e.ErrorCount, e.CreatedAt, e.LastAttemptAt, e.PostedAt, e.Message))
            .ToListAsync(ct);
        return TypedResults.Ok(rows);
    }

    private static async Task<IResult> Queue(PostDayBookRequest request, TallyService tally, ICurrentUser currentUser, CancellationToken ct) =>
        currentUser.FacilityId is { } facilityId
            ? (await tally.QueueAsync(facilityId, request.Date, ct)).ToNoContent()
            : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> Vouchers(DateOnly date, TallyService tally, ICurrentUser currentUser, CancellationToken ct) =>
        currentUser.FacilityId is { } facilityId
            ? TypedResults.Ok(await tally.BuildVouchersAsync(facilityId, date, ct))
            : BillingDb.FacilityRequired.ToProblem();

    private static async Task<IResult> ExportFile(DateOnly date, TallyService tally, ICurrentUser currentUser, IAuditLogger audit, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return BillingDb.FacilityRequired.ToProblem();
        }

        var (content, contentType, fileName) = await tally.ExportFileAsync(facilityId, date, ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Data, "billing.tally.exported", $"Downloaded the Tally day book of {date:yyyy-MM-dd}"), ct);
        return TypedResults.File(content, contentType, fileName);
    }
}
