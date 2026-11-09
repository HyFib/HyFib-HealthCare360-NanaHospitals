using HyFib.HealthCare360.Modules.HR.Domain.Statutory;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.Modules.HR.Features.Statutory;

/// <summary>
/// /api/v1/hr/statutory: the employer's statutory details, TDS challans, tax declarations and computation, the ECR, ESIC,
/// Form 24Q and bank files, Form 16 Part B, gratuity, bonus and full and final settlement.
/// </summary>
internal static class StatutoryEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr/statutory").WithTags("HR statutory");
        string[] viewers = [HRPermissions.PayrollProcess, HRPermissions.PayrollApprove];

        group.MapGet("/profile", async (StatutoryService s, CancellationToken ct) => TypedResults.Ok(await s.ProfileAsync(ct))).RequireAnyPermission(viewers);
        group.MapPut("/profile", async (SaveStatutoryProfileRequest r, StatutoryService s, CancellationToken ct) => (await s.SaveProfileAsync(r, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollApprove).WithValidation<SaveStatutoryProfileRequest>().FirstSaveCreates();

        group.MapGet("/challans", async (TdsForm? form, int? financialYear, StatutoryService s, CancellationToken ct) =>
            TypedResults.Ok(await s.ChallansAsync(form, financialYear, ct))).RequireAnyPermission(viewers);
        group.MapPost("/challans", async (SaveTdsChallanRequest r, StatutoryService s, CancellationToken ct) =>
                (await s.AddChallanAsync(r, ct)).ToCreated(id => $"/api/v1/hr/statutory/challans/{id}"))
            .RequirePermission(HRPermissions.PayrollApprove).WithValidation<SaveTdsChallanRequest>();
        group.MapDelete("/challans/{id:guid}", async (Guid id, StatutoryService s, CancellationToken ct) => (await s.RemoveChallanAsync(id, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollApprove);

        group.MapGet("/employees/{employeeId:guid}/tax-declarations/{financialYear:int}", async (Guid employeeId, int financialYear, StatutoryService s, CancellationToken ct) =>
            TypedResults.Ok(await s.DeclarationAsync(employeeId, financialYear, ct))).RequireAnyPermission(viewers);
        group.MapPut("/employees/{employeeId:guid}/tax-declarations/{financialYear:int}",
                async (Guid employeeId, int financialYear, SaveTaxDeclarationRequest r, StatutoryService s, CancellationToken ct) =>
                    (await s.SaveDeclarationAsync(employeeId, financialYear, r, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollProcess).WithValidation<SaveTaxDeclarationRequest>().FirstSaveCreates();
        group.MapGet("/employees/{employeeId:guid}/tax-computation/{financialYear:int}", async (Guid employeeId, int financialYear, StatutoryService s, CancellationToken ct) =>
            (await s.ComputationAsync(employeeId, financialYear, ct)).ToOk()).RequireAnyPermission(viewers);
        group.MapGet("/employees/{employeeId:guid}/form16/{financialYear:int}", async (Guid employeeId, int financialYear, StatutoryService s, CancellationToken ct) =>
            Ok(await s.Form16Async(employeeId, financialYear, ct), d => d.ToDto())).RequirePermission(HRPermissions.PayrollApprove);
        group.MapGet("/employees/{employeeId:guid}/form16/{financialYear:int}/pdf",
                async (Guid employeeId, int financialYear, StatutoryService s, IPdfRenderer pdf, CancellationToken ct) =>
                {
                    var document = await s.Form16Async(employeeId, financialYear, ct);
                    return document.IsFailure ? document.Error!.ToProblem() : await document.Value.ToPdfAsync(pdf, ct);
                })
            .RequirePermission(HRPermissions.PayrollApprove);

        group.MapGet("/employees/{employeeId:guid}/gratuity", async (Guid employeeId, DateOnly? asOf, StatutoryService s, CancellationToken ct) =>
            (await s.GratuityAsync(employeeId, asOf, ct)).ToOk()).RequireAnyPermission(viewers);
        group.MapPost("/employees/{employeeId:guid}/full-and-final", async (Guid employeeId, FullAndFinalRequest r, StatutoryService s, CancellationToken ct) =>
                (await s.FullAndFinalAsync(employeeId, r, ct)).ToOk())
            .RequirePermission(HRPermissions.PayrollProcess).WithValidation<FullAndFinalRequest>();

        // Settling pays the statement out: the leave it encashes is debited from the balances, once, by someone who approves
        // payroll and is not the employee.
        group.MapPost("/employees/{employeeId:guid}/full-and-final/settle", async (Guid employeeId, FullAndFinalRequest r, StatutoryService s, CancellationToken ct) =>
                (await s.SettleFullAndFinalAsync(employeeId, r, ct)).ToOk())
            .RequirePermission(HRPermissions.PayrollApprove).WithValidation<FullAndFinalRequest>();
        group.MapGet("/bonus/{financialYear:int}", async (int financialYear, decimal? ratePercent, decimal? minimumWage, StatutoryService s, CancellationToken ct) =>
            (await s.BonusRegisterAsync(financialYear, ratePercent ?? StatutoryBonus.MinimumRate, minimumWage ?? 0, ct)).ToOk()).RequireAnyPermission(viewers);

        group.MapGet("/runs/{runId:guid}/ecr", async (Guid runId, StatutoryService s, CancellationToken ct) =>
            Ok(await s.EcrAsync(runId, ct), f => new { f.Ecr.Lines, f.Ecr.Problems, f.Ecr.TotalEpf, f.Ecr.TotalEps, f.Ecr.TotalDifference }))
            .RequirePermission(HRPermissions.PayrollApprove);
        group.MapGet("/runs/{runId:guid}/ecr.txt", async (Guid runId, StatutoryService s, CancellationToken ct) =>
            File(await s.EcrAsync(runId, ct), f => (f.FileName, f.Content), "text/plain; charset=utf-8")).RequirePermission(HRPermissions.PayrollApprove);
        group.MapGet("/runs/{runId:guid}/esic", async (Guid runId, StatutoryService s, CancellationToken ct) =>
            Ok(await s.EsicAsync(runId, ct), f => new { f.Esic.Lines, f.Esic.Problems, f.Esic.TotalWages, f.Esic.TotalEmployee, f.Esic.TotalEmployer }))
            .RequirePermission(HRPermissions.PayrollApprove);
        group.MapGet("/runs/{runId:guid}/esic.csv", async (Guid runId, StatutoryService s, CancellationToken ct) =>
            File(await s.EsicAsync(runId, ct), f => (f.FileName, f.Content), "text/csv; charset=utf-8")).RequirePermission(HRPermissions.PayrollApprove);
        group.MapGet("/runs/{runId:guid}/bank-file/{format}", async (Guid runId, BankFileFormat format, StatutoryService s, CancellationToken ct) =>
            File(await s.BankUploadAsync(runId, format, ct), f => f, format == BankFileFormat.Sbi ? "text/plain; charset=utf-8" : "text/csv; charset=utf-8"))
            .RequirePermission(HRPermissions.PayrollApprove);

        group.MapGet("/24q/{financialYear:int}/{quarter:int}", async (int financialYear, int quarter, StatutoryService s, CancellationToken ct) =>
                Ok(await s.Form24QAsync(financialYear, Math.Clamp(quarter, 1, 4), ct), f => new { f.Return.Challans, f.Return.Deductees, f.Return.TotalDeposited, f.Return.TotalDeducted, f.Return.Problems }))
            .RequirePermission(HRPermissions.PayrollApprove);
        group.MapGet("/24q/{financialYear:int}/{quarter:int}/file", async (int financialYear, int quarter, StatutoryService s, CancellationToken ct) =>
            {
                var result = await s.Form24QAsync(financialYear, Math.Clamp(quarter, 1, 4), ct);
                return result.IsSuccess && result.Value.Return.Problems.Count > 0
                    ? Error.Rule("tds_return.problems", "Correct these before making the file: " + string.Join(" ", result.Value.Return.Problems)).ToProblem()
                    : File(result, f => (f.FileName, f.Content), "text/plain; charset=utf-8");
            })
            .RequirePermission(HRPermissions.PayrollApprove);

        var me = app.MapGroup("/api/v1/hr/me").WithTags("HR self-service").RequirePermission(HRPermissions.Self);
        me.MapGet("/tax-declarations/{financialYear:int}", async (int financialYear, CurrentEmployee current, StatutoryService s, CancellationToken ct) =>
        {
            var id = await current.IdAsync(ct);
            return id.IsFailure ? id.Error!.ToProblem() : TypedResults.Ok(await s.DeclarationAsync(id.Value, financialYear, ct));
        });
        me.MapPut("/tax-declarations/{financialYear:int}", async (int financialYear, SaveTaxDeclarationRequest r, CurrentEmployee current, StatutoryService s, CancellationToken ct) =>
            {
                var id = await current.IdAsync(ct);
                return id.IsFailure ? id.Error!.ToProblem() : (await s.SaveDeclarationAsync(id.Value, financialYear, r, ct)).ToNoContent();
            })
            .WithValidation<SaveTaxDeclarationRequest>().FirstSaveCreates();
        me.MapGet("/tax-computation/{financialYear:int}", async (int financialYear, CurrentEmployee current, StatutoryService s, CancellationToken ct) =>
        {
            var id = await current.IdAsync(ct);
            return id.IsFailure ? id.Error!.ToProblem() : (await s.ComputationAsync(id.Value, financialYear, ct)).ToOk();
        });
    }

    private static IResult Ok<T, TValue>(Result<T> result, Func<T, TValue> map) =>
        result.IsFailure ? result.Error!.ToProblem() : TypedResults.Ok(map(result.Value));

    private static IResult File<T>(Result<T> result, Func<T, (string FileName, byte[] Content)> file, string contentType)
    {
        if (result.IsFailure)
        {
            return result.Error!.ToProblem();
        }

        var (name, content) = file(result.Value);
        return TypedResults.File(content, contentType, name);
    }
}
