using System.Text;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Monthly payroll (HR-06/07): creates the month's run, computes every payslip from the salary structure in force,
/// attendance-based loss of pay, one-off inputs and statutory deductions, and drives the maker-checker workflow
/// (process → approve → paid → locked). Locking also freezes the month's attendance.
/// </summary>
public sealed class PayrollService(
    HospitalDbContext db,
    ICurrentUser currentUser,
    IFieldProtector protector,
    IAuditLogger audit,
    INotificationService notifications,
    TimeProvider time)
{
    public async Task<Result<Guid>> CreateRunAsync(CreatePayrollRunRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var today = time.LocalToday();
        if (new DateOnly(request.Year, request.Month, 1) > new DateOnly(today.Year, today.Month, 1))
        {
            return Error.Rule("payroll.future_month", "Payroll cannot be created for a future month.");
        }

        if (await db.Set<PayrollRun>().AnyAsync(r => r.Year == request.Year && r.Month == request.Month, ct))
        {
            return Error.Conflict("payroll.exists", "A payroll for this month already exists.");
        }

        var run = new PayrollRun
        {
            FacilityId = facilityId,
            Year = request.Year,
            Month = request.Month,
            Status = PayrollRunStatus.Draft,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
        };
        db.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    public async Task<Result<PayrollProcessResult>> ProcessAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), runId);
        }

        if (!run.IsEditable)
        {
            return Error.Rule("payroll.not_editable", "An approved or paid payroll cannot be processed again. Reopen it first.");
        }

        var context = await LoadContextAsync(run, ct);
        var messages = new List<string>();
        var processed = new List<Payslip>();
        var skipped = 0;

        foreach (var employee in context.Employees)
        {
            var structure = context.Structures.GetValueOrDefault(employee.Id);
            if (structure is null)
            {
                messages.Add($"{employee.EmployeeCode} {employee.FullName}: no salary structure in force this month — not paid.");
                skipped++;
                continue;
            }

            var payslip = context.Existing.GetValueOrDefault(employee.Id);
            if (payslip is null)
            {
                payslip = new Payslip { FacilityId = run.FacilityId, PayrollRunId = run.Id, EmployeeId = employee.Id, Year = run.Year, Month = run.Month };
                db.Add(payslip);
            }

            Compute(run, employee, structure, context, payslip);
            processed.Add(payslip);
        }

        foreach (var stale in context.Existing.Values.Where(p => !processed.Contains(p)))
        {
            db.Remove(stale);
        }

        var withErrors = processed.Count(p => p.HasErrors);
        var result = run.MarkProcessed(currentUser.UserId, time.GetUtcNow(), processed.Count, processed.Sum(p => p.GrossEarnings),
            processed.Sum(p => p.TotalDeductions), processed.Sum(p => p.NetPay));
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return new PayrollProcessResult(processed.Count, skipped, withErrors, messages);
    }

    public async Task<Result> ApproveAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), runId);
        }

        if (currentUser.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "Sign in first.");
        }

        var errors = await db.Set<Payslip>().CountAsync(p => p.PayrollRunId == runId && p.HasErrors, ct);
        if (errors > 0)
        {
            return Error.Rule("payroll.has_errors", $"{errors} payslip(s) have errors. Correct the inputs and process again before approving.");
        }

        var result = run.Approve(userId, time.GetUtcNow());
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    public Task<Result> ReopenAsync(Guid runId, string reason, CancellationToken ct) =>
        ChangeAsync(runId, run => run.Reopen(reason), ct);

    public async Task<Result> MarkPaidAsync(Guid runId, MarkPayrollPaidRequest request, CancellationToken ct)
    {
        var result = await ChangeAsync(runId, run => run.MarkPaid(request.PaidOn, request.Reference), ct);
        if (result.IsFailure)
        {
            return result;
        }

        var run = await db.Set<PayrollRun>().AsNoTracking().FirstAsync(r => r.Id == runId, ct);
        var userIds = await (
                from p in db.Set<Payslip>()
                join e in db.Set<Employee>() on p.EmployeeId equals e.Id
                where p.PayrollRunId == runId && e.UserId != null
                select e.UserId!.Value)
            .ToListAsync(ct);
        var period = new DateOnly(run.Year, run.Month, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        await notifications.NotifyUsersAsync(userIds, $"Salary for {period} credited", "Your payslip is available under My HR → Payslips.",
            "/hr/me/payslips", "hr", "info", ct);
        return Result.Success();
    }

    public async Task<Result> LockAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), runId);
        }

        var result = run.Lock(time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        var (start, end) = (run.PeriodStart, run.PeriodEnd);
        await db.Set<AttendanceDay>()
            .Where(a => a.FacilityId == run.FacilityId && a.Date >= start && a.Date <= end)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsLocked, true), ct);
        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public async Task<Result> DeleteAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), runId);
        }

        if (!run.IsEditable)
        {
            return Error.Rule("payroll.not_editable", "Only draft or processed payrolls can be deleted.");
        }

        db.RemoveRange(await db.Set<Payslip>().Where(p => p.PayrollRunId == runId).ToListAsync(ct));
        db.RemoveRange(await db.Set<PayrollInput>().Where(i => i.PayrollRunId == runId).ToListAsync(ct));
        db.Remove(run);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>
    /// Builds the salary bulk-transfer CSV. It holds decrypted account numbers, so it is produced only for approved
    /// runs, never stored, and every export is written to the audit trail (PRV-04).
    /// </summary>
    public async Task<Result<(string FileName, byte[] Content)>> BankTransferFileAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), runId);
        }

        if (run.Status is PayrollRunStatus.Draft or PayrollRunStatus.Processed)
        {
            return Error.Rule("payroll.not_approved", "The bank file can be generated only after the payroll is approved.");
        }

        var payees = await (
                from p in db.Set<Payslip>().AsNoTracking()
                join e in db.Set<Employee>() on p.EmployeeId equals e.Id
                where p.PayrollRunId == runId && p.NetPay > 0 && e.PaymentMode == SalaryPaymentMode.BankTransfer
                      && e.BankAccountCipher != null && e.BankIfscCipher != null
                orderby p.EmployeeCode
                select new { p.EmployeeCode, p.EmployeeName, p.NetPay, e.BankAccountCipher, e.BankIfscCipher })
            .ToListAsync(ct);

        var narration = BankTransferFile.Narration(run.Year, run.Month);
        var rows = payees.Select(p => new BankTransferRow(p.EmployeeCode, p.EmployeeName, protector.Unprotect(p.BankAccountCipher) ?? string.Empty,
            protector.Unprotect(p.BankIfscCipher) ?? string.Empty, p.NetPay, narration)).ToList();
        var csv = BankTransferFile.BuildCsv(rows);

        await audit.WriteAsync(new AuditEntry(AuditCategories.Security, "payroll.bank_file_exported",
            $"Salary bank file for {run.Month:00}/{run.Year} exported: {rows.Count} payee(s), ₹{Money.FormatInr(rows.Sum(r => r.Amount))}")
        {
            EntityType = nameof(PayrollRun),
            EntityId = run.Id.ToString(),
            Severity = AuditSeverity.Warning,
        }, ct);

        return ($"salary-transfer-{run.Year}-{run.Month:00}.csv", Encoding.UTF8.GetBytes(csv));
    }

    private sealed record PayrollContext(
        IReadOnlyList<Employee> Employees,
        IReadOnlyDictionary<Guid, EmployeeSalaryStructure> Structures,
        IReadOnlyDictionary<Guid, SalaryComponent> Components,
        IReadOnlyDictionary<string, ProfessionalTaxSchedule> ProfessionalTax,
        ILookup<Guid, AttendanceLopDay> Attendance,
        ILookup<Guid, PayrollInput> Inputs,
        IReadOnlyDictionary<Guid, decimal> EarlierHalfYearGross,
        IReadOnlyDictionary<Guid, Payslip> Existing,
        IReadOnlyDictionary<Guid, string> Departments,
        IReadOnlyDictionary<Guid, string> Designations);

    private async Task<PayrollContext> LoadContextAsync(PayrollRun run, CancellationToken ct)
    {
        var (start, end) = (run.PeriodStart, run.PeriodEnd);
        var employees = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.FacilityId == run.FacilityId && e.JoiningDate <= end && (e.LeavingDate == null || e.LeavingDate >= start))
            .OrderBy(e => e.EmployeeCode)
            .ToListAsync(ct);
        var ids = employees.Select(e => e.Id).ToList();

        var structures = (await db.Set<EmployeeSalaryStructure>().AsNoTracking().Include(s => s.Lines)
                .Where(s => ids.Contains(s.EmployeeId) && s.EffectiveFrom <= end && (s.EffectiveTo == null || s.EffectiveTo >= start))
                .ToListAsync(ct))
            .GroupBy(s => s.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(s => s.EffectiveFrom).First());

        var components = await db.Set<SalaryComponent>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var professionalTax = (await db.Set<ProfessionalTaxSlab>().AsNoTracking().ToListAsync(ct))
            .GroupBy(s => s.StateCode)
            .ToDictionary(g => g.Key, g => ProfessionalTaxSchedule.From(g.ToList())!, StringComparer.OrdinalIgnoreCase);

        var attendance = await db.Set<AttendanceDay>().AsNoTracking()
            .Where(a => ids.Contains(a.EmployeeId) && a.Date >= start && a.Date <= end)
            .Select(a => new { a.EmployeeId, Day = new AttendanceLopDay(a.Date, a.AbsentDays, a.UnpaidLeaveDays) })
            .ToListAsync(ct);

        // Attendance-exempt staff are not computed daily, so their unpaid (LOP) leave comes straight from approved requests.
        var exemptIds = employees.Where(e => e.IsAttendanceExempt).Select(e => e.Id).ToList();
        var unpaidTypes = await db.Set<LeaveType>().AsNoTracking().Where(t => !t.IsPaid).Select(t => t.Id).ToListAsync(ct);
        var exemptLeave = await db.Set<LeaveRequest>().AsNoTracking()
            .Where(l => exemptIds.Contains(l.EmployeeId) && unpaidTypes.Contains(l.LeaveTypeId) && l.Status == ApprovalStatus.Approved
                        && l.FromDate <= end && l.ToDate >= start)
            .ToListAsync(ct);
        var exemptDays = exemptLeave.SelectMany(l => Enumerable.Range(0, l.ToDate.DayNumber - l.FromDate.DayNumber + 1)
                .Select(offset => l.FromDate.AddDays(offset))
                .Where(date => date >= start && date <= end)
                .Select(date => new { l.EmployeeId, Day = new AttendanceLopDay(date, 0, l.PortionOn(date)) }));

        var inputs = await db.Set<PayrollInput>().AsNoTracking().Where(i => i.PayrollRunId == run.Id).OrderBy(i => i.CreatedAt).ToListAsync(ct);

        var halfYear = ProfessionalTaxCalculator.HalfYearMonths(run.Year, run.Month);
        var firstKey = (halfYear[0].Year * 100) + halfYear[0].Month;
        var currentKey = (run.Year * 100) + run.Month;
        var earlier = await db.Set<Payslip>().AsNoTracking()
            .Where(p => ids.Contains(p.EmployeeId) && (p.Year * 100) + p.Month >= firstKey && (p.Year * 100) + p.Month < currentKey)
            .GroupBy(p => p.EmployeeId)
            .Select(g => new { EmployeeId = g.Key, Gross = g.Sum(p => p.GrossEarnings) })
            .ToDictionaryAsync(g => g.EmployeeId, g => g.Gross, ct);

        var existing = await db.Set<Payslip>().Include(p => p.Lines).Where(p => p.PayrollRunId == run.Id).ToDictionaryAsync(p => p.EmployeeId, ct);
        var departments = await db.Set<Department>().AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var designations = await db.Set<Designation>().AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return new PayrollContext(
            employees,
            structures,
            components,
            professionalTax,
            attendance.Concat(exemptDays).ToLookup(a => a.EmployeeId, a => a.Day),
            inputs.ToLookup(i => i.EmployeeId),
            earlier,
            existing,
            departments,
            designations);
    }

    private static void Compute(PayrollRun run, Employee employee, EmployeeSalaryStructure structure, PayrollContext context, Payslip payslip)
    {
        var inputs = context.Inputs[employee.Id].ToList();
        var lopOverride = inputs.LastOrDefault(i => i.Kind == PayrollInputKind.LopDaysOverride)?.Amount;
        var tdsOverride = inputs.LastOrDefault(i => i.Kind == PayrollInputKind.TdsOverride)?.Amount;
        var lop = LopCalculator.Compute(run.Year, run.Month, employee.JoiningDate, employee.LeavingDate, employee.IsAttendanceExempt,
            context.Attendance[employee.Id].ToList(), lopOverride);

        var lines = structure.Lines
            .Where(l => context.Components.ContainsKey(l.SalaryComponentId))
            .Select(l =>
            {
                var component = context.Components[l.SalaryComponentId];
                return new SalaryLineInput(component.Code, component.Name, component.Kind, component.CalculationType, l.Value, component.SortOrder, component.WageTreatment);
            })
            .ToList();
        var adjustments = inputs
            .Where(i => i.Kind is PayrollInputKind.Earning or PayrollInputKind.Deduction)
            .Select(i => new PayrollAdjustmentInput(i.ComponentCode, i.Description, i.Kind == PayrollInputKind.Earning ? SalaryComponentKind.Earning : SalaryComponentKind.Deduction, i.Amount,
                context.Components.Values.FirstOrDefault(c => c.Code == i.ComponentCode)?.WageTreatment ?? WageTreatment.Wages))
            .ToList();

        var schedule = structure.ProfessionalTaxState is { } state ? context.ProfessionalTax.GetValueOrDefault(state) : null;
        decimal? earlierHalfYear = schedule?.Frequency == ProfessionalTaxFrequency.HalfYearly && context.EarlierHalfYearGross.TryGetValue(employee.Id, out var earlier)
            ? earlier
            : null;

        var result = PayrollCalculator.Calculate(new PayrollCalculationInput(
            run.Year, run.Month, lop.DaysInMonth, lop.PayableDays, lines, adjustments,
            new StatutoryProfile(structure.PfApplicable, structure.PfOnActualBasic, structure.EsiApplicable, schedule),
            structure.MonthlyTds, tdsOverride, earlierHalfYear));

        var warnings = result.Warnings.ToList();
        if (lop.MissingAttendanceDays > 0)
        {
            warnings.Add($"{lop.MissingAttendanceDays} day(s) without computed attendance were treated as absent.");
        }

        if (employee.PaymentMode == SalaryPaymentMode.BankTransfer && employee.BankAccountCipher is null)
        {
            warnings.Add("Paid by bank transfer but no bank account is on record.");
        }

        payslip.EmployeeCode = employee.EmployeeCode;
        payslip.EmployeeName = employee.FullName;
        payslip.DesignationName = employee.DesignationId is { } designationId ? context.Designations.GetValueOrDefault(designationId) : null;
        payslip.DepartmentName = employee.DepartmentId is { } departmentId ? context.Departments.GetValueOrDefault(departmentId) : null;
        payslip.DaysInMonth = lop.DaysInMonth;
        payslip.PayableDays = lop.PayableDays;
        payslip.LopDays = lop.LopDays;
        payslip.MissingAttendanceDays = lop.MissingAttendanceDays;
        payslip.StandardGross = result.StandardGross;
        payslip.GrossEarnings = result.GrossEarnings;
        payslip.TotalDeductions = result.TotalDeductions;
        payslip.NetPay = result.NetPay;
        payslip.PfWage = result.PfWage;
        payslip.EmployeePf = result.EmployeePf;
        payslip.EmployerPf = result.EmployerPf;
        payslip.EsiWage = result.EsiWage;
        payslip.EmployeeEsi = result.EmployeeEsi;
        payslip.EmployerEsi = result.EmployerEsi;
        payslip.ProfessionalTax = result.ProfessionalTax;
        payslip.Tds = result.Tds;
        payslip.PaymentMode = employee.PaymentMode;
        payslip.BankAccountMasked = employee.BankAccountMasked;
        payslip.Warnings = warnings;
        payslip.HasErrors = result.HasErrors;
        payslip.Lines.Clear();
        payslip.Lines.AddRange(result.Lines.Select(l => new PayslipLine
        {
            PayslipId = payslip.Id,
            Code = l.Code,
            Name = l.Name,
            Kind = l.Kind,
            FullAmount = l.FullAmount,
            Amount = l.Amount,
            IsStatutory = l.IsStatutory,
            SortOrder = l.SortOrder,
        }));
    }

    private async Task<Result> ChangeAsync(Guid runId, Func<PayrollRun, Result> change, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), runId);
        }

        var result = change(run);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}
