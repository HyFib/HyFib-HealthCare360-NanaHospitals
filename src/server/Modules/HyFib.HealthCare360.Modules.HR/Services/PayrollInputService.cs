using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Variable pay items for a payroll month: entered by payroll staff, imported in bulk (CSV), or posted by other
/// modules — for example the canteen's staff credit (CAN-03) — through <see cref="PostDeductionAsync"/>, which is
/// idempotent on the caller's reference so a retried post never deducts twice.
/// </summary>
public sealed class PayrollInputService(HospitalDbContext db)
{
    public async Task<Result<Guid>> AddAsync(Guid runId, SavePayrollInputRequest request, CancellationToken ct)
    {
        var run = await EditableRunAsync(runId, ct);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        var employee = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == request.EmployeeId && e.FacilityId == run.Value.FacilityId)
            .Select(e => new { e.Id }).FirstOrDefaultAsync(ct);
        if (employee is null)
        {
            return Error.Field("employeeId", "Choose an employee of this facility.");
        }

        var code = (request.ComponentCode ?? request.Kind.ToString()).Trim().ToUpperInvariant();
        if (request.Kind is PayrollInputKind.Earning or PayrollInputKind.Deduction)
        {
            var problem = await CheckComponentAsync(code, request.Kind, ct);
            if (problem is not null)
            {
                return problem;
            }
        }

        var input = new PayrollInput
        {
            FacilityId = run.Value.FacilityId,
            PayrollRunId = runId,
            EmployeeId = request.EmployeeId,
            Kind = request.Kind,
            ComponentCode = code,
            Description = request.Description.Trim(),
            Amount = Money.Round(request.Amount),
            Source = PayrollInputSource.Manual,
        };
        db.Add(input);
        await db.SaveChangesAsync(ct);
        return input.Id;
    }

    public async Task<Result> RemoveAsync(Guid inputId, CancellationToken ct)
    {
        var input = await db.Set<PayrollInput>().FirstOrDefaultAsync(i => i.Id == inputId, ct);
        if (input is null)
        {
            return Error.NotFound(nameof(PayrollInput), inputId);
        }

        var run = await EditableRunAsync(input.PayrollRunId, ct);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        db.Remove(input);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>All-or-nothing CSV import: any bad line (format, unknown employee or pay head) rejects the whole file.</summary>
    public async Task<Result<PayrollImportResult>> ImportAsync(Guid runId, string csv, CancellationToken ct)
    {
        var run = await EditableRunAsync(runId, ct);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        var parsed = PayrollInputCsvParser.Parse(csv);
        var errors = parsed.Errors.ToList();

        var codes = parsed.Lines.Select(l => l.EmployeeCode).Distinct().ToList();
        var employees = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.FacilityId == run.Value.FacilityId && codes.Contains(e.EmployeeCode))
            .ToDictionaryAsync(e => e.EmployeeCode, e => e.Id, ct);
        var components = await db.Set<SalaryComponent>().AsNoTracking()
            .Where(c => c.IsActive && !c.IsStatutory)
            .ToDictionaryAsync(c => c.Code, c => c.Kind, ct);

        var inputs = new List<PayrollInput>();
        foreach (var line in parsed.Lines)
        {
            if (!employees.TryGetValue(line.EmployeeCode, out var employeeId))
            {
                errors.Add($"Line {line.LineNumber}: employee {line.EmployeeCode} is not on the rolls of this facility.");
                continue;
            }

            if (!components.TryGetValue(line.ComponentCode, out var kind))
            {
                errors.Add($"Line {line.LineNumber}: unknown pay head {line.ComponentCode}.");
                continue;
            }

            inputs.Add(new PayrollInput
            {
                FacilityId = run.Value.FacilityId,
                PayrollRunId = runId,
                EmployeeId = employeeId,
                Kind = kind == SalaryComponentKind.Earning ? PayrollInputKind.Earning : PayrollInputKind.Deduction,
                ComponentCode = line.ComponentCode,
                Description = line.Description ?? line.ComponentCode,
                Amount = line.Amount,
                Source = PayrollInputSource.Import,
            });
        }

        if (errors.Count > 0)
        {
            return Error.Validation("payroll.import_invalid", "The file has errors; nothing was imported.",
                new Dictionary<string, string[]> { ["csv"] = errors.Take(100).ToArray() });
        }

        db.AddRange(inputs);
        await db.SaveChangesAsync(ct);
        return new PayrollImportResult(inputs.Count);
    }

    /// <summary>
    /// For other modules: records a deduction (for example canteen credit) against the employee's payroll for the
    /// given month, creating the month's draft payroll if needed. Posting the same <paramref name="sourceReference"/>
    /// again returns the existing item.
    /// </summary>
    public async Task<Result<Guid>> PostDeductionAsync(
        Guid employeeId,
        int year,
        int month,
        string componentCode,
        decimal amount,
        string description,
        string sourceReference,
        CancellationToken ct)
    {
        if (amount <= 0 || string.IsNullOrWhiteSpace(sourceReference))
        {
            return Error.Rule("payroll.invalid_deduction", "A deduction needs a positive amount and a source reference.");
        }

        var employee = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(e => e.Id == employeeId).Select(e => new { e.FacilityId }).FirstOrDefaultAsync(ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        var code = componentCode.Trim().ToUpperInvariant();
        var problem = await CheckComponentAsync(code, PayrollInputKind.Deduction, ct);
        if (problem is not null)
        {
            return problem;
        }

        var run = await db.Set<PayrollRun>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(r => r.FacilityId == employee.FacilityId && r.Year == year && r.Month == month, ct);
        if (run is null)
        {
            run = new PayrollRun { FacilityId = employee.FacilityId, Year = year, Month = month, Status = PayrollRunStatus.Draft };
            db.Add(run);
        }
        else if (!run.IsEditable)
        {
            return Error.Rule("payroll.closed", $"Payroll for {month:00}/{year} is already approved; post the deduction to the next month.");
        }

        var reference = sourceReference.Trim();
        var existing = await db.Set<PayrollInput>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(i => i.PayrollRunId == run.Id && i.Source == PayrollInputSource.Integration && i.SourceReference == reference)
            .Select(i => (Guid?)i.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is { } existingId)
        {
            return existingId;
        }

        var input = new PayrollInput
        {
            FacilityId = employee.FacilityId,
            PayrollRunId = run.Id,
            EmployeeId = employeeId,
            Kind = PayrollInputKind.Deduction,
            ComponentCode = code,
            Description = description.Trim(),
            Amount = Money.Round(amount),
            Source = PayrollInputSource.Integration,
            SourceReference = reference,
        };
        db.Add(input);
        await db.SaveChangesAsync(ct);
        return input.Id;
    }

    private async Task<Error?> CheckComponentAsync(string code, PayrollInputKind kind, CancellationToken ct)
    {
        var component = await db.Set<SalaryComponent>().AsNoTracking().FirstOrDefaultAsync(c => c.Code == code && c.IsActive, ct);
        if (component is null || component.IsStatutory)
        {
            return Error.Field("componentCode", $"Choose an active, non-statutory pay head ({code} is not one).");
        }

        var expected = kind == PayrollInputKind.Earning ? SalaryComponentKind.Earning : SalaryComponentKind.Deduction;
        return component.Kind == expected ? null : Error.Field("componentCode", $"{component.Name} is not a {expected.ToString().ToLowerInvariant()}.");
    }

    private async Task<Result<PayrollRun>> EditableRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.NotFound(nameof(PayrollRun), runId);
        }

        return run.IsEditable ? run : Error.Rule("payroll.not_editable", "Inputs can be changed only before the payroll is approved.");
    }
}
