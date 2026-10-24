using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Leave;
using HyFib.HealthCare360.Platform.Files;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Leave management (HR-05): application with balance and overlap checks, manager/HR approval, cancellation,
/// yearly credit with carry-forward and pro-rating, and manual adjustments. Approved leave feeds attendance, and
/// decisions on past dates recompute attendance immediately so payroll never sees stale absences.
/// </summary>
public sealed class LeaveService(
    HospitalDbContext db,
    ICurrentUser currentUser,
    HrApprovers approvers,
    AttendanceService attendance,
    IAuditLogger audit,
    TimeProvider time)
{
    public async Task<Result<Guid>> ApplyAsync(Guid employeeId, ApplyLeaveRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        if (employee.IsSeparated || !employee.IsEmployedOn(request.FromDate) || !employee.IsEmployedOn(request.ToDate))
        {
            return Error.Rule("leave.not_employed", "Leave can be applied only for days the employee is on the rolls.");
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId && t.IsActive, ct);
        if (type is null)
        {
            return Error.Field("leaveTypeId", "Choose an active leave type.");
        }

        if (type.ApplicableGender is { } gender && gender != employee.Gender)
        {
            return Error.Rule("leave.not_applicable", $"{type.Name} is not applicable to this employee.");
        }

        if (!type.AllowHalfDay && LeaveDayCounter.IncludesHalfDay(request.FromSession, request.ToSession))
        {
            return Error.Field("fromSession", $"{type.Name} cannot be taken in half days.");
        }

        if (request.FromDate.Year != request.ToDate.Year)
        {
            return Error.Field("toDate", "Leave that crosses the new year must be applied as two requests.");
        }

        var isNonWorking = await NonWorkingDaysAsync(employee, request.FromDate, request.ToDate, ct);
        var days = LeaveDayCounter.Count(request.FromDate, request.FromSession, request.ToDate, request.ToSession, isNonWorking, type.CountsNonWorkingDays);
        if (days <= 0)
        {
            return Error.Rule("leave.no_working_days", "The selected dates contain no working days.");
        }

        if (type.MaxDaysPerRequest is { } max && days > max)
        {
            return Error.Rule("leave.too_long", $"{type.Name} can be taken for at most {max:0.#} days at a time.");
        }

        if (type.DocumentRequiredAfterDays is { } documentAfter && days > documentAfter)
        {
            if (request.AttachmentFileId is not { } fileId || !await db.Set<StoredFile>().AnyAsync(f => f.Id == fileId, ct))
            {
                return Error.Field("attachmentFileId", $"Attach a supporting document (for example a medical certificate) for more than {documentAfter:0.#} days.");
            }
        }

        var overlapping = await db.Set<LeaveRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(l => l.EmployeeId == employeeId && (l.Status == ApprovalStatus.Pending || l.Status == ApprovalStatus.Approved)
                           && l.FromDate <= request.ToDate && l.ToDate >= request.FromDate, ct);
        if (overlapping)
        {
            return Error.Conflict("leave.overlap", "Another leave request already covers some of these dates.");
        }

        if (type.TracksBalance)
        {
            var available = await AvailableAsync(employeeId, type.Id, request.FromDate.Year, excludeRequestId: null, ct);
            if (available < days)
            {
                return Error.Rule("leave.insufficient_balance", $"Only {available:0.#} day(s) of {type.Name} are available; {days:0.#} requested.");
            }
        }

        var leave = new LeaveRequest
        {
            FacilityId = employee.FacilityId,
            EmployeeId = employeeId,
            LeaveTypeId = type.Id,
            FromDate = request.FromDate,
            FromSession = request.FromSession,
            ToDate = request.ToDate,
            ToSession = request.ToSession,
            Days = days,
            Reason = request.Reason.Trim(),
            AttachmentFileId = request.AttachmentFileId,
            Status = ApprovalStatus.Pending,
            AppliedAt = time.GetUtcNow(),
            RequestedByUserId = currentUser.UserId,
        };
        db.Add(leave);
        await db.SaveChangesAsync(ct);

        await approvers.NotifyRequestAsync(employeeId, employee.FacilityId, HRPermissions.LeaveApprove,
            "Leave request to review",
            $"{employee.FullName} applied for {days:0.#} day(s) of {type.Name} from {request.FromDate:dd MMM} to {request.ToDate:dd MMM yyyy}.",
            "/hr/leave?status=Pending", ct);
        return leave.Id;
    }

    public async Task<Result> DecideAsync(Guid id, bool approve, string? note, CancellationToken ct)
    {
        var leave = await db.Set<LeaveRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (leave is null)
        {
            return Error.NotFound(nameof(LeaveRequest), id);
        }

        var approver = await approvers.EnsureCanDecideAsync(leave.EmployeeId, leave.FacilityId, HRPermissions.LeaveApprove, ct);
        if (approver.IsFailure)
        {
            return approver.Error!;
        }

        var now = time.GetUtcNow();
        if (!approve)
        {
            var rejected = leave.Reject(approver.Value, now, note);
            if (rejected.IsFailure)
            {
                return rejected;
            }

            await db.SaveChangesAsync(ct);
            await approvers.NotifyEmployeeAsync(leave.EmployeeId, "Leave request rejected",
                $"Your leave from {leave.FromDate:dd MMM} to {leave.ToDate:dd MMM yyyy} was not approved. Reason: {note?.Trim()}", "/hr/me/leave", ct);
            return Result.Success();
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == leave.LeaveTypeId, ct);
        if (type.TracksBalance)
        {
            var available = await AvailableAsync(leave.EmployeeId, type.Id, leave.FromDate.Year, excludeRequestId: leave.Id, ct);
            if (available < leave.Days)
            {
                return Error.Rule("leave.insufficient_balance", $"Only {available:0.#} day(s) of {type.Name} remain; the request is for {leave.Days:0.#}.");
            }
        }

        var approved = leave.Approve(approver.Value, now, note);
        if (approved.IsFailure)
        {
            return approved;
        }

        if (type.TracksBalance)
        {
            var balance = await GetOrCreateBalanceAsync(leave.FacilityId, leave.EmployeeId, type.Id, leave.FromDate.Year, ct);
            balance.Availed += leave.Days;
        }

        await db.SaveChangesAsync(ct);
        await RecomputePastDaysAsync(leave, ct);
        await approvers.NotifyEmployeeAsync(leave.EmployeeId, "Leave approved",
            $"Your {type.Name} from {leave.FromDate:dd MMM} to {leave.ToDate:dd MMM yyyy} ({leave.Days:0.#} day(s)) was approved.", "/hr/me/leave", ct);
        return Result.Success();
    }

    /// <summary>Cancels a request: the employee's own (<paramref name="ownEmployeeId"/>) or, when null, by HR.</summary>
    public async Task<Result> CancelAsync(Guid id, string reason, Guid? ownEmployeeId, CancellationToken ct)
    {
        var leave = await db.Set<LeaveRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(l => l.Id == id, ct);
        if (leave is null || (ownEmployeeId is { } own && leave.EmployeeId != own))
        {
            return Error.NotFound(nameof(LeaveRequest), id);
        }

        var wasApproved = leave.Status == ApprovalStatus.Approved;
        var result = leave.Cancel(reason, time.LocalToday(), byHr: ownEmployeeId is null, time.GetUtcNow());
        if (result.IsFailure)
        {
            return result;
        }

        if (wasApproved)
        {
            var type = await db.Set<LeaveType>().AsNoTracking().FirstAsync(t => t.Id == leave.LeaveTypeId, ct);
            if (type.TracksBalance)
            {
                var balance = await GetOrCreateBalanceAsync(leave.FacilityId, leave.EmployeeId, type.Id, leave.FromDate.Year, ct);
                balance.Availed -= leave.Days;
            }
        }

        await db.SaveChangesAsync(ct);
        if (wasApproved)
        {
            await RecomputePastDaysAsync(leave, ct);
        }

        if (ownEmployeeId is null)
        {
            await approvers.NotifyEmployeeAsync(leave.EmployeeId, "Leave cancelled by HR",
                $"Your leave from {leave.FromDate:dd MMM} to {leave.ToDate:dd MMM yyyy} was cancelled. Reason: {reason.Trim()}", "/hr/me/leave", ct);
        }

        return Result.Success();
    }

    public async Task<IReadOnlyList<LeaveBalanceDto>> BalancesAsync(Guid employeeId, int year, CancellationToken ct)
    {
        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.IsActive && t.TracksBalance).OrderBy(t => t.Code).ToListAsync(ct);
        var balances = await db.Set<LeaveBalance>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(b => b.EmployeeId == employeeId && b.Year == year)
            .ToDictionaryAsync(b => b.LeaveTypeId, ct);
        var pending = await db.Set<LeaveRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(l => l.EmployeeId == employeeId && l.Status == ApprovalStatus.Pending && l.FromDate.Year == year)
            .GroupBy(l => l.LeaveTypeId)
            .Select(g => new { g.Key, Days = g.Sum(l => l.Days) })
            .ToDictionaryAsync(g => g.Key, g => g.Days, ct);

        return types.Select(t =>
        {
            balances.TryGetValue(t.Id, out var b);
            var pendingDays = pending.GetValueOrDefault(t.Id);
            var closing = b?.Closing ?? 0;
            return new LeaveBalanceDto(b?.Id, t.Id, t.Code, t.Name, year, b?.Opening ?? 0, b?.Credited ?? 0, b?.Adjusted ?? 0, b?.Availed ?? 0,
                b?.Encashed ?? 0, closing, pendingDays, LeaveEntitlement.Available(closing, pendingDays));
        }).ToList();
    }

    /// <summary>
    /// Opens the year's balances for every current employee of the facility: carry-forward from last year (capped)
    /// plus the pro-rated annual quota. Existing balances are left alone, so the credit can be re-run safely.
    /// </summary>
    public async Task<Result<CreditLeaveResult>> CreditYearAsync(int year, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var yearEnd = new DateOnly(year, 12, 31);
        var employees = await db.Set<Employee>().AsNoTracking()
            .Where(e => e.FacilityId == facilityId && e.Status != EmployeeStatus.Separated && e.JoiningDate <= yearEnd)
            .Select(e => new { e.Id, e.JoiningDate, e.Gender })
            .ToListAsync(ct);
        var types = await db.Set<LeaveType>().AsNoTracking().Where(t => t.IsActive && t.TracksBalance).ToListAsync(ct);
        var employeeIds = employees.Select(e => e.Id).ToList();
        var balances = await db.Set<LeaveBalance>().AsNoTracking()
            .Where(b => employeeIds.Contains(b.EmployeeId) && (b.Year == year || b.Year == year - 1))
            .ToListAsync(ct);
        var current = balances.Where(b => b.Year == year).Select(b => (b.EmployeeId, b.LeaveTypeId)).ToHashSet();
        var previous = balances.Where(b => b.Year == year - 1).ToDictionary(b => (b.EmployeeId, b.LeaveTypeId), b => b.Closing);

        var created = 0;
        foreach (var employee in employees)
        {
            foreach (var type in types.Where(t => t.ApplicableGender is null || t.ApplicableGender == employee.Gender))
            {
                if (current.Contains((employee.Id, type.Id)))
                {
                    continue;
                }

                db.Add(new LeaveBalance
                {
                    FacilityId = facilityId,
                    EmployeeId = employee.Id,
                    LeaveTypeId = type.Id,
                    Year = year,
                    Opening = LeaveEntitlement.CarryForward(previous.GetValueOrDefault((employee.Id, type.Id)), type.CarryForwardCap),
                    Credited = LeaveEntitlement.ProratedCredit(type.AnnualQuota, employee.JoiningDate, year),
                });
                created++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new CreditLeaveResult(employees.Count, created);
    }

    public async Task<Result> AdjustAsync(AdjustLeaveBalanceRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == request.EmployeeId)
            .Select(e => new { e.FacilityId, e.EmployeeCode }).FirstOrDefaultAsync(ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), request.EmployeeId);
        }

        var type = await db.Set<LeaveType>().AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId && t.TracksBalance, ct);
        if (type is null)
        {
            return Error.Field("leaveTypeId", "Choose a leave type that keeps a balance.");
        }

        var balance = await GetOrCreateBalanceAsync(employee.FacilityId, request.EmployeeId, type.Id, request.Year, ct);
        if (balance.Closing + request.Days < 0)
        {
            return Error.Rule("leave.negative_balance", "The adjustment would make the balance negative.");
        }

        balance.Adjusted += request.Days;
        audit.Add(new AuditEntry(AuditCategories.Data, "leave.balance_adjusted",
            $"{type.Code} {request.Year} balance of {employee.EmployeeCode} adjusted by {request.Days:+0.#;-0.#}: {request.Reason.Trim()}")
        {
            EntityType = nameof(LeaveBalance),
            EntityId = balance.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Holidays, week offs (fixed or rostered) — unless a shift is rostered that day.</summary>
    private async Task<Func<DateOnly, bool>> NonWorkingDaysAsync(Employee employee, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var holidays = (await db.Set<Holiday>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(h => h.FacilityId == employee.FacilityId && h.Date >= from && h.Date <= to && h.Kind != HolidayKind.Restricted)
                .Select(h => h.Date)
                .ToListAsync(ct))
            .ToHashSet();
        var roster = await db.Set<RosterEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(r => r.EmployeeId == employee.Id && r.Date >= from && r.Date <= to)
            .Select(r => new { r.Date, r.IsWeekOff })
            .ToListAsync(ct);
        var rosteredDuty = roster.Where(r => !r.IsWeekOff).Select(r => r.Date).ToHashSet();
        var rosteredOff = roster.Where(r => r.IsWeekOff).Select(r => r.Date).ToHashSet();

        return date => !rosteredDuty.Contains(date)
                       && (rosteredOff.Contains(date) || holidays.Contains(date) || (employee.WeeklyOff is { } off && AttendanceService.ToWeekday(date) == off));
    }

    private async Task<decimal> AvailableAsync(Guid employeeId, Guid leaveTypeId, int year, Guid? excludeRequestId, CancellationToken ct)
    {
        var balance = await db.Set<LeaveBalance>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstOrDefaultAsync(b => b.EmployeeId == employeeId && b.LeaveTypeId == leaveTypeId && b.Year == year, ct);
        var pending = await db.Set<LeaveRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(l => l.EmployeeId == employeeId && l.LeaveTypeId == leaveTypeId && l.Status == ApprovalStatus.Pending
                        && l.FromDate.Year == year && l.Id != excludeRequestId)
            .SumAsync(l => l.Days, ct);
        return LeaveEntitlement.Available(balance?.Closing ?? 0, pending);
    }

    private async Task<LeaveBalance> GetOrCreateBalanceAsync(Guid facilityId, Guid employeeId, Guid leaveTypeId, int year, CancellationToken ct)
    {
        var balance = await db.Set<LeaveBalance>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(b => b.EmployeeId == employeeId && b.LeaveTypeId == leaveTypeId && b.Year == year, ct);
        if (balance is null)
        {
            balance = new LeaveBalance { FacilityId = facilityId, EmployeeId = employeeId, LeaveTypeId = leaveTypeId, Year = year };
            db.Add(balance);
        }

        return balance;
    }

    private async Task RecomputePastDaysAsync(LeaveRequest leave, CancellationToken ct)
    {
        var yesterday = time.LocalToday().AddDays(-1);
        if (leave.FromDate > yesterday)
        {
            return;
        }

        var to = leave.ToDate < yesterday ? leave.ToDate : yesterday;
        var from = to.AddDays(-(AttendanceService.MaxRangeDays - 1)) > leave.FromDate ? to.AddDays(-(AttendanceService.MaxRangeDays - 1)) : leave.FromDate;
        await attendance.ComputeAsync(leave.FacilityId, from, to, [leave.EmployeeId], ct);
    }
}
