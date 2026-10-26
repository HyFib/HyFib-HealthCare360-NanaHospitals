using System.Globalization;
using System.Text;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;
using HyFib.HealthCare360.Modules.SupportServices.Services;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Waste;

/// <summary>
/// /api/v1/bmw: bio-medical waste. Bags bar-coded and weighed where the waste is generated, the storage area's pending
/// bags, the handover to the common treatment facility, the month's register and the year's totals (Form IV).
/// </summary>
internal static class WasteEndpoints
{
    private static readonly string[] Readers = [SupportServicesPermissions.WasteRecord, SupportServicesPermissions.WasteManage];

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/bmw").WithTags("Bio-medical Waste");

        group.MapGet("/bags", Bags).RequireAnyPermission(Readers);
        group.MapGet("/bags/pending", Pending).RequireAnyPermission(Readers);
        group.MapPost("/bags", Record).RequirePermission(SupportServicesPermissions.WasteRecord).WithValidation<RecordWasteBagRequest>();
        group.MapPost("/bags/{id:guid}/void", Void).RequireAnyPermission(Readers).WithValidation<VoidWasteBagRequest>();
        group.MapGet("/handovers", Handovers).RequirePermission(SupportServicesPermissions.WasteManage);
        group.MapPost("/handovers", HandOver).RequirePermission(SupportServicesPermissions.WasteManage).WithValidation<HandOverWasteRequest>();
        group.MapGet("/register", Month).RequirePermission(SupportServicesPermissions.WasteManage);
        group.MapGet("/register/export", MonthExport).RequirePermission(SupportServicesPermissions.WasteManage);
        group.MapGet("/annual", Year).RequirePermission(SupportServicesPermissions.WasteManage);
    }

    private static async Task<IResult> Bags(DateOnly? from, DateOnly? to, WasteService waste, TimeProvider time, CancellationToken ct)
    {
        var (f, t) = Range(from, to, time);
        return TypedResults.Ok(await waste.BagsAsync(f, t, ct));
    }

    private static async Task<IResult> Pending(WasteService waste, CancellationToken ct) => TypedResults.Ok(await waste.PendingAsync(ct));

    private static async Task<IResult> Record(RecordWasteBagRequest request, WasteService waste, CancellationToken ct) =>
        (await waste.RecordAsync(request, ct)).ToOk();

    private static async Task<IResult> Void(Guid id, VoidWasteBagRequest request, WasteService waste, CancellationToken ct) =>
        (await waste.VoidAsync(id, request, ct)).ToOk();

    private static async Task<IResult> Handovers(DateOnly? from, DateOnly? to, WasteService waste, TimeProvider time, CancellationToken ct)
    {
        var (f, t) = Range(from, to, time);
        return TypedResults.Ok(await waste.HandoversAsync(f, t, ct));
    }

    private static async Task<IResult> HandOver(HandOverWasteRequest request, WasteService waste, CancellationToken ct) =>
        (await waste.HandOverAsync(request, ct)).ToOk();

    private static async Task<IResult> Month(int? year, int? month, WasteService waste, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = YearMonth(year, month, time);
        return m is < 1 or > 12 ? Error.Field("month", "Choose a month from 1 to 12.").ToProblem() : TypedResults.Ok(await waste.MonthAsync(y, m, ct));
    }

    private static async Task<IResult> MonthExport(int? year, int? month, WasteService waste, TimeProvider time, CancellationToken ct)
    {
        var (y, m) = YearMonth(year, month, time);
        if (m is < 1 or > 12)
        {
            return Error.Field("month", "Choose a month from 1 to 12.").ToProblem();
        }

        var register = await waste.MonthAsync(y, m, ct);
        var text = new StringBuilder("Date,Yellow (kg),Red (kg),White (kg),Blue (kg),Total (kg)\r\n");
        foreach (var day in register.Days)
        {
            text.Append(day.Date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture));
            foreach (var category in day.ByCategory)
            {
                text.Append(',').Append(Kg(category.Kg));
            }

            text.Append(',').Append(Kg(day.TotalKg)).Append("\r\n");
        }

        text.Append("Total");
        foreach (var category in register.Totals)
        {
            text.Append(',').Append(Kg(category.Kg));
        }

        text.Append(',').Append(Kg(register.Totals.Sum(c => c.Kg))).Append("\r\n");
        return TypedResults.File(
            [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())], "text/csv; charset=utf-8",
            string.Create(CultureInfo.InvariantCulture, $"bmw-register-{y}-{m:00}.csv"));
    }

    private static async Task<IResult> Year(int? year, WasteService waste, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(await waste.YearAsync(year ?? time.Today().Year - 1, ct));

    private static string Kg(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static (int Year, int Month) YearMonth(int? year, int? month, TimeProvider time)
    {
        var today = time.Today();
        return (year ?? today.Year, month ?? today.Month);
    }

    private static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, TimeProvider time)
    {
        var today = time.Today();
        var end = to ?? today;
        var start = from ?? end.AddDays(-6);
        return start > end ? (end, end) : (start, end.DayNumber - start.DayNumber > 92 ? start.AddDays(92) : end);
    }
}
