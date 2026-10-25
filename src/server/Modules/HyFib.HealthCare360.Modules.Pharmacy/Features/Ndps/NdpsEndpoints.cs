using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Registers;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Ndps;

/// <summary>
/// /api/v1/pharmacy/ndps — the NDPS accounts of the essential narcotic drugs: the day's account of a store (Form 3H),
/// a patient's record (Form 3E), the year's return with next year's estimate (Form 3J), and the two-person count at a
/// change of shift. Reads that name patients, and every export, are audited.
/// </summary>
internal static class NdpsEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/pharmacy/ndps").WithTags("Pharmacy");

        group.MapGet("/daily", Daily).RequirePermission(PharmacyPermissions.Registers);
        group.MapGet("/daily/export", DailyExport).RequirePermission(PharmacyPermissions.Registers);
        group.MapGet("/annual", Annual).RequirePermission(PharmacyPermissions.Registers);
        group.MapGet("/annual/export", AnnualExport).RequirePermission(PharmacyPermissions.Registers);
        group.MapGet("/patients/{patientId:guid}", PatientRecord).RequirePermission(PharmacyPermissions.Registers);

        group.MapGet("/counts", Counts).RequireAnyPermission(PharmacyPermissions.NdpsCount, PharmacyPermissions.Registers);
        group.MapGet("/stores", Stores).RequireAnyPermission(PharmacyPermissions.NdpsCount, PharmacyPermissions.Registers);
        group.MapGet("/counts/sheet", Sheet).RequirePermission(PharmacyPermissions.NdpsCount);
        group.MapPost("/counts", Count).RequirePermission(PharmacyPermissions.NdpsCount).WithValidation<RecordNarcoticCountRequest>();
        group.MapPost("/counts/{id:guid}/witness", Witness).RequirePermission(PharmacyPermissions.NdpsCount)
            .WithValidation<WitnessNarcoticCountRequest>();
    }

    private static async Task<IResult> Daily(Guid storeId, DateOnly? date, NdpsService ndps, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var day = date ?? HospitalClock.Today(time);
        var account = await ndps.DailyAsync(storeId, day, ct);
        if (account.IsSuccess)
        {
            await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "pharmacy.ndps.daily.viewed",
                $"Viewed the narcotic account (Form 3H) of {account.Value.StoreName} for {day:yyyy-MM-dd}"), ct);
        }

        return account.ToOk();
    }

    private static async Task<IResult> DailyExport(Guid storeId, DateOnly? date, NdpsService ndps, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var day = date ?? HospitalClock.Today(time);
        var account = await ndps.DailyAsync(storeId, day, ct);
        if (account.IsFailure)
        {
            return account.Error!.ToProblem();
        }

        var a = account.Value;
        await audit.WriteAsync(new AuditEntry(AuditCategories.PhiAccess, "pharmacy.ndps.daily.exported",
            $"Narcotic account (Form 3H) of {a.StoreName} for {day:yyyy-MM-dd} exported")
        { Severity = AuditSeverity.Warning }, ct);
        var lines = new List<IReadOnlyList<string?>>
        {
            Csv.Row("Drug", "Time", "Movement", "Opening", "Received", "Dispensed", "Patient", "UHID", "Prescriber", "Prescription", "Reference",
                "Balance"),
        };
        foreach (var drug in a.Drugs)
        {
            lines.Add(Csv.Row(drug.ItemName, null, "Opening stock", Csv.Number(drug.Opening)));
            lines.AddRange(drug.Movements.Select(m => Csv.Row(
                drug.ItemName, Csv.Time(m.At), m.Type.ToString(), null, m.In == 0 ? null : Csv.Number(m.In), m.Out == 0 ? null : Csv.Number(m.Out),
                m.PatientName, m.PatientUhid, m.PrescriberName, m.PrescriptionNumber, m.Reference, Csv.Number(m.BalanceAfter))));
            lines.Add(Csv.Row(drug.ItemName, null, "Closing stock", null, Csv.Number(drug.Received + drug.OtherIn), Csv.Number(drug.Dispensed + drug.OtherOut),
                null, null, null, null, null, Csv.Number(drug.Closing)));
        }

        return TypedResults.File(Csv.Build(lines), "text/csv; charset=utf-8",
            string.Create(CultureInfo.InvariantCulture, $"ndps-form-3h-{day:yyyyMMdd}.csv"));
    }

    private static async Task<IResult> Annual(int? year, NdpsService ndps, TimeProvider time, CancellationToken ct) =>
        TypedResults.Ok(await ndps.AnnualAsync(year ?? HospitalClock.Today(time).Year - 1, ct));

    private static async Task<IResult> AnnualExport(int? year, NdpsService ndps, IAuditLogger audit, TimeProvider time, CancellationToken ct)
    {
        var report = await ndps.AnnualAsync(year ?? HospitalClock.Today(time).Year - 1, ct);
        await audit.WriteAsync(new AuditEntry(AuditCategories.Clinical, "pharmacy.ndps.annual.exported", $"NDPS annual return for {report.Year} exported"), ct);
        var lines = report.Drugs.Select(d => Csv.Row(
                d.ItemName, Csv.Number(d.Opening), Csv.Number(d.Received), Csv.Number(d.Consumed), Csv.Number(d.WrittenOff), Csv.Number(d.Closing),
                Csv.Number(d.EstimateNextYear)))
            .Prepend(Csv.Row("Drug", $"Stock on 1 January {report.Year}", "Received", "Consumed", "Written off or adjusted", $"Stock on 31 December {report.Year}",
                $"Estimated requirement {report.Year + 1}"));
        return TypedResults.File(Csv.Build(lines), "text/csv; charset=utf-8",
            string.Create(CultureInfo.InvariantCulture, $"ndps-annual-{report.Year}.csv"));
    }

    private static async Task<IResult> PatientRecord(Guid patientId, NdpsService ndps, PatientAccessGuard guard, IAuditLogger audit, CancellationToken ct)
    {
        var allowed = await guard.EnsureCanOpenAsync(patientId, ct);
        if (allowed.IsFailure)
        {
            return allowed.Error!.ToProblem();
        }

        var record = await ndps.PatientAsync(patientId, ct);
        if (record.IsSuccess)
        {
            await audit.PatientRecordAccessedAsync(patientId, "Viewed the narcotic record (Form 3E)", ct);
        }

        return record.ToOk();
    }

    private static async Task<IResult> Counts(Guid? storeId, NdpsService ndps, CancellationToken ct) => TypedResults.Ok(await ndps.CountsAsync(storeId, ct));

    private static async Task<IResult> Stores(NdpsService ndps, CancellationToken ct) => TypedResults.Ok(await ndps.StoresAsync(ct));

    private static async Task<IResult> Sheet(Guid storeId, NdpsService ndps, CancellationToken ct) => (await ndps.SheetAsync(storeId, ct)).ToOk();

    private static async Task<IResult> Count(RecordNarcoticCountRequest request, NdpsService ndps, CancellationToken ct) =>
        (await ndps.CountAsync(request, ct)).ToOk();

    private static async Task<IResult> Witness(Guid id, WitnessNarcoticCountRequest request, NdpsService ndps, CancellationToken ct) =>
        (await ndps.WitnessAsync(id, request, ct)).ToOk();
}
