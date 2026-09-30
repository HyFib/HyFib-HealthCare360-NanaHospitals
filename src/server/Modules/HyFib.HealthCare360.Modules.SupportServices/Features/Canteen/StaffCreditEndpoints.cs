using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Canteen;

/// <summary>/api/v1/canteen/staff-credit — staff credit balances, monthly statements, payroll export and settlement (CAN-03).</summary>
internal static class StaffCreditEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/canteen/staff-credit").WithTags("Canteen");

        group.MapGet("/balances", async (bool? outstandingOnly, StaffCreditService credit, CancellationToken ct) =>
                TypedResults.Ok(await credit.BalancesAsync(outstandingOnly != false, ct)))
            .RequirePermission(SupportServicesPermissions.CanteenCredit);
        group.MapGet("/statement", async (string employeeCode, string period, StaffCreditService credit, CancellationToken ct) =>
                (await credit.StatementAsync(employeeCode, period, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CanteenCredit);
        group.MapGet("/export", Export).RequirePermission(SupportServicesPermissions.CanteenCredit);
        group.MapPost("/settlements", async (SettleStaffCreditRequest r, StaffCreditService credit, CancellationToken ct) =>
                (await credit.SettleAsync(r, ct)).ToOk())
            .RequirePermission(SupportServicesPermissions.CanteenCredit).WithValidation<SettleStaffCreditRequest>();
        group.MapPost("/payments", async (StaffPaymentRequest r, StaffCreditService credit, CancellationToken ct) =>
                (await credit.RecordPaymentAsync(r, ct)).ToOk())
            .RequireAnyPermission(SupportServicesPermissions.CanteenCredit, SupportServicesPermissions.CanteenSales).WithValidation<StaffPaymentRequest>();
    }

    /// <summary>CSV for HR's payroll deduction of the month (yyyy-MM).</summary>
    private static async Task<IResult> Export(string period, StaffCreditService credit, CancellationToken ct)
    {
        var export = await credit.ExportAsync(period, ct);
        return export.IsFailure
            ? export.Error!.ToProblem()
            : TypedResults.File(export.Value.Content, "text/csv; charset=utf-8", export.Value.FileName);
    }
}
