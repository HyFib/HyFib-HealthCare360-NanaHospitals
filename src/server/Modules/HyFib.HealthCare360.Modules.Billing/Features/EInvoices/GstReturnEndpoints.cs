using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Services;

namespace HyFib.HealthCare360.Modules.Billing.Features.EInvoices;

/// <summary>
/// /api/v1/billing/gst-returns — GSTR-1 and GSTR-3B of the signed-in facility's GSTIN for a month (<c>?month=2026-10</c>),
/// and the GSTN offline tool's JSON of GSTR-1.
/// </summary>
internal static class GstReturnEndpoints
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/billing/gst-returns").WithTags("Billing").RequirePermission(BillingPermissions.GstReturns);
        group.MapGet("/gstr1", async (string? month, GstReturnService returns, CancellationToken ct) =>
            Month(month) is not { } m ? BadMonth() : (await returns.Gstr1Async(m.Year, m.Month, ct)).ToOk());
        group.MapGet("/gstr3b", async (string? month, GstReturnService returns, CancellationToken ct) =>
            Month(month) is not { } m ? BadMonth() : (await returns.Gstr3bAsync(m.Year, m.Month, ct)).ToOk());
        group.MapGet("/gstr1/json", async (string? month, GstReturnService returns, CancellationToken ct) =>
        {
            if (Month(month) is not { } m)
            {
                return BadMonth();
            }

            var built = await returns.Gstr1JsonAsync(m.Year, m.Month, ct);
            return built.IsFailure
                ? built.Error!.ToProblem()
                : TypedResults.File(Encoding.UTF8.GetBytes(built.Value.Json.ToJsonString(Indented)), "application/json", built.Value.FileName);
        });
    }

    private static (int Year, int Month)? Month(string? month) =>
        DateOnly.TryParseExact($"{month}-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
        && date.Year >= 2017
            ? (date.Year, date.Month)
            : null;

    private static IResult BadMonth() => Error.Field("month", "Choose the month, as 2026-10 (GST began in July 2017).").ToProblem();
}
