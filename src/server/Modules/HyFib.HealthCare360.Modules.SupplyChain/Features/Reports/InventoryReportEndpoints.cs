using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Reports;

/// <summary>/api/v1/inventory/reports — expiry, reorder, valuation, store dashboard and ABC/VED/FSN analysis (INV-08).</summary>
internal static class InventoryReportEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/inventory/reports").WithTags("Inventory");

        group.MapGet("/summary", async (Guid? storeId, InventoryReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.SummaryAsync(storeId, ct)))
            .RequirePermission(SupplyChainPermissions.StockRead);
        group.MapGet("/near-expiry", async (Guid? storeId, int? days, InventoryReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.NearExpiryAsync(storeId, Math.Clamp(days ?? InventoryReportService.DefaultNearExpiryDays, 1, 730), ct)))
            .RequirePermission(SupplyChainPermissions.StockRead);
        group.MapGet("/expired", async (Guid? storeId, InventoryReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.ExpiredAsync(storeId, ct)))
            .RequirePermission(SupplyChainPermissions.StockRead);
        group.MapGet("/reorder", async (Guid? storeId, InventoryReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.ReorderAsync(storeId, ct)))
            .RequireAnyPermission(SupplyChainPermissions.StockRead, SupplyChainPermissions.PurchaseManage);
        group.MapGet("/valuation", async (Guid? storeId, InventoryReportService reports, CancellationToken ct) =>
                TypedResults.Ok(await reports.ValuationAsync(storeId, ct)))
            .RequirePermission(SupplyChainPermissions.StockRead);
        group.MapGet("/analysis", Analysis).RequireAnyPermission(SupplyChainPermissions.StockRead, SupplyChainPermissions.PurchaseManage);
    }

    /// <summary>ABC/VED/FSN over a period (default: the last 12 months). Dates are local (IST) calendar dates.</summary>
    private static async Task<IResult> Analysis(DateOnly? from, DateOnly? to, Guid? storeId, InventoryReportService reports, TimeProvider time, CancellationToken ct)
    {
        var today = HospitalClock.Today(time);
        var end = to ?? today;
        var start = from ?? end.AddYears(-1).AddDays(1);
        if (start > end)
        {
            return Error.Field("from", "The start date must be on or before the end date.").ToProblem();
        }

        if (end.DayNumber - start.DayNumber > 3 * 366)
        {
            return Error.Field("from", "Analyse at most three years at a time.").ToProblem();
        }

        return TypedResults.Ok(await reports.AnalyseAsync(start, end, storeId, ct));
    }
}
