using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Attendance;
using HyFib.HealthCare360.Modules.HR.Features.Credentials;
using HyFib.HealthCare360.Modules.HR.Features.Leave;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.HR.Features.Rostering;
using HyFib.HealthCare360.Modules.HR.Services;

namespace HyFib.HealthCare360.Modules.HR.Features.SelfService;

/// <summary>
/// /api/v1/hr/me — employee self-service for every staff user linked to an employee record: profile, roster,
/// attendance, credentials, leave, regularisation and payslips; plus "my team" approvals for reporting managers.
/// Everything is resolved from the signed-in user, never from ids in the request.
/// </summary>
internal static class SelfServiceEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/hr/me").WithTags("HR self-service").RequirePermission(HRPermissions.Self);

        me.MapGet("/profile", Profile);
        me.MapGet("/roster", Roster);
        me.MapGet("/attendance", Attendance);
        me.MapGet("/credentials", Credentials);

        me.MapGet("/leave/balances", async (int? year, CurrentEmployee current, LeaveService leave, TimeProvider time, CancellationToken ct) =>
            await WithEmployeeAsync(current, ct, async id => TypedResults.Ok(await leave.BalancesAsync(id, year ?? time.LocalToday().Year, ct))));
        me.MapGet("/leave/requests", async (CurrentEmployee current, HospitalDbContext db, CancellationToken ct) =>
            await WithEmployeeAsync(current, ct, async id => TypedResults.Ok(await LeaveEndpoints
                .Project(db.Set<LeaveRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(l => l.EmployeeId == id), db)
                .Take(100).ToListAsync(ct))));
        me.MapPost("/leave/requests", async (ApplyLeaveRequest r, CurrentEmployee current, LeaveService leave, CancellationToken ct) =>
                await WithEmployeeAsync(current, ct, async id => (await leave.ApplyAsync(id, r, ct)).ToCreated(requestId => $"/api/v1/hr/me/leave/requests/{requestId}")))
            .WithValidation<ApplyLeaveRequest>();
        me.MapPost("/leave/requests/{requestId:guid}/cancel", async (Guid requestId, CancelLeaveRequest r, CurrentEmployee current, LeaveService leave, CancellationToken ct) =>
                await WithEmployeeAsync(current, ct, async id => (await leave.CancelAsync(requestId, r.Reason, id, ct)).ToNoContent()))
            .WithValidation<CancelLeaveRequest>();

        me.MapGet("/attendance/regularisations", async (CurrentEmployee current, HospitalDbContext db, CancellationToken ct) =>
            await WithEmployeeAsync(current, ct, async id => TypedResults.Ok(await AttendanceEndpoints
                .ProjectRegularisations(db.Set<AttendanceRegularisation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking().Where(r => r.EmployeeId == id), db)
                .Take(100).ToListAsync(ct))));
        me.MapPost("/attendance/regularisations", async (RequestRegularisationRequest r, CurrentEmployee current, RegularisationService service, CancellationToken ct) =>
                await WithEmployeeAsync(current, ct, async id => (await service.RequestAsync(id, r, ct)).ToCreated(requestId => $"/api/v1/hr/me/attendance/regularisations/{requestId}")))
            .WithValidation<RequestRegularisationRequest>();
        me.MapPost("/attendance/regularisations/{requestId:guid}/cancel", async (Guid requestId, CurrentEmployee current, RegularisationService service, CancellationToken ct) =>
            await WithEmployeeAsync(current, ct, async id => (await service.CancelOwnAsync(requestId, id, ct)).ToNoContent()));

        me.MapGet("/payslips", Payslips);
        me.MapGet("/payslips/{payslipId:guid}", async (Guid payslipId, CurrentEmployee current, HospitalDbContext db, CancellationToken ct) =>
            await WithEmployeeAsync(current, ct, async id =>
                await PayrollEndpoints.LoadPayslipAsync(db, p => p.Id == payslipId && p.EmployeeId == id, visibleToEmployeeOnly: true, ct) is { } payslip
                    ? TypedResults.Ok(payslip)
                    : Error.NotFound(nameof(Payslip), payslipId).ToProblem()));

        // Reporting managers: requests from their direct reportees (decisions re-check the reporting line).
        me.MapGet("/team/leave-requests", async (ApprovalStatus? status, CurrentEmployee current, HospitalDbContext db, CancellationToken ct) =>
            await WithEmployeeAsync(current, ct, async id =>
            {
                var team = Reportees(db, id);
                var requests = db.Set<LeaveRequest>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                    .Where(l => (status == null || l.Status == status) && team.Contains(l.EmployeeId));
                return TypedResults.Ok(await LeaveEndpoints.Project(requests, db).Take(200).ToListAsync(ct));
            }));
        me.MapPost("/team/leave-requests/{requestId:guid}/approve", async (Guid requestId, DecisionRequest r, LeaveService leave, CancellationToken ct) =>
                (await leave.DecideAsync(requestId, approve: true, r.Note, ct)).ToNoContent())
            .WithValidation<DecisionRequest>();
        me.MapPost("/team/leave-requests/{requestId:guid}/reject", async (Guid requestId, DecisionRequest r, LeaveService leave, CancellationToken ct) =>
                (await leave.DecideAsync(requestId, approve: false, r.Note, ct)).ToNoContent())
            .WithValidation<DecisionRequest>();
        me.MapGet("/team/regularisations", async (ApprovalStatus? status, CurrentEmployee current, HospitalDbContext db, CancellationToken ct) =>
            await WithEmployeeAsync(current, ct, async id =>
            {
                var team = Reportees(db, id);
                var requests = db.Set<AttendanceRegularisation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                    .Where(r => (status == null || r.Status == status) && team.Contains(r.EmployeeId));
                return TypedResults.Ok(await AttendanceEndpoints.ProjectRegularisations(requests, db).Take(200).ToListAsync(ct));
            }));
        me.MapPost("/team/regularisations/{requestId:guid}/approve", async (Guid requestId, DecisionRequest r, RegularisationService service, CancellationToken ct) =>
                (await service.DecideAsync(requestId, approve: true, r.Note, ct)).ToNoContent())
            .WithValidation<DecisionRequest>();
        me.MapPost("/team/regularisations/{requestId:guid}/reject", async (Guid requestId, DecisionRequest r, RegularisationService service, CancellationToken ct) =>
                (await service.DecideAsync(requestId, approve: false, r.Note, ct)).ToNoContent())
            .WithValidation<DecisionRequest>();
    }

    /// <summary>Ids of the manager's current direct reportees (in any facility).</summary>
    private static IQueryable<Guid> Reportees(HospitalDbContext db, Guid managerEmployeeId) =>
        db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(e => e.ReportingManagerId == managerEmployeeId && e.Status != EmployeeStatus.Separated)
            .Select(e => e.Id);

    private static async Task<IResult> WithEmployeeAsync(CurrentEmployee current, CancellationToken ct, Func<Guid, Task<IResult>> action)
    {
        var employee = await current.IdAsync(ct);
        return employee.IsFailure ? employee.Error!.ToProblem() : await action(employee.Value);
    }

    private static Task<IResult> Profile(CurrentEmployee current, HospitalDbContext db, CancellationToken ct) =>
        WithEmployeeAsync(current, ct, async id =>
        {
            var employees = db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking();
            var profile = await (
                    from e in employees
                    join d in db.Set<Department>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on e.DepartmentId equals d.Id into departments
                    from d in departments.DefaultIfEmpty()
                    join g in db.Set<Designation>() on e.DesignationId equals g.Id into designations
                    from g in designations.DefaultIfEmpty()
                    join m in employees on e.ReportingManagerId equals m.Id into managers
                    from m in managers.DefaultIfEmpty()
                    where e.Id == id
                    select new MyProfileDto(
                        e.Id, e.EmployeeCode, e.FullName, g != null ? g.Name : null, d != null ? d.Name : null, e.EmploymentType, e.Status, e.JoiningDate,
                        e.Phone, e.Email, m != null ? m.FullName : null, e.BiometricPin, e.PaymentMode, e.BankName, e.BankAccountMasked, e.BankIfscMasked,
                        e.PanMasked, e.Uan, e.PfNumber, e.EsiNumber, employees.Count(r => r.ReportingManagerId == e.Id && r.Status != EmployeeStatus.Separated)))
                .FirstAsync(ct);
            return TypedResults.Ok(profile);
        });

    private static Task<IResult> Roster(DateOnly? from, DateOnly? to, CurrentEmployee current, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        WithEmployeeAsync(current, ct, async id =>
        {
            var start = from ?? time.LocalToday();
            var end = to ?? start.AddDays(13);
            if (end < start || end.DayNumber - start.DayNumber > 62)
            {
                return Error.Field("to", "Choose a range of at most 62 days.").ToProblem();
            }

            var rows = await (
                    from r in db.Set<RosterEntry>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                    join e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on r.EmployeeId equals e.Id
                    join s in db.Set<Shift>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on r.ShiftId equals s.Id into shifts
                    from s in shifts.DefaultIfEmpty()
                    where r.EmployeeId == id && r.Date >= start && r.Date <= end
                    orderby r.Date, s != null ? s.StartTime : TimeOnly.MinValue
                    select new RosterEntryDto(r.Id, r.EmployeeId, e.EmployeeCode, e.FullName, r.Date, r.ShiftId, s != null ? s.Code : null,
                        s != null ? s.Name : null, s != null ? s.Color : null, s != null ? (TimeOnly?)s.StartTime : null, s != null ? (TimeOnly?)s.EndTime : null,
                        r.IsWeekOff, r.Notes))
                .ToListAsync(ct);
            return TypedResults.Ok(rows);
        });

    private static Task<IResult> Attendance(DateOnly? from, DateOnly? to, CurrentEmployee current, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        WithEmployeeAsync(current, ct, async id =>
        {
            var today = time.LocalToday();
            var start = from ?? new DateOnly(today.Year, today.Month, 1);
            var end = to ?? today;
            if (end < start || end.DayNumber - start.DayNumber > 62)
            {
                return Error.Field("to", "Choose a range of at most 62 days.").ToProblem();
            }

            var rows = await (
                    from a in db.Set<AttendanceDay>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                    join e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on a.EmployeeId equals e.Id
                    join s in db.Set<Shift>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on a.ShiftId equals s.Id into shifts
                    from s in shifts.DefaultIfEmpty()
                    where a.EmployeeId == id && a.Date >= start && a.Date <= end
                    orderby a.Date descending
                    select new AttendanceDayDto(a.Id, a.EmployeeId, e.EmployeeCode, e.FullName, a.Date, a.Status, s != null ? s.Code : null, a.FirstIn,
                        a.LastOut, a.WorkedMinutes, a.LateMinutes, a.EarlyExitMinutes, a.OvertimeMinutes, a.PresentDays, a.PaidLeaveDays,
                        a.UnpaidLeaveDays, a.AbsentDays, a.OffDays, a.Flags, a.PunchCount, a.IsLocked))
                .ToListAsync(ct);
            return TypedResults.Ok(rows);
        });

    private static Task<IResult> Credentials(CurrentEmployee current, HospitalDbContext db, TimeProvider time, CancellationToken ct) =>
        WithEmployeeAsync(current, ct, async id =>
        {
            var employee = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(e => e.Id == id).Select(e => new { e.EmployeeCode, e.FullName }).FirstAsync(ct);
            var today = time.LocalToday();
            var credentials = await db.Set<Credential>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(c => c.EmployeeId == id).OrderBy(c => c.ExpiresOn).ToListAsync(ct);
            return TypedResults.Ok(credentials.Select(c => CredentialEndpoints.ToDto(c, employee.EmployeeCode, employee.FullName, today)).ToList());
        });

    /// <summary>Only approved (or paid/locked) months are visible to employees.</summary>
    private static Task<IResult> Payslips(CurrentEmployee current, HospitalDbContext db, CancellationToken ct) =>
        WithEmployeeAsync(current, ct, async id => TypedResults.Ok(await (
                from p in db.Set<Payslip>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                join r in db.Set<PayrollRun>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on p.PayrollRunId equals r.Id
                where p.EmployeeId == id
                      && (r.Status == PayrollRunStatus.Approved || r.Status == PayrollRunStatus.Paid || r.Status == PayrollRunStatus.Locked)
                orderby p.Year descending, p.Month descending
                select new MyPayslipItem(p.Id, p.Year, p.Month, p.GrossEarnings, p.TotalDeductions, p.NetPay, r.Status))
            .Take(60)
            .ToListAsync(ct)));
}
