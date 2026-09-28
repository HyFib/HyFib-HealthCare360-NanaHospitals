using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Features.Employees;

public sealed record DesignationDto(Guid Id, string Code, string Name, StaffCategory Category, int Grade, bool IsActive, uint Version);

public sealed record SaveDesignationRequest(string Code, string Name, StaffCategory Category, int Grade, bool IsActive, uint? Version);

public sealed record EmployeeListItem(
    Guid Id,
    string EmployeeCode,
    string FullName,
    Gender Gender,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? DesignationId,
    string? DesignationName,
    EmploymentType EmploymentType,
    EmployeeStatus Status,
    DateOnly JoiningDate,
    string? PhoneMasked,
    bool HasUserAccount,
    string? BiometricPin);

public sealed record EmployeeDetail(
    Guid Id,
    string EmployeeCode,
    string? Title,
    string FirstName,
    string? MiddleName,
    string? LastName,
    string FullName,
    Gender Gender,
    DateOnly? DateOfBirth,
    string? Phone,
    string? Email,
    Address Address,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? DesignationId,
    string? DesignationName,
    EmploymentType EmploymentType,
    EmployeeStatus Status,
    DateOnly JoiningDate,
    DateOnly? ConfirmationDate,
    DateOnly? LeavingDate,
    SeparationReason? SeparationReason,
    string? SeparationNote,
    Guid? ReportingManagerId,
    string? ReportingManagerName,
    Guid? UserId,
    Guid? PractitionerId,
    string? BiometricPin,
    Guid? DefaultShiftId,
    Weekday? WeeklyOff,
    bool IsAttendanceExempt,
    SalaryPaymentMode PaymentMode,
    string? BankName,
    string? BankAccountMasked,
    string? BankIfscMasked,
    string? PanMasked,
    string? Uan,
    string? PfNumber,
    string? EsiNumber,
    Guid? PhotoFileId,
    string? Notes,
    uint Version);

public sealed record BankDetailsInput(string? BankName, string AccountNumber, string Ifsc);

/// <summary>
/// Create/update payload. <see cref="Pan"/>: null keeps the stored value, "" removes it. Bank details are accepted
/// at creation only; later changes go through the dedicated (separately audited) bank-details endpoint.
/// </summary>
public sealed record SaveEmployeeRequest(
    string? Title,
    string FirstName,
    string? MiddleName,
    string? LastName,
    Gender Gender,
    DateOnly? DateOfBirth,
    string? Phone,
    string? Email,
    Address? Address,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    Guid? DepartmentId,
    Guid? DesignationId,
    EmploymentType EmploymentType,
    EmployeeStatus Status,
    DateOnly JoiningDate,
    DateOnly? ConfirmationDate,
    Guid? ReportingManagerId,
    Guid? UserId,
    Guid? PractitionerId,
    string? BiometricPin,
    Guid? DefaultShiftId,
    Weekday? WeeklyOff,
    bool IsAttendanceExempt,
    SalaryPaymentMode PaymentMode,
    string? Pan,
    string? Uan,
    string? PfNumber,
    string? EsiNumber,
    Guid? PhotoFileId,
    string? Notes,
    BankDetailsInput? Bank,
    uint? Version);

public sealed record UpdateBankDetailsRequest(string? BankName, string AccountNumber, string Ifsc, bool ConfirmSharedAccount, uint Version);

public sealed record SeparateEmployeeRequest(DateOnly LastWorkingDay, SeparationReason Reason, string? Note, uint Version);

public sealed record EmployeeCreated(Guid Id, string EmployeeCode);

internal static class EmployeeRules
{
    public static bool ValidName(string? value) => value is null || value.All(c => char.IsLetter(c) || c is ' ' or '.' or '\'' or '-');

    public static bool ValidCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool ValidPfNumber(string? value) =>
        string.IsNullOrWhiteSpace(value) || (value.Length <= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-'));
}

public sealed class SaveDesignationRequestValidator : AbstractValidator<SaveDesignationRequest>
{
    public SaveDesignationRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Must(EmployeeRules.ValidCode).WithMessage("Use letters, digits, '-' or '_'.");
        RuleFor(x => x.Name).NotEmpty().SafeText(100);
        RuleFor(x => x.Grade).InclusiveBetween(0, 99);
    }
}

public sealed class SaveEmployeeRequestValidator : AbstractValidator<SaveEmployeeRequest>
{
    public SaveEmployeeRequestValidator(TimeProvider time)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

        RuleFor(x => x.FirstName).NotEmpty().WithMessage("First name is required.").MaximumLength(100)
            .Must(EmployeeRules.ValidName).WithMessage("Use letters, spaces, dots, apostrophes or hyphens.");
        RuleFor(x => x.MiddleName).MaximumLength(100).Must(EmployeeRules.ValidName).WithMessage("Use letters only.");
        RuleFor(x => x.LastName).MaximumLength(100).Must(EmployeeRules.ValidName).WithMessage("Use letters only.");
        RuleFor(x => x.Title).MaximumLength(16);
        RuleFor(x => x.DateOfBirth)
            .Must(d => d is null || (d <= today && d.Value.Year >= 1930)).WithMessage("Enter a valid date of birth.");
        RuleFor(x => x)
            .Must(x => x.DateOfBirth is null || x.DateOfBirth.Value.AddYears(18) <= x.JoiningDate)
            .WithName("dateOfBirth").WithMessage("Employees must be at least 18 years old on the joining date.");
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.EmergencyContactPhone).PhoneNumber();
        RuleFor(x => x.Email).EmailAddress().MaximumLength(256).When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.Address!.PinCode).PinCode().When(x => !string.IsNullOrWhiteSpace(x.Address?.PinCode));
        RuleFor(x => x.EmergencyContactName).SafeText(200);
        RuleFor(x => x.JoiningDate)
            .Must(d => d.Year >= 1960 && d <= today.AddYears(1)).WithMessage("Enter a valid joining date.");
        RuleFor(x => x.ConfirmationDate)
            .Must((x, d) => d is null || d >= x.JoiningDate).WithMessage("Confirmation cannot be before joining.");
        RuleFor(x => x.Status).NotEqual(EmployeeStatus.Separated).WithMessage("Use the separation action to separate an employee.");
        RuleFor(x => x.BiometricPin)
            .Must(p => string.IsNullOrWhiteSpace(p) || ZkTecoAttLogParser.IsValidPin(p.Trim()))
            .WithMessage($"Use up to {ZkTecoAttLogParser.MaxPinLength} letters or digits, exactly as enrolled on the terminal.");
        RuleFor(x => x.Pan).Must(p => string.IsNullOrWhiteSpace(p) || HrIdentifiers.IsValidPan(p)).WithMessage("Enter a valid PAN (ABCDE1234F).");
        RuleFor(x => x.Uan).Must(u => string.IsNullOrWhiteSpace(u) || HrIdentifiers.IsValidUan(u)).WithMessage("UAN must have 12 digits.");
        RuleFor(x => x.EsiNumber).Must(e => string.IsNullOrWhiteSpace(e) || HrIdentifiers.IsValidEsiNumber(e)).WithMessage("Enter a valid ESI insurance number.");
        RuleFor(x => x.PfNumber).Must(EmployeeRules.ValidPfNumber).WithMessage("Enter a valid PF member number.");
        RuleFor(x => x.Notes).SafeText(1000);
        RuleFor(x => x.Bank!).SetValidator(new BankDetailsInputValidator()).When(x => x.Bank is not null);
    }
}

public sealed class BankDetailsInputValidator : AbstractValidator<BankDetailsInput>
{
    public BankDetailsInputValidator()
    {
        RuleFor(x => x.BankName).SafeText(100);
        RuleFor(x => x.AccountNumber).Must(HrIdentifiers.IsValidBankAccount).WithMessage("Account numbers have 9 to 18 digits.");
        RuleFor(x => x.Ifsc).Must(HrIdentifiers.IsValidIfsc).WithMessage("Enter a valid IFSC (for example SBIN0001234).");
    }
}

public sealed class UpdateBankDetailsRequestValidator : AbstractValidator<UpdateBankDetailsRequest>
{
    public UpdateBankDetailsRequestValidator()
    {
        RuleFor(x => x.BankName).SafeText(100);
        RuleFor(x => x.AccountNumber).Must(HrIdentifiers.IsValidBankAccount).WithMessage("Account numbers have 9 to 18 digits.");
        RuleFor(x => x.Ifsc).Must(HrIdentifiers.IsValidIfsc).WithMessage("Enter a valid IFSC (for example SBIN0001234).");
    }
}

public sealed class SeparateEmployeeRequestValidator : AbstractValidator<SeparateEmployeeRequest>
{
    public SeparateEmployeeRequestValidator() => RuleFor(x => x.Note).SafeText(500);
}
