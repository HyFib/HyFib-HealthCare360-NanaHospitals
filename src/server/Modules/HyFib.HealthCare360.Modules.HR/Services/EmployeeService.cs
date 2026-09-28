using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Employee master (HR-01/02): code allocation, link validation (department, designation, manager without loops,
/// user account, practitioner, terminal PIN), encryption of bank/PAN details and separation.
/// </summary>
public sealed class EmployeeService(
    HospitalDbContext db,
    INumberSeries numbers,
    IFieldProtector protector,
    ICurrentUser currentUser,
    INotificationService notifications,
    IAuditLogger audit,
    TimeProvider time)
{
    public const string EmployeeSeries = "hr.employee";
    public const string EmployeeFormat = "{FAC}EMP{SEQ:5}";

    public async Task<Result<EmployeeCreated>> CreateAsync(SaveEmployeeRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility before adding employees.");
        }

        var employee = new Employee { FacilityId = facilityId };
        var problem = await ValidateReferencesAsync(employee, request, ct)
                      ?? await CheckIdentifierConflictsAsync(null, request.Pan, request.Bank?.AccountNumber, confirmSharedAccount: false, ct);
        if (problem is not null)
        {
            return problem;
        }

        Apply(employee, request);
        if (request.Bank is { } bank)
        {
            ApplyBank(employee, bank.BankName, bank.AccountNumber, bank.Ifsc);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        employee.EmployeeCode = await numbers.NextAsync(EmployeeSeries, facilityId, EmployeeFormat, NumberResetPolicy.Never, ct);
        employee.MarkJoined();
        db.Add(employee);
        await db.SaveChangesAsync(ct);
        await LinkUnmatchedPunchesAsync(employee, ct);
        await transaction.CommitAsync(ct);

        return new EmployeeCreated(employee.Id, employee.EmployeeCode);
    }

    public async Task<Result> UpdateAsync(Guid id, SaveEmployeeRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), id);
        }

        if (employee.IsSeparated)
        {
            return Error.Rule("employee.separated", "Separated employees are kept for records and cannot be edited.");
        }

        if (request.Version is not { } version)
        {
            return Error.Field("version", "Reload the employee and try again.");
        }

        var problem = await ValidateReferencesAsync(employee, request, ct)
                      ?? await CheckIdentifierConflictsAsync(id, request.Pan, null, confirmSharedAccount: false, ct);
        if (problem is not null)
        {
            return problem;
        }

        db.SetExpectedVersion(employee, version);
        var previousPin = employee.BiometricPin;
        Apply(employee, request);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        if (employee.BiometricPin is not null && employee.BiometricPin != previousPin)
        {
            await LinkUnmatchedPunchesAsync(employee, ct);
        }

        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public async Task<Result> UpdateBankDetailsAsync(Guid id, UpdateBankDetailsRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), id);
        }

        var problem = await CheckIdentifierConflictsAsync(id, null, request.AccountNumber, request.ConfirmSharedAccount, ct);
        if (problem is not null)
        {
            return problem;
        }

        db.SetExpectedVersion(employee, request.Version);
        ApplyBank(employee, request.BankName, request.AccountNumber, request.Ifsc);
        audit.Add(new AuditEntry(AuditCategories.Security, "employee.bank_changed", $"Salary bank account of {employee.EmployeeCode} changed to {employee.BankAccountMasked}")
        {
            EntityType = nameof(Employee),
            EntityId = employee.Id.ToString(),
            Severity = AuditSeverity.Warning,
        });
        await db.SaveChangesAsync(ct);

        // Payroll-diversion fraud usually starts with a quiet bank change, so the employee is always told.
        if (employee.UserId is { } userId && userId != currentUser.UserId)
        {
            await notifications.NotifyUsersAsync([userId], "Your salary bank account was changed",
                $"Salary will now be paid to account {employee.BankAccountMasked}. Contact HR immediately if you did not ask for this.",
                "/hr/me/profile", "security", "warning", ct);
        }

        return Result.Success();
    }

    public async Task<Result> SeparateAsync(Guid id, SeparateEmployeeRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), id);
        }

        db.SetExpectedVersion(employee, request.Version);
        var result = employee.Separate(request.LastWorkingDay, request.Reason, request.Note);
        if (result.IsFailure)
        {
            return result;
        }

        // Nothing may be rostered or pending beyond the last working day.
        var lastDay = request.LastWorkingDay;
        foreach (var entry in await db.Set<RosterEntry>().Where(r => r.EmployeeId == id && r.Date > lastDay).ToListAsync(ct))
        {
            db.Remove(entry);
        }

        var now = time.GetUtcNow();
        foreach (var leave in await db.Set<LeaveRequest>().Where(l => l.EmployeeId == id && l.Status == ApprovalStatus.Pending).ToListAsync(ct))
        {
            leave.Cancel("Employee separated.", time.LocalToday(), byHr: true, now);
        }

        await db.SaveChangesAsync(ct);

        if (employee.UserId is not null)
        {
            await notifications.NotifyPermissionHoldersAsync(PlatformPermissions.UsersManage, employee.FacilityId,
                "Disable the user account of a separated employee",
                $"{employee.FullName} ({employee.EmployeeCode}) was separated with effect from {lastDay:dd MMM yyyy}. Deactivate their sign-in.",
                "/admin/users", "security", "warning", ct);
        }

        return Result.Success();
    }

    private async Task<Error?> ValidateReferencesAsync(Employee employee, SaveEmployeeRequest request, CancellationToken ct)
    {
        if (request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId && d.IsActive, ct))
        {
            return Error.Field("departmentId", "Choose an active department.");
        }

        if (request.DesignationId is { } designationId && !await db.Set<Designation>().AnyAsync(d => d.Id == designationId && d.IsActive, ct))
        {
            return Error.Field("designationId", "Choose an active designation.");
        }

        if (request.DefaultShiftId is { } shiftId && !await db.Set<Shift>().AnyAsync(s => s.Id == shiftId && s.IsActive, ct))
        {
            return Error.Field("defaultShiftId", "Choose an active shift.");
        }

        if (request.ReportingManagerId is { } managerId)
        {
            if (managerId == employee.Id)
            {
                return Error.Field("reportingManagerId", "An employee cannot report to themselves.");
            }

            var managers = await db.Set<Employee>().Where(e => e.Status != EmployeeStatus.Separated)
                .Select(e => new { e.Id, e.ReportingManagerId })
                .ToDictionaryAsync(e => e.Id, e => e.ReportingManagerId, ct);
            if (!managers.ContainsKey(managerId))
            {
                return Error.Field("reportingManagerId", "Choose a current employee of this facility as reporting manager.");
            }

            if (ReportingChain.CreatesCycle(employee.Id, managerId, id => managers.GetValueOrDefault(id)))
            {
                return Error.Field("reportingManagerId", "This manager (directly or indirectly) reports to this employee.");
            }
        }

        if (request.UserId is { } userId)
        {
            if (!await db.Set<User>().AnyAsync(u => u.Id == userId && u.Type == UserType.Staff, ct))
            {
                return Error.Field("userId", "Choose a staff user account.");
            }

            var holder = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.UserId == userId && e.Id != employee.Id).Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("employee.user_linked", $"This user account is already linked to employee {holder}.");
            }
        }

        if (request.PractitionerId is { } practitionerId)
        {
            if (!await db.Set<Practitioner>().AnyAsync(p => p.Id == practitionerId, ct))
            {
                return Error.Field("practitionerId", "Choose a practitioner from the doctors' master.");
            }

            var holder = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.PractitionerId == practitionerId && e.Id != employee.Id).Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("employee.practitioner_linked", $"This practitioner is already linked to employee {holder}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.BiometricPin))
        {
            var pin = request.BiometricPin.Trim();
            var holder = await db.Set<Employee>()
                .Where(e => e.FacilityId == employee.FacilityId && e.BiometricPin == pin && e.Id != employee.Id)
                .Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("employee.pin_in_use", $"Terminal PIN {pin} is already assigned to {holder}.");
            }
        }

        return null;
    }

    private async Task<Error?> CheckIdentifierConflictsAsync(Guid? excludeId, string? pan, string? accountNumber, bool confirmSharedAccount, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(pan))
        {
            var index = protector.BlindIndex(HrIdentifiers.Normalize(pan));
            var holder = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.PanIndex == index && e.Id != excludeId && e.Status != EmployeeStatus.Separated)
                .Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("employee.pan_in_use", $"This PAN is already recorded for current employee {holder}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(accountNumber) && !confirmSharedAccount)
        {
            var index = protector.BlindIndex(HrIdentifiers.Normalize(accountNumber));
            var holder = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.BankAccountIndex == index && e.Id != excludeId && e.Status != EmployeeStatus.Separated)
                .Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("employee.bank_account_shared",
                    $"Salary for current employee {holder} is already paid into this account. Confirm only if the account is genuinely shared.",
                    new { requiresConfirmation = true });
            }
        }

        return null;
    }

    private void Apply(Employee employee, SaveEmployeeRequest request)
    {
        employee.SetName(request.Title, request.FirstName, request.MiddleName, request.LastName);
        employee.Gender = request.Gender;
        employee.DateOfBirth = request.DateOfBirth;
        employee.Phone = Phone.Normalize(request.Phone);
        employee.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim().ToLowerInvariant();
        employee.Address = request.Address ?? new Address();
        employee.EmergencyContactName = Clean(request.EmergencyContactName);
        employee.EmergencyContactPhone = Phone.Normalize(request.EmergencyContactPhone);
        employee.DepartmentId = request.DepartmentId;
        employee.DesignationId = request.DesignationId;
        employee.EmploymentType = request.EmploymentType;
        employee.Status = request.Status;
        employee.JoiningDate = request.JoiningDate;
        employee.ConfirmationDate = request.ConfirmationDate;
        employee.ReportingManagerId = request.ReportingManagerId;
        employee.UserId = request.UserId;
        employee.PractitionerId = request.PractitionerId;
        employee.BiometricPin = Clean(request.BiometricPin);
        employee.DefaultShiftId = request.DefaultShiftId;
        employee.WeeklyOff = request.WeeklyOff;
        employee.IsAttendanceExempt = request.IsAttendanceExempt;
        employee.PaymentMode = request.PaymentMode;
        employee.Uan = string.IsNullOrWhiteSpace(request.Uan) ? null : HrIdentifiers.Normalize(request.Uan);
        employee.PfNumber = Clean(request.PfNumber)?.ToUpperInvariant();
        employee.EsiNumber = string.IsNullOrWhiteSpace(request.EsiNumber) ? null : HrIdentifiers.Normalize(request.EsiNumber);
        employee.PhotoFileId = request.PhotoFileId;
        employee.Notes = Clean(request.Notes);

        // null = leave unchanged, "" = remove, value = replace.
        if (request.Pan is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Pan))
            {
                employee.PanCipher = employee.PanIndex = employee.PanMasked = null;
            }
            else
            {
                var pan = HrIdentifiers.Normalize(request.Pan);
                employee.PanCipher = protector.Protect(pan);
                employee.PanIndex = protector.BlindIndex(pan);
                employee.PanMasked = HrIdentifiers.MaskPan(pan);
            }
        }
    }

    private void ApplyBank(Employee employee, string? bankName, string accountNumber, string ifsc)
    {
        var account = HrIdentifiers.Normalize(accountNumber);
        var code = HrIdentifiers.Normalize(ifsc);
        employee.BankName = Clean(bankName);
        employee.BankAccountCipher = protector.Protect(account);
        employee.BankAccountIndex = protector.BlindIndex(account);
        employee.BankAccountMasked = HrIdentifiers.MaskAccount(account);
        employee.BankIfscCipher = protector.Protect(code);
        employee.BankIfscMasked = HrIdentifiers.MaskIfsc(code);
    }

    /// <summary>Punches received before HR assigned this PIN are attached to the employee now.</summary>
    private Task<int> LinkUnmatchedPunchesAsync(Employee employee, CancellationToken ct) =>
        employee.BiometricPin is not { } pin
            ? Task.FromResult(0)
            : db.Set<AttendancePunch>()
                .Where(p => p.FacilityId == employee.FacilityId && p.EmployeeId == null && p.Pin == pin)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.EmployeeId, employee.Id), ct);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
