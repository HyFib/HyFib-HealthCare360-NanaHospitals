using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Platform.Approvals;
using PlatformApprovalStatus = HyFib.HealthCare360.Platform.Approvals.ApprovalStatus;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Which changes to pay wait for a second person (maker-checker slice 11). What raises pay waits: a salary structure (a
/// revision), an additional earning, a loss-of-pay or TDS override (either way: an override replaces what attendance and
/// the approved structure say). What lowers it is made at once: a deduction added. A deduction removed waits when another
/// module posted it (canteen credit, an advance) or another person entered it; one's own entry is taken back at once.
/// Earnings and overrides already in a run are removed at once: the pay then follows attendance and the approved structure.
/// </summary>
public static class PayChangeRule
{
    public static bool InputWaits(PayrollInputKind kind) => kind != PayrollInputKind.Deduction;

    public static bool RemovalWaits(PayrollInputKind kind, PayrollInputSource source, Guid? enteredBy, Guid? removedBy) =>
        kind == PayrollInputKind.Deduction && (source == PayrollInputSource.Integration || enteredBy is null || enteredBy != removedBy);
}

/// <summary>A salary structure as asked: the employee, the revision, and the start of the latest structure it follows.</summary>
public sealed record SalaryStructurePayload(Guid EmployeeId, SaveSalaryStructureRequest Structure, DateOnly? LatestFrom);

/// <summary>One payroll input as asked: the input it becomes (its id), the run, the employee and what it is.</summary>
public sealed record PayrollInputPayload(Guid InputId, Guid RunId, Guid EmployeeId, PayrollInputKind Kind, string ComponentCode, string Description, decimal Amount);

/// <summary>An imported file with earnings in it, as asked: every line, resolved to its employee.</summary>
public sealed record PayrollImportPayload(Guid RunId, IReadOnlyList<PayrollInputPayload> Lines);

/// <summary>A deduction's removal as asked: the input, as it was.</summary>
public sealed record PayrollInputRemovalPayload(Guid InputId, Guid RunId, Guid EmployeeId, string ComponentCode, decimal Amount, PayrollInputSource Source, uint Version);

/// <summary>A change to pay waiting for a second person, for the run's and the employee's screens.</summary>
public sealed record PendingPayChange(Guid ApprovalId, string Kind, Guid? EmployeeId, Guid? InputId, string Summary, string RequestedBy, DateTimeOffset RequestedAt);

/// <summary>
/// Changes to pay approved by someone other than who made them (maker-checker slice 11): a salary structure revision, an
/// additional earning, a loss-of-pay or TDS override, a file of inputs with earnings in it, and a deduction another module
/// posted, or another person entered, taken off (<see cref="PayChangeRule"/>). The maker holds
/// <see cref="HRPermissions.PayrollProcess"/>; someone else who holds <see cref="HRPermissions.PayrollApprove"/> approves,
/// never the one who asked (<c>approval.self</c>). Nobody makes or approves a change to their own pay or structure
/// (<see cref="OwnCode"/>). The change is made only as asked, on a payroll still open, and audited with both names.
/// </summary>
public sealed class PayApprovals(
    HospitalDbContext db, ICurrentUser current, IAuditLogger audit, IApprovals approvals, SalaryStructureService structures, TimeProvider time)
{
    public const string StructureKind = "hr.salary_structure";
    public const string InputKind = "hr.payroll_input";
    public const string ImportKind = "hr.payroll_import";
    public const string RemovalKind = "hr.payroll_input_remove";

    /// <summary>Refused, asking or approving: a change to one's own pay or salary structure.</summary>
    public const string OwnCode = "hr.pay_own";

    private static readonly string[] Kinds = [StructureKind, InputKind, ImportKind, RemovalKind];

    /// <summary>Refuses a change to the signed-in user's own pay: <paramref name="employeeIds"/> holds their employee record.</summary>
    public async Task<Error?> OwnPayAsync(IReadOnlyCollection<Guid> employeeIds, bool approving, CancellationToken ct)
    {
        if (current.UserId is not { } userId || employeeIds.Count == 0)
        {
            return null;
        }

        var own = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(e => employeeIds.Contains(e.Id) && e.UserId == userId)
            .Select(e => e.EmployeeCode)
            .FirstOrDefaultAsync(ct);
        return own is null
            ? null
            : Error.Forbidden(OwnCode, approving
                ? $"This changes your own pay ({own}), so someone else who approves payroll decides it."
                : $"This changes your own pay ({own}): someone else in HR enters it, and another person approves it.");
    }

    /// <summary>Asks a second person to add a salary structure for <paramref name="employeeId"/>.</summary>
    public async Task<Result<ApprovalRequestDto>> AskStructureAsync(Guid employeeId, SaveSalaryStructureRequest request, CancellationToken ct)
    {
        var own = await OwnPayAsync([employeeId], approving: false, ct);
        if (own is not null)
        {
            return own;
        }

        var check = await structures.CheckAsync(employeeId, request, ct);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var employee = await NameAsync(employeeId, ct);
        var words = await structures.DescribeAsync(request, ct);
        List<ApprovalDetail> details =
        [
            new("Employee", employee),
            new("Salary structure", words),
            new("What it reaches",
                check.Value.LatestFrom is { } latest
                    ? $"The structure in force since {latest:dd MMM yyyy} closes the day before; every payroll from {request.EffectiveFrom:MMM yyyy} pays this one."
                    : $"Every payroll from {request.EffectiveFrom:MMM yyyy} pays this structure (the employee has none yet)."),
        ];
        if (request.Notes is { Length: > 0 } notes)
        {
            details.Add(new("Notes", notes.Trim()));
        }

        return await approvals.RequestAsync(
            StructureKind,
            new ApprovalSubject(nameof(Employee), employeeId, null, check.Value.FacilityId, null),
            new SalaryStructurePayload(employeeId, request, check.Value.LatestFrom),
            Fit($"Salary structure of {employee}: {words}", 300),
            details,
            string.IsNullOrWhiteSpace(request.Notes) ? "Salary revision." : request.Notes.Trim(),
            ct);
    }

    /// <summary>Asks a second person to add an earning or an override to a run.</summary>
    public async Task<Result<ApprovalRequestDto>> AskInputAsync(PayrollRun run, PayrollInputPayload input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(input);
        var employee = await NameAsync(input.EmployeeId, ct);
        var words = Describe(input);
        return await approvals.RequestAsync(
            InputKind,
            new ApprovalSubject(nameof(PayrollInput), input.InputId, null, run.FacilityId, null),
            input,
            Fit($"{words} for {employee}, payroll {run.Month:00}/{run.Year}", 300),
            [
                new("Employee", employee),
                new("Payroll", $"{run.Month:00}/{run.Year}"),
                new(KindWords(input.Kind), words),
                new("What it reaches", "The employee's pay for the month once the payroll is processed again; nothing changes until it is approved."),
            ],
            input.Description,
            ct);
    }

    /// <summary>Asks a second person to add an imported file with earnings in it, as one.</summary>
    public async Task<Result<ApprovalRequestDto>> AskImportAsync(PayrollRun run, IReadOnlyList<PayrollInputPayload> lines, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(lines);
        var earnings = lines.Where(l => l.Kind == PayrollInputKind.Earning).ToList();
        var deductions = lines.Where(l => l.Kind == PayrollInputKind.Deduction).ToList();
        return await approvals.RequestAsync(
            ImportKind,
            new ApprovalSubject(nameof(PayrollRun), run.Id, null, run.FacilityId, null),
            new PayrollImportPayload(run.Id, lines),
            $"Imported inputs for payroll {run.Month:00}/{run.Year}: {lines.Count} line(s), earnings ₹{Money.FormatInr(earnings.Sum(l => l.Amount))}",
            [
                new("Payroll", $"{run.Month:00}/{run.Year}"),
                new("Earnings", $"{earnings.Count} line(s) for {earnings.Select(l => l.EmployeeId).Distinct().Count()} employee(s), ₹{Money.FormatInr(earnings.Sum(l => l.Amount))}"),
                new("Deductions", $"{deductions.Count} line(s), ₹{Money.FormatInr(deductions.Sum(l => l.Amount))}"),
                new("What it reaches", "Every line is added together once approved, and pays with the month's payroll once it is processed again."),
            ],
            "Imported payroll inputs with earnings.",
            ct);
    }

    /// <summary>Asks a second person to take a deduction off a run.</summary>
    public async Task<Result<ApprovalRequestDto>> AskRemovalAsync(PayrollRun run, PayrollInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(input);
        var employee = await NameAsync(input.EmployeeId, ct);
        var source = input.Source == PayrollInputSource.Integration ? $"posted by another module ({input.SourceReference})" : "entered by another person";
        return await approvals.RequestAsync(
            RemovalKind,
            new ApprovalSubject(nameof(PayrollInput), input.Id, input.Version, run.FacilityId, null),
            new PayrollInputRemovalPayload(input.Id, run.Id, input.EmployeeId, input.ComponentCode, input.Amount, input.Source, input.Version),
            Fit($"Deduction {input.ComponentCode} ₹{Money.FormatInr(input.Amount)} taken off {employee}, payroll {run.Month:00}/{run.Year}", 300),
            [
                new("Employee", employee),
                new("Deduction", $"{input.ComponentCode} ₹{Money.FormatInr(input.Amount)}: {input.Description}"),
                new("Entered", source),
                new("What it reaches", "The employee is paid this much more for the month, and what it recovers (canteen credit, an advance) stays owed elsewhere."),
            ],
            $"Deduction removed: {input.Description}",
            ct);
    }

    /// <summary>The changes to pay waiting in a run, and the structures waiting for its employees.</summary>
    public async Task<IReadOnlyList<PendingPayChange>> PendingAsync(Guid? runId, Guid? employeeId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var requests = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => Kinds.Contains(r.Kind) && r.Status == PlatformApprovalStatus.Pending && r.ExpiresAt > now)
            .OrderBy(r => r.RequestedAt)
            .Select(r => new { r.Id, r.Kind, r.Payload, r.Summary, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        var pending = new List<PendingPayChange>();
        foreach (var r in requests)
        {
            Guid? run = null, employee = null, input = null;
            switch (r.Kind)
            {
                case StructureKind:
                    employee = ApprovalPayload.Read<SalaryStructurePayload>(r.Payload).EmployeeId;
                    break;
                case InputKind:
                    var asked = ApprovalPayload.Read<PayrollInputPayload>(r.Payload);
                    (run, employee, input) = (asked.RunId, asked.EmployeeId, asked.InputId);
                    break;
                case ImportKind:
                    run = ApprovalPayload.Read<PayrollImportPayload>(r.Payload).RunId;
                    break;
                default:
                    var removal = ApprovalPayload.Read<PayrollInputRemovalPayload>(r.Payload);
                    (run, employee, input) = (removal.RunId, removal.EmployeeId, removal.InputId);
                    break;
            }

            if ((runId is null || run == runId) && (employeeId is null || employee == employeeId) && (runId is not null || r.Kind == StructureKind))
            {
                pending.Add(new PendingPayChange(r.Id, r.Kind, employee, input, r.Summary, r.RequestedByName, r.RequestedAt));
            }
        }

        return pending;
    }

    internal async Task<Result<string>> ApplyStructureAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = ApprovalPayload.Read<SalaryStructurePayload>(request.Payload);
        var own = await OwnPayAsync([payload.EmployeeId], approving: true, ct);
        if (own is not null)
        {
            return own;
        }

        var check = await structures.CheckAsync(payload.EmployeeId, payload.Structure, ct);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        if (check.Value.LatestFrom != payload.LatestFrom)
        {
            return Error.Rule("approval.stale", "Another salary structure was added for this employee after this was asked for, so nothing was changed. Ask again.");
        }

        var structure = await structures.AddAsync(payload.EmployeeId, check.Value.FacilityId, payload.Structure, ct);
        var words = await structures.DescribeAsync(payload.Structure, ct);
        Audit("payroll.salary_structure_added", nameof(EmployeeSalaryStructure), structure.Id,
            $"Salary structure of {await NameAsync(payload.EmployeeId, ct)}: {words}", request, new { payload.EmployeeId, payload.Structure });
        return words;
    }

    internal async Task<Result<string>> ApplyInputAsync(ApprovalRequest request, CancellationToken ct)
    {
        var input = ApprovalPayload.Read<PayrollInputPayload>(request.Payload);
        var added = await AddApprovedAsync(input.RunId, [input], PayrollInputSource.Manual, ct);
        if (added.IsFailure)
        {
            return added.Error!;
        }

        var words = Describe(input);
        Audit("payroll.input_added", nameof(PayrollInput), input.InputId, $"{words} for {await NameAsync(input.EmployeeId, ct)} ({input.Description})", request, input);
        return words;
    }

    internal async Task<Result<string>> ApplyImportAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = ApprovalPayload.Read<PayrollImportPayload>(request.Payload);
        var added = await AddApprovedAsync(payload.RunId, payload.Lines, PayrollInputSource.Import, ct);
        if (added.IsFailure)
        {
            return added.Error!;
        }

        var words = $"{payload.Lines.Count} input(s) imported";
        Audit("payroll.inputs_imported", nameof(PayrollRun), payload.RunId, words, request, new { lines = payload.Lines.Count, total = payload.Lines.Sum(l => l.Amount) });
        return words;
    }

    internal async Task<Result<string>> ApplyRemovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        var payload = ApprovalPayload.Read<PayrollInputRemovalPayload>(request.Payload);
        var own = await OwnPayAsync([payload.EmployeeId], approving: true, ct);
        if (own is not null)
        {
            return own;
        }

        var input = await db.Set<PayrollInput>().FirstOrDefaultAsync(i => i.Id == payload.InputId, ct);
        if (input is null)
        {
            return Error.Rule("approval.stale", "The deduction was already removed, so nothing was changed.");
        }

        if (input.Version != payload.Version)
        {
            return Error.Rule("approval.stale", "The deduction changed after this was asked for, so nothing was changed. Ask again.");
        }

        var run = await OpenRunAsync(payload.RunId, ct);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        db.Remove(input);
        var words = $"Deduction {input.ComponentCode} ₹{Money.FormatInr(input.Amount)} removed";
        Audit("payroll.input_removed", nameof(PayrollInput), input.Id, $"{words} from {await NameAsync(input.EmployeeId, ct)} ({input.Description})", request,
            new { input.EmployeeId, input.ComponentCode, input.Amount, input.Source, input.SourceReference });
        return words;
    }

    private async Task<Result> AddApprovedAsync(Guid runId, IReadOnlyList<PayrollInputPayload> lines, PayrollInputSource source, CancellationToken ct)
    {
        var own = await OwnPayAsync([.. lines.Select(l => l.EmployeeId).Distinct()], approving: true, ct);
        if (own is not null)
        {
            return own;
        }

        var run = await OpenRunAsync(runId, ct);
        if (run.IsFailure)
        {
            return run.Error!;
        }

        var employeeIds = lines.Select(l => l.EmployeeId).Distinct().ToList();
        var found = await db.Set<Employee>().AsNoTracking().CountAsync(e => employeeIds.Contains(e.Id) && e.FacilityId == run.Value.FacilityId, ct);
        if (found != employeeIds.Count)
        {
            return Error.Rule("approval.stale", "An employee in this request is no longer on the rolls of this facility, so nothing was changed.");
        }

        foreach (var line in lines)
        {
            if (line.Kind is PayrollInputKind.Earning or PayrollInputKind.Deduction && await PayrollInputService.CheckComponentAsync(db, line.ComponentCode, line.Kind, ct) is { } problem)
            {
                return Error.Rule("approval.stale", $"{problem.Message} Nothing was changed.");
            }

            db.Add(new PayrollInput
            {
                Id = line.InputId,
                FacilityId = run.Value.FacilityId,
                PayrollRunId = runId,
                EmployeeId = line.EmployeeId,
                Kind = line.Kind,
                ComponentCode = line.ComponentCode,
                Description = line.Description,
                Amount = line.Amount,
                Source = source,
            });
        }

        return Result.Success();
    }

    private async Task<Result<PayrollRun>> OpenRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<PayrollRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return Error.Rule("payroll.gone", "The payroll no longer exists, so nothing was changed.");
        }

        return run.IsEditable ? run : Error.Rule("payroll.not_editable", $"Payroll {run.Month:00}/{run.Year} is already approved, so nothing was changed.");
    }

    private void Audit(string action, string entityType, Guid entityId, string summary, ApprovalRequest request, object details)
    {
        var approver = current.DisplayName ?? current.UserName ?? "Unknown user";
        audit.Add(new AuditEntry(AuditCategories.Data, action, Fit($"{summary} (asked for by {request.RequestedByName}, approved by {approver})", 1000))
        {
            EntityType = entityType,
            EntityId = entityId.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { change = details, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = current.UserId },
        });
    }

    private async Task<string> NameAsync(Guid employeeId, CancellationToken ct) =>
        await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(e => e.Id == employeeId)
            .Select(e => e.FullName + " (" + e.EmployeeCode + ")").FirstOrDefaultAsync(ct) ?? "an employee";

    /// <summary>"Earning OT ₹8,000.00", "Loss-of-pay days set to 2", "TDS set to ₹1,500.00".</summary>
    public static string Describe(PayrollInputPayload input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.Kind switch
        {
            PayrollInputKind.Earning => $"Earning {input.ComponentCode} ₹{Money.FormatInr(input.Amount)}",
            PayrollInputKind.Deduction => $"Deduction {input.ComponentCode} ₹{Money.FormatInr(input.Amount)}",
            PayrollInputKind.LopDaysOverride => $"Loss-of-pay days set to {input.Amount:0.##}",
            _ => $"TDS for the month set to ₹{Money.FormatInr(input.Amount)}",
        };
    }

    private static string KindWords(PayrollInputKind kind) => kind switch
    {
        PayrollInputKind.Earning => "Additional earning",
        PayrollInputKind.LopDaysOverride => "Loss-of-pay override",
        PayrollInputKind.TdsOverride => "TDS override",
        _ => "Deduction",
    };

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";
}

/// <summary>A salary structure revision: one HR user asks, another who approves payroll decides.</summary>
internal sealed class SalaryStructureApproval(PayApprovals pay) : IApprovalAction
{
    public string Kind => PayApprovals.StructureKind;

    public string Title => "Salary structure revision";

    public string ApprovePermission => HRPermissions.PayrollApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => pay.ApplyStructureAsync(request, ct);
}

/// <summary>An additional earning, loss-of-pay override or TDS override in a payroll: one asks, another approves.</summary>
internal sealed class PayrollInputApproval(PayApprovals pay) : IApprovalAction
{
    public string Kind => PayApprovals.InputKind;

    public string Title => "Payroll earning or override";

    public string ApprovePermission => HRPermissions.PayrollApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => pay.ApplyInputAsync(request, ct);
}

/// <summary>A file of payroll inputs with earnings in it: one imports, another approves it as a whole.</summary>
internal sealed class PayrollImportApproval(PayApprovals pay) : IApprovalAction
{
    public string Kind => PayApprovals.ImportKind;

    public string Title => "Imported payroll inputs";

    public string ApprovePermission => HRPermissions.PayrollApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => pay.ApplyImportAsync(request, ct);
}

/// <summary>A deduction another module posted, or another person entered, taken off a payroll: one asks, another approves.</summary>
internal sealed class PayrollInputRemovalApproval(PayApprovals pay) : IApprovalAction
{
    public string Kind => PayApprovals.RemovalKind;

    public string Title => "Payroll deduction removed";

    public string ApprovePermission => HRPermissions.PayrollApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => pay.ApplyRemovalAsync(request, ct);
}
