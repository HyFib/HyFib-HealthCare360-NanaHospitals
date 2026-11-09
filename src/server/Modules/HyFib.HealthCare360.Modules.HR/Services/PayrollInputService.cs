using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Platform.Approvals;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>An input added at once (its id), or asked for a second person's approval (the request).</summary>
public sealed record PayrollInputAdded(Guid? Id, ApprovalRequestDto? Approval);

/// <summary>
/// Variable pay items for a payroll month: entered by payroll staff, imported in bulk (CSV), or posted by other
/// modules — for example the canteen's staff credit (CAN-03) — through <see cref="PostDeductionAsync"/>, which is
/// idempotent on the caller's reference so a retried post never deducts twice. What raises pay waits for a second person,
/// and nobody changes their own pay (<see cref="PayApprovals"/>, maker-checker slice 11).
/// </summary>
public sealed class PayrollInputService(HospitalDbContext db, ICurrentUser currentUser, PayApprovals pay)
{
    public async Task<Result<PayrollInputAdded>> AddAsync(Guid runId, SavePayrollInputRequest request, CancellationToken ct)
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

        if (await pay.OwnPayAsync([employee.Id], approving: false, ct) is { } own)
        {
            return own;
        }

        var code = (request.ComponentCode ?? request.Kind.ToString()).Trim().ToUpperInvariant();
        if (request.Kind is PayrollInputKind.Earning or PayrollInputKind.Deduction)
        {
            var problem = await CheckComponentAsync(db, code, request.Kind, ct);
            if (problem is not null)
            {
                return problem;
            }
        }

        if (PayChangeRule.InputWaits(request.Kind))
        {
            var asked = await pay.AskInputAsync(run.Value,
                new PayrollInputPayload(Guid.CreateVersion7(), runId, employee.Id, request.Kind, code, request.Description.Trim(), Money.Round(request.Amount)), ct);
            return asked.IsSuccess ? Result<PayrollInputAdded>.Ok(new PayrollInputAdded(null, asked.Value)) : asked.Error!;
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
        return new PayrollInputAdded(input.Id, null);
    }

    /// <summary>Removes an input at once (null), or asks for a deduction's removal (the request: <see cref="PayChangeRule.RemovalWaits"/>).</summary>
    public async Task<Result<ApprovalRequestDto?>> RemoveAsync(Guid inputId, CancellationToken ct)
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

        if (await pay.OwnPayAsync([input.EmployeeId], approving: false, ct) is { } own)
        {
            return own;
        }

        if (PayChangeRule.RemovalWaits(input.Kind, input.Source, input.CreatedBy, currentUser.UserId))
        {
            var asked = await pay.AskRemovalAsync(run.Value, input, ct);
            return asked.IsSuccess ? Result<ApprovalRequestDto?>.Ok(asked.Value) : asked.Error!;
        }

        db.Remove(input);
        await db.SaveChangesAsync(ct);
        return (ApprovalRequestDto?)null;
    }

    /// <summary>
    /// All-or-nothing CSV import: any bad line (format, unknown employee or pay head) rejects the whole file, and so does a
    /// line for the importer's own pay. A file of deductions only is imported at once; one with an earning in it waits, as a
    /// whole, for a second person (its result then carries the request).
    /// </summary>
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

        if (await pay.OwnPayAsync([.. inputs.Select(i => i.EmployeeId).Distinct()], approving: false, ct) is { } own)
        {
            return own;
        }

        if (inputs.Any(i => PayChangeRule.InputWaits(i.Kind)))
        {
            var asked = await pay.AskImportAsync(run.Value,
                [.. inputs.Select(i => new PayrollInputPayload(i.Id, runId, i.EmployeeId, i.Kind, i.ComponentCode, i.Description, i.Amount))], ct);
            return asked.IsSuccess ? Result<PayrollImportResult>.Ok(new PayrollImportResult(0, asked.Value)) : asked.Error!;
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
        var problem = await CheckComponentAsync(db, code, PayrollInputKind.Deduction, ct);
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

    internal static async Task<Error?> CheckComponentAsync(HospitalDbContext db, string code, PayrollInputKind kind, CancellationToken ct)
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
