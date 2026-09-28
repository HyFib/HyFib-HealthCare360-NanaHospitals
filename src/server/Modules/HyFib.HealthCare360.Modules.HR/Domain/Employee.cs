namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum EmploymentType
{
    Permanent = 0,
    Contract,
    Consultant,
    Intern,
    Trainee,
}

public enum EmployeeStatus
{
    Active = 0,
    Probation,
    OnNotice,
    Suspended,

    /// <summary>Resigned, retired, terminated… — kept for statutory record retention, never deleted.</summary>
    Separated,
}

public enum SeparationReason
{
    Resignation = 0,
    Termination,
    Retirement,
    EndOfContract,
    Death,
    Absconding,
    Other,
}

public enum SalaryPaymentMode
{
    BankTransfer = 0,
    Cheque,
    Cash,
}

/// <summary>
/// A person employed (or engaged as consultant/intern) by a facility. Bank account, IFSC and PAN are stored encrypted
/// with <c>IFieldProtector</c> and shown only masked; blind indexes on account and PAN let HR detect two current
/// employees sharing a salary account (a classic "ghost employee" payroll fraud) without decrypting anything.
/// </summary>
public sealed class Employee : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}EMP{SEQ:5}", e.g. NHEMP00042.</summary>
    public string EmployeeCode { get; set; } = string.Empty;

    public string? Title { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }

    /// <summary>Denormalised "First Middle Last" for search and payslips.</summary>
    public string FullName { get; set; } = string.Empty;

    public Gender Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }

    /// <summary>E.164 mobile number.</summary>
    public string? Phone { get; set; }

    public string? Email { get; set; }
    public Address Address { get; set; } = new();
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }

    public Guid? DepartmentId { get; set; }
    public Guid? DesignationId { get; set; }
    public EmploymentType EmploymentType { get; set; }
    public EmployeeStatus Status { get; set; }
    public DateOnly JoiningDate { get; set; }
    public DateOnly? ConfirmationDate { get; set; }

    /// <summary>Last working day once separated.</summary>
    public DateOnly? LeavingDate { get; set; }

    public SeparationReason? SeparationReason { get; set; }
    public string? SeparationNote { get; set; }
    public Guid? ReportingManagerId { get; set; }

    /// <summary>Sign-in account (Platform user) used for self-service and approvals.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Clinician master record when the employee is a doctor/nurse/therapist.</summary>
    public Guid? PractitionerId { get; set; }

    /// <summary>User id enrolled on the biometric terminals ("PIN" in ZKTeco terms); unique per facility.</summary>
    public string? BiometricPin { get; set; }

    /// <summary>Shift assumed on days without a roster entry (typical for administrative staff).</summary>
    public Guid? DefaultShiftId { get; set; }

    /// <summary>Fixed weekly off; null when offs are rostered.</summary>
    public Weekday? WeeklyOff { get; set; } = Weekday.Sunday;

    /// <summary>Visiting consultants and senior staff who do not punch; payroll ignores attendance-based LOP for them.</summary>
    public bool IsAttendanceExempt { get; set; }

    public SalaryPaymentMode PaymentMode { get; set; }
    public string? BankName { get; set; }

    [SensitiveData]
    public string? BankAccountCipher { get; set; }

    [SensitiveData]
    public string? BankAccountIndex { get; set; }

    public string? BankAccountMasked { get; set; }

    [SensitiveData]
    public string? BankIfscCipher { get; set; }

    public string? BankIfscMasked { get; set; }

    [SensitiveData]
    public string? PanCipher { get; set; }

    [SensitiveData]
    public string? PanIndex { get; set; }

    public string? PanMasked { get; set; }

    /// <summary>EPFO Universal Account Number (12 digits).</summary>
    [SensitiveData]
    public string? Uan { get; set; }

    [SensitiveData]
    public string? PfNumber { get; set; }

    /// <summary>ESIC insurance (IP) number.</summary>
    [SensitiveData]
    public string? EsiNumber { get; set; }

    public Guid? PhotoFileId { get; set; }
    public string? Notes { get; set; }

    public bool IsSeparated => Status == EmployeeStatus.Separated;

    public void SetName(string? title, string firstName, string? middleName, string? lastName)
    {
        Title = Clean(title);
        FirstName = firstName.Trim();
        MiddleName = Clean(middleName);
        LastName = Clean(lastName);
        FullName = string.Join(' ', new[] { FirstName, MiddleName, LastName }.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    /// <summary>True when the employee is on the rolls on <paramref name="date"/> (joined and not yet left).</summary>
    public bool IsEmployedOn(DateOnly date) => date >= JoiningDate && (LeavingDate is null || date <= LeavingDate);

    /// <summary>Call once the employee code is allocated; lets other modules (canteen credit, user provisioning) react.</summary>
    public void MarkJoined() => Raise(new EmployeeJoined(Id, EmployeeCode, FacilityId, UserId));

    public Result Separate(DateOnly lastWorkingDay, SeparationReason reason, string? note)
    {
        if (IsSeparated)
        {
            return Error.Rule("employee.already_separated", "This employee has already been separated.");
        }

        if (lastWorkingDay < JoiningDate)
        {
            return Error.Field("lastWorkingDay", "The last working day cannot be before the joining date.");
        }

        Status = EmployeeStatus.Separated;
        LeavingDate = lastWorkingDay;
        SeparationReason = reason;
        SeparationNote = Clean(note);
        Raise(new EmployeeSeparated(Id, EmployeeCode, FacilityId, UserId, lastWorkingDay, reason));
        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record EmployeeJoined(Guid EmployeeId, string EmployeeCode, Guid FacilityId, Guid? UserId) : DomainEvent;

/// <summary>Raised on separation so access can be revoked and dues (canteen credit, advances) settled.</summary>
public sealed record EmployeeSeparated(Guid EmployeeId, string EmployeeCode, Guid FacilityId, Guid? UserId, DateOnly LastWorkingDay, SeparationReason Reason) : DomainEvent;
