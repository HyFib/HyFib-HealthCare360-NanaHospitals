using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Attendance;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Attendance regularisation with approval (HR-04): employees (or HR on their behalf) request a correction, the
/// reporting manager or HR decides, and approved corrections are fed into the attendance calculation for the day.
/// </summary>
public sealed class RegularisationService(
    HospitalDbContext db,
    ICurrentUser currentUser,
    HrApprovers approvers,
    AttendanceService attendance,
    TimeProvider time)
{
    /// <summary>Requests older than this are refused so payroll months can close.</summary>
    public const int MaxAgeDays = 45;

    public async Task<Result<Guid>> RequestAsync(Guid employeeId, RequestRegularisationRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == employeeId, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), employeeId);
        }

        if (!employee.IsEmployedOn(request.Date))
        {
            return Error.Rule("regularisation.not_employed", "The employee was not on the rolls on that date.");
        }

        var locked = await db.Set<AttendanceDay>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(a => a.EmployeeId == employeeId && a.Date == request.Date && a.IsLocked, ct);
        if (locked)
        {
            return Error.Rule("attendance.locked", "Payroll for that month is locked; attendance can no longer be corrected.");
        }

        var pending = await db.Set<AttendanceRegularisation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .AnyAsync(r => r.EmployeeId == employeeId && r.Date == request.Date && r.Status == ApprovalStatus.Pending, ct);
        if (pending)
        {
            return Error.Conflict("regularisation.pending", "A request for this date is already waiting for approval.");
        }

        var regularisation = new AttendanceRegularisation
        {
            FacilityId = employee.FacilityId,
            EmployeeId = employeeId,
            Date = request.Date,
            Kind = request.Kind,
            RequestedIn = request.RequestedIn,
            RequestedOut = request.RequestedOut,
            Reason = request.Reason.Trim(),
            Status = ApprovalStatus.Pending,
            RequestedByUserId = currentUser.UserId,
        };
        db.Add(regularisation);
        await db.SaveChangesAsync(ct);

        await approvers.NotifyRequestAsync(employeeId, employee.FacilityId, HRPermissions.AttendanceManage,
            "Attendance correction to review",
            $"{employee.FullName} asked to regularise {request.Date:dd MMM yyyy} ({request.Kind}).",
            "/hr/attendance/regularisations", ct);
        return regularisation.Id;
    }

    public async Task<Result> DecideAsync(Guid id, bool approve, string? note, CancellationToken ct)
    {
        var regularisation = await db.Set<AttendanceRegularisation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (regularisation is null)
        {
            return Error.NotFound("Regularisation", id);
        }

        var approver = await approvers.EnsureCanDecideAsync(regularisation.EmployeeId, regularisation.FacilityId, HRPermissions.AttendanceManage, ct);
        if (approver.IsFailure)
        {
            return approver.Error!;
        }

        var now = time.GetUtcNow();
        var result = approve ? regularisation.Approve(approver.Value, now, note) : regularisation.Reject(approver.Value, now, note);
        if (result.IsFailure)
        {
            return result;
        }

        await db.SaveChangesAsync(ct);
        if (approve)
        {
            await attendance.ComputeAsync(regularisation.FacilityId, regularisation.Date, regularisation.Date, [regularisation.EmployeeId], ct);
        }

        await approvers.NotifyEmployeeAsync(regularisation.EmployeeId,
            approve ? "Attendance correction approved" : "Attendance correction rejected",
            $"Your request for {regularisation.Date:dd MMM yyyy} was {(approve ? "approved" : "rejected")}." + (string.IsNullOrWhiteSpace(note) ? string.Empty : $" Note: {note.Trim()}"),
            "/hr/me/attendance", ct);
        return Result.Success();
    }

    /// <summary>Withdraws the employee's own pending request.</summary>
    public async Task<Result> CancelOwnAsync(Guid id, Guid employeeId, CancellationToken ct)
    {
        var regularisation = await db.Set<AttendanceRegularisation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .FirstOrDefaultAsync(r => r.Id == id && r.EmployeeId == employeeId, ct);
        if (regularisation is null)
        {
            return Error.NotFound("Regularisation", id);
        }

        var result = regularisation.Cancel();
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}
