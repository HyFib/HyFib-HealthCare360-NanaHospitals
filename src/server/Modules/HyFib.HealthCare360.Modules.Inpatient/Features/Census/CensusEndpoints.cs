using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Census;

/// <summary>/api/v1/inpatient/census — occupancy and census statistics for a date range (WB-06).</summary>
internal static class CensusEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/inpatient/census", Report)
            .WithTags("Admissions")
            .RequirePermission(InpatientPermissions.AdmissionsRead);
    }

    /// <summary>Dates are IST calendar dates; the default is the last 7 days including today.</summary>
    private static async Task<IResult> Report(DateOnly? from, DateOnly? to, Guid? wardId, CensusService census, TimeProvider time, CancellationToken ct)
    {
        var end = to ?? time.Today();
        var start = from ?? end.AddDays(-6);
        if (start > end)
        {
            return Error.Field("from", "The start date must be on or before the end date.").ToProblem();
        }

        if (end.DayNumber - start.DayNumber + 1 > CensusService.MaxDays)
        {
            return Error.Field("to", $"Choose a period of at most {CensusService.MaxDays} days.").ToProblem();
        }

        return TypedResults.Ok(await census.ReportAsync(start, end, wardId, ct));
    }
}
