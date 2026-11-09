using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;
using HyFib.HealthCare360.Modules.HR.Features.Leave;
using HyFib.HealthCare360.Platform.Approvals;
using PlatformApprovalStatus = HyFib.HealthCare360.Platform.Approvals.ApprovalStatus;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>An in-service encashment as asked: the days, the rate and amount the checker saw, and the payroll month.</summary>
public sealed record LeaveEncashmentPayload(
    Guid EncashmentId, Guid EmployeeId, Guid LeaveTypeId, int Year, decimal Days, decimal DailyRate, decimal Amount, int PayYear, int PayMonth);

/// <summary>
/// Leave paid out in money while the employee is in service (HR-05). HR who process payroll ask for it; someone else who
/// approves payroll approves it, never the employee whose leave it is (<see cref="PayApprovals.OwnCode"/>). Approved, the
/// balance is debited (<see cref="LeaveBalance.Encashed"/>) and the amount is posted as an earning (<see cref="ComponentCode"/>)
/// into the payroll inputs of the month asked for. The type's policy sets which leave pays out, the yearly cap and the days
/// kept (<see cref="LeaveEncashmentPolicy"/>). In-service encashment is taxable salary: s.10(10AA) exempts it only on
/// retirement or leaving, so the earning is taxed with the month's pay. Paying out on leaving is the full and final
/// settlement's (<see cref="StatutoryService"/>).
/// </summary>
public sealed class LeaveEncashmentService(
    HospitalDbContext db,
    ICurrentUser currentUser,
    IApprovals approvals,
    PayApprovals pay,
    StatutoryService statutory,
    IAuditLogger audit,
    TimeProvider time)
{
    public const string Kind = "hr.leave_encash";

    /// <summary>The payroll earning the encashment is paid under: taxable, outside PF and ESI wages.</summary>
    public const string ComponentCode = "LEAVE_ENC";

    /// <summary>Days a month's wages are divided by for the daily rate, as the full and final settlement does by default.</summary>
    public const decimal Divisor = 30m;

    /// <summary>Refused: the employee is leaving, so unused leave is paid in the full and final settlement instead.</summary>
    public const string LeavingCode = "leave.encash_leaving";

    public async Task<Result<ApprovalRequestDto>> AskAsync(EncashLeaveRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), request.EmployeeId);
        }

        if (await pay.OwnPayAsync([employee.Id], approving: false, ct) is { } own)
        {
            return own;
        }

        if (employee.Status == EmployeeStatus.Separated || employee.LeavingDate is not null)
        {
            return Error.Rule(LeavingCode, $"{employee.FullName} is leaving, so unused leave is paid in the full and final settlement, not as an in-service encashment.");
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId, ct);
        if (type is null)
        {
            return Error.Field("leaveTypeId", "Choose a leave type.");
        }

        var position = await PositionAsync(employee.Id, type.Id, request.Year, null, ct);
        if (LeaveEncashmentPolicy.Check(type, position, request.Days) is { } refused)
        {
            return Error.Rule(refused.Code, refused.Message);
        }

        var today = time.LocalToday();
        if ((request.PayYear * 100) + request.PayMonth > (today.Year * 100) + today.Month)
        {
            return Error.Rule("payroll.future_month", "Payroll cannot be created for a future month: pay the encashment in this month's payroll or an earlier one still open.");
        }

        var run = await db.Set<PayrollRun>().AsNoTracking()
            .FirstOrDefaultAsync(r => r.FacilityId == employee.FacilityId && r.Year == request.PayYear && r.Month == request.PayMonth, ct);
        if (run is { IsEditable: false })
        {
            return Error.Rule("payroll.closed", $"Payroll for {request.PayMonth:00}/{request.PayYear} is already approved; pay the encashment in a later month.");
        }

        if (await PayrollInputService.CheckComponentAsync(db, ComponentCode, PayrollInputKind.Earning, ct) is { } missing)
        {
            return missing;
        }

        var wages = await statutory.EncashmentWagesAsync(employee.Id, new DateOnly(request.PayYear, request.PayMonth, 1), ct);
        if (wages is not > 0)
        {
            return Error.Rule("leave.encash_no_structure", $"{employee.FullName} has no salary structure, so the encashment cannot be worked out.");
        }

        var worked = LeaveEncashment.Compute(request.Days, wages.Value, Divisor, onLeaving: false);
        var payload = new LeaveEncashmentPayload(Guid.CreateVersion7(), employee.Id, type.Id, request.Year, request.Days, worked.DailyRate, worked.Amount,
            request.PayYear, request.PayMonth);
        var left = position.Closing - position.PendingLeave - request.Days;
        return await approvals.RequestAsync(
            Kind,
            new ApprovalSubject(nameof(Employee), employee.Id, null, employee.FacilityId, null),
            payload,
            $"{request.Days:0.#} day(s) of {type.Code} {request.Year} encashed for {employee.FullName} ({employee.EmployeeCode}): ₹{Money.FormatInr(worked.Amount)}",
            [
                new ApprovalDetail("Employee", $"{employee.FullName} ({employee.EmployeeCode})"),
                new ApprovalDetail("Leave", $"{type.Name} ({type.Code}), {request.Year}: {request.Days:0.#} day(s); balance {position.Closing:0.#} → {position.Closing - request.Days:0.#}"
                    + (position.PendingLeave > 0 ? $" ({left:0.#} after {position.PendingLeave:0.#} day(s) of leave waiting)" : string.Empty)),
                new ApprovalDetail("Amount", $"₹{Money.FormatInr(worked.Amount)}: {request.Days:0.#} × ₹{Money.FormatInr(worked.DailyRate)} a day (₹{Money.FormatInr(wages.Value)} monthly wages ÷ {Divisor:0})"),
                new ApprovalDetail("Paid in", $"Payroll for {request.PayMonth:00}/{request.PayYear}, as an earning ({ComponentCode})"),
                new ApprovalDetail("Tax", "Taxable salary: s.10(10AA) exempts leave encashment only on retirement or leaving, so it is taxed with the month's pay."),
                new ApprovalDetail("Policy", $"At most {type.InServiceEncashmentDaysPerYear:0.#} day(s) a year in service ({position.EncashedThisYear:0.#} encashed already); {type.MinimumBalanceAfterEncashment:0.#} day(s) kept."),
            ],
            request.Reason,
            ct);
    }

    /// <summary>The encashments made for this employee, newest first.</summary>
    public async Task<IReadOnlyList<LeaveEncashmentDto>> ListAsync(Guid employeeId, CancellationToken ct)
    {
        var rows = await (
                from e in db.Set<LeaveEncashmentRecord>().AsNoTracking()
                join t in db.Set<LeaveType>() on e.LeaveTypeId equals t.Id
                where e.EmployeeId == employeeId
                orderby e.CreatedAt descending
                select new { e, t.Code })
            .ToListAsync(ct);
        var runIds = rows.Where(r => r.e.PayrollRunId is not null).Select(r => r.e.PayrollRunId!.Value).Distinct().ToList();
        var runs = await db.Set<PayrollRun>().AsNoTracking().Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => $"{r.Month:00}/{r.Year}", ct);
        return [.. rows.Select(r => new LeaveEncashmentDto(r.e.Id, r.e.LeaveTypeId, r.Code, r.e.Year, r.e.Kind, r.e.Days, r.e.DailyRate, r.e.Amount, r.e.PayrollRunId,
            r.e.PayrollRunId is { } run ? runs.GetValueOrDefault(run) : null, r.e.LastWorkingDay, r.e.Reason, r.e.CreatedAt))];
    }

    /// <summary>For each leave type that pays out in service: the cap, what was encashed and waits, and the most days now.</summary>
    public async Task<IReadOnlyList<EncashableLeaveDto>> EncashableAsync(Guid employeeId, int year, CancellationToken ct)
    {
        var types = await db.Set<LeaveType>().AsNoTracking()
            .Where(t => t.IsActive && t.IsEncashable && t.TracksBalance && t.InServiceEncashmentDaysPerYear > 0)
            .OrderBy(t => t.Code)
            .ToListAsync(ct);
        var today = time.LocalToday();
        var wages = await statutory.EncashmentWagesAsync(employeeId, today, ct) ?? 0;
        var rate = LeaveEncashment.Compute(1, wages, Divisor, onLeaving: false).DailyRate;
        var list = new List<EncashableLeaveDto>();
        foreach (var type in types)
        {
            var position = await PositionAsync(employeeId, type.Id, year, null, ct);
            list.Add(new EncashableLeaveDto(type.Id, type.Code, type.Name, year, type.InServiceEncashmentDaysPerYear!.Value, type.MinimumBalanceAfterEncashment,
                position.EncashedThisYear, position.PendingEncashment, LeaveEncashmentPolicy.MostDays(type, position) ?? 0, rate));
        }

        return list;
    }

    /// <summary>The encashments waiting for this employee.</summary>
    public async Task<IReadOnlyList<PendingLeaveEncashment>> PendingAsync(Guid employeeId, CancellationToken ct) =>
        [.. (await WaitingAsync(db, employeeId, time.GetUtcNow(), ct)).Select(w =>
            new PendingLeaveEncashment(w.ApprovalId, w.Payload.LeaveTypeId, w.Payload.Year, w.Payload.Days, w.Payload.Amount, w.Summary, w.RequestedBy, w.RequestedAt))];

    /// <summary>Encashments asked for and not decided: their days are reserved, as pending leave is.</summary>
    internal static async Task<IReadOnlyList<(Guid ApprovalId, LeaveEncashmentPayload Payload, string Summary, string RequestedBy, DateTimeOffset RequestedAt)>> WaitingAsync(
        HospitalDbContext db, Guid employeeId, DateTimeOffset now, CancellationToken ct)
    {
        var requests = await db.Set<ApprovalRequest>().AsNoTracking()
            .Where(r => r.Kind == Kind && r.SubjectType == nameof(Employee) && r.SubjectId == employeeId && r.Status == PlatformApprovalStatus.Pending && r.ExpiresAt > now)
            .Select(r => new { r.Id, r.Payload, r.Summary, r.RequestedByName, r.RequestedAt })
            .ToListAsync(ct);
        return [.. requests.Select(r => (r.Id, ApprovalPayload.Read<LeaveEncashmentPayload>(r.Payload), r.Summary, r.RequestedByName, r.RequestedAt))];
    }

    /// <summary>Days of encashment waiting for one leave type and year.</summary>
    internal static async Task<decimal> WaitingDaysAsync(HospitalDbContext db, Guid employeeId, Guid leaveTypeId, int year, DateTimeOffset now, Guid? exceptApprovalId, CancellationToken ct) =>
        (await WaitingAsync(db, employeeId, now, ct))
        .Where(w => w.Payload.LeaveTypeId == leaveTypeId && w.Payload.Year == year && w.ApprovalId != exceptApprovalId)
        .Sum(w => w.Payload.Days);

    /// <summary>
    /// Makes an approved encashment, in the approval's transaction: checked again against the balance and the policy as they
    /// are now, then the balance debited, the earning posted to the month's payroll and the encashment recorded. Never by
    /// the employee whose leave it is.
    /// </summary>
    internal async Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<LeaveEncashmentPayload>(request.Payload);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == asked.EmployeeId, ct);
        if (employee is null)
        {
            return Error.Rule("employee.gone", "The employee no longer exists, so nothing was paid.");
        }

        if (await pay.OwnPayAsync([employee.Id], approving: true, ct) is { } own)
        {
            return own;
        }

        if (employee.Status == EmployeeStatus.Separated || employee.LeavingDate is not null)
        {
            return Error.Rule(LeavingCode, $"{employee.EmployeeCode} is leaving now, so unused leave is paid in the full and final settlement. Nothing was paid.");
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == asked.LeaveTypeId, ct);
        if (type is null)
        {
            return Error.Rule("approval.stale", "The leave type no longer exists, so nothing was paid.");
        }

        var position = await PositionAsync(employee.Id, type.Id, asked.Year, request.Id, ct);
        if (LeaveEncashmentPolicy.Check(type, position, asked.Days) is { } refused)
        {
            return Error.Rule("approval.stale", $"The balance or the policy changed since this was asked for: {refused.Message} Nothing was paid; ask again.");
        }

        var run = await db.Set<PayrollRun>().FirstOrDefaultAsync(r => r.FacilityId == employee.FacilityId && r.Year == asked.PayYear && r.Month == asked.PayMonth, ct);
        if (run is null)
        {
            run = new PayrollRun { FacilityId = employee.FacilityId, Year = asked.PayYear, Month = asked.PayMonth, Status = PayrollRunStatus.Draft };
            db.Add(run);
        }
        else if (!run.IsEditable)
        {
            return Error.Rule("payroll.closed", $"Payroll for {asked.PayMonth:00}/{asked.PayYear} has been approved since this was asked for, so nothing was paid. Ask again for a later month.");
        }

        if (await PayrollInputService.CheckComponentAsync(db, ComponentCode, PayrollInputKind.Earning, ct) is { } missing)
        {
            return missing;
        }

        var balance = await db.Set<LeaveBalance>()
            .FirstAsync(b => b.EmployeeId == employee.Id && b.LeaveTypeId == type.Id && b.Year == asked.Year, ct);
        balance.Encashed += asked.Days;

        var description = $"Leave encashment: {asked.Days:0.#} day(s) of {type.Code} {asked.Year} at ₹{Money.FormatInr(asked.DailyRate)}";
        var input = new PayrollInput
        {
            FacilityId = employee.FacilityId,
            PayrollRunId = run.Id,
            EmployeeId = employee.Id,
            Kind = PayrollInputKind.Earning,
            ComponentCode = ComponentCode,
            Description = description,
            Amount = asked.Amount,
            Source = PayrollInputSource.Integration,
            SourceReference = $"leave-encashment:{asked.EncashmentId}",
        };
        db.Add(input);
        db.Add(new LeaveEncashmentRecord
        {
            Id = asked.EncashmentId,
            FacilityId = employee.FacilityId,
            EmployeeId = employee.Id,
            LeaveTypeId = type.Id,
            Year = asked.Year,
            Kind = LeaveEncashmentKind.InService,
            Days = asked.Days,
            DailyRate = asked.DailyRate,
            Amount = asked.Amount,
            PayrollRunId = run.Id,
            PayrollInputId = input.Id,
            ApprovalId = request.Id,
            Reason = request.Reason,
        });

        var words = $"{asked.Days:0.#} day(s) of {type.Code} {asked.Year} encashed for {employee.EmployeeCode}: ₹{Money.FormatInr(asked.Amount)} in payroll {asked.PayMonth:00}/{asked.PayYear}";
        audit.Add(new AuditEntry(AuditCategories.Data, "leave.encashed",
            $"{words}: {request.Reason} (asked for by {request.RequestedByName}, approved by {currentUser.DisplayName ?? currentUser.UserName})")
        {
            EntityType = nameof(LeaveBalance),
            EntityId = balance.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { asked, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return words;
    }

    /// <summary>Where the balance stands: closing, leave and encashments waiting, and what was encashed in service this year.</summary>
    private async Task<EncashmentPosition> PositionAsync(Guid employeeId, Guid leaveTypeId, int year, Guid? exceptApprovalId, CancellationToken ct)
    {
        var closing = (await db.Set<LeaveBalance>().AsNoTracking()
            .FirstOrDefaultAsync(b => b.EmployeeId == employeeId && b.LeaveTypeId == leaveTypeId && b.Year == year, ct))?.Closing ?? 0;
        var pendingLeave = await db.Set<LeaveRequest>()
            .Where(l => l.EmployeeId == employeeId && l.LeaveTypeId == leaveTypeId && l.Status == Domain.ApprovalStatus.Pending && l.FromDate.Year == year)
            .SumAsync(l => l.Days, ct);
        var encashed = await db.Set<LeaveEncashmentRecord>()
            .Where(e => e.EmployeeId == employeeId && e.LeaveTypeId == leaveTypeId && e.Year == year && e.Kind == LeaveEncashmentKind.InService)
            .SumAsync(e => e.Days, ct);
        var waiting = await WaitingDaysAsync(db, employeeId, leaveTypeId, year, time.GetUtcNow(), exceptApprovalId, ct);
        return new EncashmentPosition(closing, pendingLeave, waiting, encashed);
    }
}

/// <summary>An in-service leave encashment: HR who process payroll ask, another person who approves payroll approves.</summary>
internal sealed class LeaveEncashmentApproval(LeaveEncashmentService encashments) : IApprovalAction
{
    public string Kind => LeaveEncashmentService.Kind;

    public string Title => "Leave encashed";

    public string ApprovePermission => HRPermissions.PayrollApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => encashments.ApplyAsync(request, ct);
}
