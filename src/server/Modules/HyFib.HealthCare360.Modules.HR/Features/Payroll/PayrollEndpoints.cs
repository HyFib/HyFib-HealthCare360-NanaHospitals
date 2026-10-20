using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Organization;

namespace HyFib.HealthCare360.Modules.HR.Features.Payroll;

/// <summary>/api/v1/hr/payroll — monthly runs, inputs, payslips and the bank transfer file (HR-06/07).</summary>
internal static class PayrollEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/hr/payroll").WithTags("HR");
        string[] viewers = [HRPermissions.PayrollProcess, HRPermissions.PayrollApprove];

        group.MapGet("/runs", ListRuns).RequireAnyPermission(viewers);
        group.MapGet("/runs/{id:guid}", GetRun).RequireAnyPermission(viewers);
        group.MapPost("/runs", async (CreatePayrollRunRequest r, PayrollService payroll, CancellationToken ct) =>
                (await payroll.CreateRunAsync(r, ct)).ToCreated(id => $"/api/v1/hr/payroll/runs/{id}"))
            .RequirePermission(HRPermissions.PayrollProcess).WithValidation<CreatePayrollRunRequest>();
        group.MapDelete("/runs/{id:guid}", async (Guid id, PayrollService payroll, CancellationToken ct) => (await payroll.DeleteAsync(id, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollProcess);
        group.MapPost("/runs/{id:guid}/process", async (Guid id, PayrollService payroll, CancellationToken ct) => (await payroll.ProcessAsync(id, ct)).ToOk())
            .RequirePermission(HRPermissions.PayrollProcess);
        group.MapPost("/runs/{id:guid}/approve", async (Guid id, PayrollService payroll, CancellationToken ct) => (await payroll.ApproveAsync(id, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollApprove);
        group.MapPost("/runs/{id:guid}/reopen", async (Guid id, ReopenPayrollRequest r, PayrollService payroll, CancellationToken ct) =>
                (await payroll.ReopenAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollApprove).WithValidation<ReopenPayrollRequest>();
        group.MapPost("/runs/{id:guid}/mark-paid", async (Guid id, MarkPayrollPaidRequest r, PayrollService payroll, CancellationToken ct) =>
                (await payroll.MarkPaidAsync(id, r, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollApprove).WithValidation<MarkPayrollPaidRequest>();
        group.MapPost("/runs/{id:guid}/lock", async (Guid id, PayrollService payroll, CancellationToken ct) => (await payroll.LockAsync(id, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollApprove);
        group.MapGet("/runs/{id:guid}/bank-transfer.csv", BankFile).RequirePermission(HRPermissions.PayrollApprove);

        group.MapGet("/runs/{id:guid}/inputs", ListInputs).RequireAnyPermission(viewers);
        group.MapPost("/runs/{id:guid}/inputs", async (Guid id, SavePayrollInputRequest r, PayrollInputService inputs, CancellationToken ct) =>
                (await inputs.AddAsync(id, r, ct)).ToCreated(inputId => $"/api/v1/hr/payroll/inputs/{inputId}"))
            .RequirePermission(HRPermissions.PayrollProcess).WithValidation<SavePayrollInputRequest>();
        group.MapPost("/runs/{id:guid}/inputs/import", async (Guid id, ImportPayrollInputsRequest r, PayrollInputService inputs, CancellationToken ct) =>
                (await inputs.ImportAsync(id, r.Csv, ct)).ToOk())
            .RequirePermission(HRPermissions.PayrollProcess).WithValidation<ImportPayrollInputsRequest>();
        group.MapDelete("/inputs/{inputId:guid}", async (Guid inputId, PayrollInputService inputs, CancellationToken ct) => (await inputs.RemoveAsync(inputId, ct)).ToNoContent())
            .RequirePermission(HRPermissions.PayrollProcess);

        group.MapGet("/runs/{id:guid}/payslips", ListPayslips).RequireAnyPermission(viewers);
        group.MapGet("/payslips/{id:guid}", GetPayslip).RequireAnyPermission(viewers);
    }

    private static async Task<IResult> ListRuns([AsParameters] PageQuery query, int? year, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var runs = db.Set<PayrollRun>().AsNoTracking().Where(r => year == null || r.Year == year);
        var result = await runs.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .Select(r => new { Run = r, Errors = db.Set<Payslip>().Count(p => p.PayrollRunId == r.Id && p.HasErrors) })
            .ToPagedResultAsync(page, ct);
        return TypedResults.Ok(result.Map(x => ToDto(x.Run, x.Errors)));
    }

    private static async Task<IResult> GetRun(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), id).ToProblem();
        }

        var errors = await db.Set<Payslip>().CountAsync(p => p.PayrollRunId == id && p.HasErrors, ct);
        return TypedResults.Ok(ToDto(run, errors));
    }

    private static async Task<IResult> BankFile(Guid id, PayrollService payroll, CancellationToken ct)
    {
        var result = await payroll.BankTransferFileAsync(id, ct);
        return result.IsFailure ? result.Error!.ToProblem() : TypedResults.File(result.Value.Content, "text/csv; charset=utf-8", result.Value.FileName);
    }

    private static async Task<IResult> ListInputs(Guid id, HospitalDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await (
                from i in db.Set<PayrollInput>().AsNoTracking()
                join e in db.Set<Employee>() on i.EmployeeId equals e.Id
                where i.PayrollRunId == id
                orderby e.EmployeeCode, i.CreatedAt
                select new PayrollInputDto(i.Id, i.EmployeeId, e.EmployeeCode, e.FullName, i.Kind, i.ComponentCode, i.Description, i.Amount, i.Source, i.SourceReference))
            .ToListAsync(ct));

    private static async Task<IResult> ListPayslips(Guid id, [AsParameters] PageQuery query, bool? errorsOnly, HospitalDbContext db, CancellationToken ct)
    {
        var page = query.ToRequest();
        var payslips = db.Set<Payslip>().AsNoTracking().Where(p => p.PayrollRunId == id && (errorsOnly != true || p.HasErrors));
        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            payslips = payslips.Where(p => EF.Functions.ILike(p.EmployeeName, like, QueryExtensions.LikeEscape) || p.EmployeeCode == term.ToUpper());
        }

        return TypedResults.Ok(await payslips.OrderBy(p => p.EmployeeCode)
            .Select(p => new PayslipListItem(p.Id, p.EmployeeId, p.EmployeeCode, p.EmployeeName, p.DepartmentName, p.PayableDays, p.LopDays,
                p.GrossEarnings, p.TotalDeductions, p.NetPay, p.HasErrors))
            .ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> GetPayslip(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var payslip = await LoadPayslipAsync(db, p => p.Id == id, visibleToEmployeeOnly: false, ct);
        return payslip is null ? Error.NotFound(nameof(Payslip), id).ToProblem() : TypedResults.Ok(payslip);
    }

    /// <summary>Shared by the payroll screens and self-service (which sees only approved months).</summary>
    internal static async Task<PayslipDto?> LoadPayslipAsync(
        HospitalDbContext db,
        System.Linq.Expressions.Expression<Func<Payslip, bool>> filter,
        bool visibleToEmployeeOnly,
        CancellationToken ct)
    {
        var payslips = visibleToEmployeeOnly
            ? db.Set<Payslip>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            : db.Set<Payslip>();
        var row = await (
                from p in payslips.AsNoTracking().Where(filter)
                join r in db.Set<PayrollRun>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.PayrollRunId equals r.Id
                join e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.EmployeeId equals e.Id
                join f in db.Set<Facility>() on p.FacilityId equals f.Id into facilities
                from f in facilities.DefaultIfEmpty()
                where !visibleToEmployeeOnly || r.Status == PayrollRunStatus.Approved || r.Status == PayrollRunStatus.Paid || r.Status == PayrollRunStatus.Locked
                select new { Payslip = p, r.Status, e.PanMasked, e.Uan, FacilityName = f != null ? f.Name : null })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var slip = row.Payslip;
        var lines = await db.Set<PayslipLine>().AsNoTracking()
            .Where(l => l.PayslipId == slip.Id)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Code)
            .ToListAsync(ct);
        PayslipLineDto ToLine(PayslipLine l) => new(l.Code, l.Name, l.FullAmount, l.Amount, l.IsStatutory);
        return new PayslipDto(
            slip.Id, slip.PayrollRunId, row.Status, slip.Year, slip.Month, row.FacilityName, slip.EmployeeId, slip.EmployeeCode, slip.EmployeeName,
            slip.DesignationName, slip.DepartmentName, row.PanMasked, row.Uan, slip.DaysInMonth, slip.PayableDays, slip.LopDays, slip.MissingAttendanceDays,
            lines.Where(l => l.Kind == SalaryComponentKind.Earning).Select(ToLine).ToList(),
            lines.Where(l => l.Kind == SalaryComponentKind.Deduction).Select(ToLine).ToList(),
            slip.GrossEarnings, slip.TotalDeductions, slip.NetPay, slip.PfWage, slip.EmployerPf, slip.EsiWage, slip.EmployerEsi, slip.PaymentMode,
            slip.BankAccountMasked, slip.Warnings, slip.HasErrors);
    }

    private static PayrollRunDto ToDto(PayrollRun r, int errors) => new(
        r.Id, r.Year, r.Month, r.Status, r.EmployeeCount, r.TotalGross, r.TotalDeductions, r.TotalNet, r.ProcessedAt, r.ApprovedAt, r.PaidOn,
        r.PaymentReference, r.LockedAt, r.Notes, errors, r.Version);
}
