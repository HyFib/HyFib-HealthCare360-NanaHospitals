using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Employees;
using HyFib.HealthCare360.Platform.Approvals;
using ApprovalStatus = HyFib.HealthCare360.Modules.HR.Domain.ApprovalStatus;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Employee master (HR-01/02): code allocation, link validation (department, designation, manager without loops,
/// user account, practitioner, terminal PIN), encryption of bank/PAN details and separation. Salary payment details (the
/// bank account and the payment mode) are asked for and wait for a second person (<see cref="PaymentDetailsApprovals"/>).
/// </summary>
public sealed class EmployeeService(
    HospitalDbContext db,
    INumberSeries numbers,
    IFieldProtector protector,
    ICurrentUser currentUser,
    INotificationService notifications,
    PaymentDetailsApprovals paymentDetails,
    TimeProvider time)
{
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

        // A new employee starts with the payment mode asked for; a bank account given waits for a second person like any
        // later change, so the first salary is not sent anywhere on one person's word.
        Apply(employee, request);
        employee.PaymentMode = request.PaymentMode;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        employee.EmployeeCode = await numbers.NextAsync(HRNumberSeries.Employee, facilityId, ct);
        employee.MarkJoined();
        db.Add(employee);
        await db.SaveChangesAsync(ct);
        await LinkUnmatchedPunchesAsync(employee, ct);
        Guid? bankApproval = null;
        if (request.Bank is { } bank)
        {
            var asked = Asked(employee, request.PaymentMode, bank.BankName, bank.AccountNumber, bank.Ifsc);
            var requested = await paymentDetails.AskAsync(employee, asked.Details, asked.Index, confirmSharedAccount: false,
                "Bank account given when the employee was added.", ct);
            if (requested.IsFailure)
            {
                return requested.Error!;
            }

            bankApproval = requested.Value.Id;
        }

        await transaction.CommitAsync(ct);
        return new EmployeeCreated(employee.Id, employee.EmployeeCode, bankApproval);
    }

    /// <summary>
    /// Saves the employee at once, except a payment mode different from the one in force, which is asked for a second
    /// person's approval in the same save: then the request it waits in is returned (202), else none.
    /// </summary>
    public async Task<Result<ApprovalRequestDto?>> UpdateAsync(Guid id, SaveEmployeeRequest request, CancellationToken ct)
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

        ApprovalRequestDto? asked = null;
        if (request.PaymentMode != employee.PaymentMode)
        {
            var details = PaymentDetails.Of(employee) with { Mode = request.PaymentMode };
            if (details.Mode == SalaryPaymentMode.BankTransfer && details.AccountCipher is null)
            {
                return Error.Field("paymentMode", "No bank account is on record: give one through the bank details, which are approved with the payment mode.");
            }

            var requested = await paymentDetails.AskAsync(employee, details, null, confirmSharedAccount: false, "Payment mode changed on the employee's record.", ct);
            if (requested.IsFailure)
            {
                return requested.Error!;
            }

            asked = requested.Value;
        }

        await transaction.CommitAsync(ct);
        return asked;
    }

    /// <summary>
    /// Asks a second person to change the employee's salary payment details (maker-checker slice 11): nothing changes until
    /// someone else who approves payroll approves it, never the employee themself.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> RequestPaymentDetailsAsync(Guid id, UpdateBankDetailsRequest request, CancellationToken ct)
    {
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null)
        {
            return Error.NotFound(nameof(Employee), id);
        }

        if (employee.IsSeparated)
        {
            return Error.Rule("employee.separated", "Separated employees are kept for records and cannot be edited.");
        }

        if (employee.Version != request.Version)
        {
            return Error.ConcurrencyConflict;
        }

        var asked = string.IsNullOrWhiteSpace(request.AccountNumber)
            ? (Details: PaymentDetails.Of(employee) with { Mode = request.PaymentMode, BankName = Clean(request.BankName) ?? employee.BankName }, Index: (string?)null)
            : Asked(employee, request.PaymentMode, request.BankName, request.AccountNumber, request.Ifsc ?? string.Empty);
        if (asked.Details.Mode == SalaryPaymentMode.BankTransfer && asked.Details.AccountCipher is null)
        {
            return Error.Field("accountNumber", "Give the account number and IFSC the salary is to be paid into.");
        }

        if (PaymentDetailsChange.Same(PaymentDetails.Of(employee), asked.Details, protector.Unprotect))
        {
            return Error.Field("accountNumber", "Nothing changes: these are the payment details in force.");
        }

        if (asked.Index is not null)
        {
            var problem = await CheckIdentifierConflictsAsync(id, null, request.AccountNumber, request.ConfirmSharedAccount, ct);
            if (problem is not null)
            {
                return problem;
            }
        }

        return await paymentDetails.AskAsync(employee, asked.Details, asked.Index, request.ConfirmSharedAccount, request.Reason, ct);
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
            var indexes = protector.BlindIndexes(HrIdentifiers.Normalize(pan)).ToArray();
            var holder = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.PanIndex != null && indexes.Contains(e.PanIndex) && e.Id != excludeId && e.Status != EmployeeStatus.Separated)
                .Select(e => e.EmployeeCode).FirstOrDefaultAsync(ct);
            if (holder is not null)
            {
                return Error.Conflict("employee.pan_in_use", $"This PAN is already recorded for current employee {holder}.");
            }
        }

        if (!string.IsNullOrWhiteSpace(accountNumber) && !confirmSharedAccount)
        {
            var indexes = protector.BlindIndexes(HrIdentifiers.Normalize(accountNumber)).ToArray();
            var holder = await db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                .Where(e => e.BankAccountIndex != null && indexes.Contains(e.BankAccountIndex) && e.Id != excludeId && e.Status != EmployeeStatus.Separated)
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

    /// <summary>
    /// The payment details asked for, with <paramref name="accountNumber"/> encrypted, and its blind index when it is a new
    /// account; the account already on record keeps its encrypted value, so asking for it again is no change.
    /// </summary>
    private (PaymentDetails Details, string? Index) Asked(Employee employee, SalaryPaymentMode mode, string? bankName, string accountNumber, string ifsc)
    {
        var account = HrIdentifiers.Normalize(accountNumber);
        var code = HrIdentifiers.Normalize(ifsc);
        var sameAccount = string.Equals(protector.Unprotect(employee.BankAccountCipher), account, StringComparison.Ordinal);
        var sameIfsc = string.Equals(protector.Unprotect(employee.BankIfscCipher), code, StringComparison.Ordinal);
        return (new PaymentDetails(
                mode,
                Clean(bankName),
                sameAccount ? employee.BankAccountCipher : protector.Protect(account),
                HrIdentifiers.MaskAccount(account),
                sameIfsc ? employee.BankIfscCipher : protector.Protect(code),
                HrIdentifiers.MaskIfsc(code)),
            sameAccount ? null : protector.BlindIndex(account));
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
