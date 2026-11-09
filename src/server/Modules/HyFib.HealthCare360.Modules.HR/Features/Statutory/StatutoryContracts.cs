using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;

namespace HyFib.HealthCare360.Modules.HR.Features.Statutory;

public sealed record StatutoryProfileDto(
    string? Tan, string? Pan, string? DeductorName, string? Branch, string DeductorType, string? AddressLine1, string? AddressLine2, string? AddressLine3,
    string? City, string? StateCode, string? Pin, string? Email, string? Phone, string? ResponsibleName, string? ResponsibleDesignation, string? ResponsiblePan,
    string? ResponsibleMobile, string? ResponsibleEmail, string? EpfEstablishmentCode, string? EsicEmployerCode, string? SalaryDebitAccountMasked,
    BankFileFormat SalaryBank, uint? Version);

/// <param name="SalaryDebitAccount">The hospital's salary account; empty keeps the one on record.</param>
/// <param name="Version">The version loaded; none on the first save, which creates the profile.</param>
public sealed record SaveStatutoryProfileRequest(
    string Tan, string Pan, string DeductorName, string? Branch, string DeductorType, string AddressLine1, string? AddressLine2, string? AddressLine3,
    string City, string StateCode, string Pin, string? Email, string? Phone, string ResponsibleName, string ResponsibleDesignation, string? ResponsiblePan,
    string? ResponsibleMobile, string? ResponsibleEmail, string? EpfEstablishmentCode, string? EsicEmployerCode, string? SalaryDebitAccount,
    BankFileFormat SalaryBank, uint? Version);

public sealed record TdsChallanDto(
    Guid Id, TdsForm Form, int Year, int Month, string BsrCode, DateOnly DepositedOn, string ChallanSerial, decimal Tax, decimal Surcharge, decimal Cess,
    decimal Interest, decimal Others, decimal LateFee, string MinorHead, decimal Total);

public sealed record SaveTdsChallanRequest(
    TdsForm Form, int Year, int Month, string BsrCode, DateOnly DepositedOn, string ChallanSerial, decimal Tax, decimal Surcharge, decimal Cess,
    decimal Interest, decimal Others, decimal LateFee, string? MinorHead);

public sealed record TaxDeclarationDto(
    Guid EmployeeId, int FinancialYear, TaxRegime Regime, decimal RentPaid, bool MetroCity, decimal Section80C, decimal Section80Ccd1B,
    decimal Section80DSelf, decimal Section80DParents, bool ParentsSenior, decimal Section80E, decimal Section80G, decimal Section80Tta,
    decimal HousingLoanInterest, decimal OtherIncome, decimal PreviousEmployerSalary, decimal PreviousEmployerTds, string? Notes, uint? Version);

/// <param name="Version">The version loaded; none on the first save for the year.</param>
public sealed record SaveTaxDeclarationRequest(
    TaxRegime Regime, decimal RentPaid, bool MetroCity, decimal Section80C, decimal Section80Ccd1B, decimal Section80DSelf, decimal Section80DParents,
    bool ParentsSenior, decimal Section80E, decimal Section80G, decimal Section80Tta, decimal HousingLoanInterest, decimal OtherIncome,
    decimal PreviousEmployerSalary, decimal PreviousEmployerTds, string? Notes, uint? Version);

/// <summary>The year's tax under both regimes, the chosen one first, from salary paid and the present pay projected to March.</summary>
public sealed record TaxComputationDto(
    int FinancialYear, TaxRegime ChosenRegime, int MonthsPaid, int MonthsProjected, decimal TdsDeducted, IncomeTaxComputation Chosen, IncomeTaxComputation Other,
    string RatesSource);

public sealed record GratuityDto(Guid EmployeeId, DateOnly JoiningDate, DateOnly AsOf, GratuityResult Result);

public sealed record BonusRegisterRow(Guid EmployeeId, string EmployeeCode, string EmployeeName, BonusResult Result);

public sealed record BonusRegisterDto(int FinancialYear, decimal RatePercent, decimal MinimumWage, IReadOnlyList<BonusRegisterRow> Rows, decimal Total);

public sealed record SettlementRecoveryInput(string Code, string Description, decimal Amount);

/// <param name="LastWorkingDay">None takes the employee's recorded last working day (or today).</param>
/// <param name="UnpaidSalary">Salary for days worked that no payroll month has paid.</param>
/// <param name="BonusDisqualified">Dismissed for fraud, violence or theft (Payment of Bonus Act s.9): no bonus.</param>
public sealed record FullAndFinalRequest(
    DateOnly? LastWorkingDay, int NoticeDaysRequired, int NoticeDaysServed, bool NoticeWaived, bool EmployerEndedWithoutNotice, decimal UnpaidSalary,
    decimal? LeaveDays, decimal LeaveDivisor, decimal BonusRatePercent, decimal MinimumWage, IReadOnlyList<SettlementRecoveryInput>? Recoveries,
    bool BonusDisqualified = false);

public sealed record FullAndFinalDto(
    Guid EmployeeId, string EmployeeCode, string EmployeeName, DateOnly JoiningDate, DateOnly LastWorkingDay, decimal MonthlyGross, decimal MonthlyWages,
    GratuityResult Gratuity, LeaveEncashmentResult LeaveEncashment, BonusResult Bonus, FullAndFinalStatement Statement);

public sealed class SaveStatutoryProfileRequestValidator : AbstractValidator<SaveStatutoryProfileRequest>
{
    public SaveStatutoryProfileRequestValidator()
    {
        RuleFor(x => x.Tan).Must(HrIdentifiers.IsValidTan).WithMessage("Enter the TAN as four letters, five digits and a letter (CHEN12345E).");
        RuleFor(x => x.Pan).Must(HrIdentifiers.IsValidPan).WithMessage("Enter a valid PAN.");
        RuleFor(x => x.ResponsiblePan).Must(p => string.IsNullOrWhiteSpace(p) || HrIdentifiers.IsValidPan(p)).WithMessage("Enter a valid PAN.");
        RuleFor(x => x.DeductorName).NotEmpty().SafeText(75);
        RuleFor(x => x.Branch).SafeText(75);
        RuleFor(x => x.DeductorType).NotEmpty().Length(1, 2).Must(t => t.All(char.IsAsciiLetterUpper)).WithMessage("Use the deductor type code (K for a company).");
        RuleFor(x => x.AddressLine1).NotEmpty().SafeText(25);
        RuleFor(x => x.AddressLine2).SafeText(25);
        RuleFor(x => x.AddressLine3).SafeText(25);
        RuleFor(x => x.City).NotEmpty().SafeText(25);
        RuleFor(x => x.StateCode).Matches("^[0-9]{2}$").WithMessage("Use the state's two-digit code in the TDS return.");
        RuleFor(x => x.Pin).Matches("^[1-9][0-9]{5}$").WithMessage("Enter a six-digit PIN code.");
        RuleFor(x => x.Email).SafeText(75).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.ResponsibleEmail).SafeText(75).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.ResponsibleEmail));
        RuleFor(x => x.Phone).Matches("^[0-9 +-]{6,15}$").When(x => !string.IsNullOrWhiteSpace(x.Phone));
        RuleFor(x => x.ResponsibleName).NotEmpty().SafeText(75);
        RuleFor(x => x.ResponsibleDesignation).NotEmpty().SafeText(20);
        RuleFor(x => x.ResponsibleMobile).Matches("^[6-9][0-9]{9}$").When(x => !string.IsNullOrWhiteSpace(x.ResponsibleMobile))
            .WithMessage("Enter a ten-digit mobile number.");
        RuleFor(x => x.EpfEstablishmentCode).SafeText(20).Must(c => c is null || c.All(char.IsAsciiLetterOrDigit)).WithMessage("Letters and digits only.");
        RuleFor(x => x.EsicEmployerCode).Must(c => string.IsNullOrWhiteSpace(c) || c.Length == 17 && c.All(char.IsAsciiDigit))
            .WithMessage("The ESIC employer code is 17 digits.");
        RuleFor(x => x.SalaryDebitAccount).Must(a => string.IsNullOrWhiteSpace(a) || HrIdentifiers.IsValidBankAccount(a))
            .WithMessage("Enter the account number (9 to 18 digits).");
        RuleFor(x => x.SalaryBank).IsInEnum();
    }
}

public sealed class SaveTdsChallanRequestValidator : AbstractValidator<SaveTdsChallanRequest>
{
    public SaveTdsChallanRequestValidator()
    {
        RuleFor(x => x.Form).IsInEnum();
        RuleFor(x => x.Year).InclusiveBetween(2020, 2100);
        RuleFor(x => x.Month).InclusiveBetween(1, 12);
        RuleFor(x => x.BsrCode).Matches("^[0-9]{7}$").WithMessage("The bank's BSR code is 7 digits.");
        RuleFor(x => x.ChallanSerial).Matches("^[0-9]{1,5}$").WithMessage("The challan serial number is up to 5 digits.");
        RuleFor(x => x.DepositedOn).Must((r, d) => d >= new DateOnly(r.Year, r.Month, 1)).WithMessage("The deposit cannot be before the month of deduction.");
        RuleFor(x => x.Tax).InclusiveBetween(0, 1_000_000_000);
        RuleFor(x => x.Surcharge).InclusiveBetween(0, 1_000_000_000);
        RuleFor(x => x.Cess).InclusiveBetween(0, 1_000_000_000);
        RuleFor(x => x.Interest).InclusiveBetween(0, 1_000_000_000);
        RuleFor(x => x.Others).InclusiveBetween(0, 1_000_000_000);
        RuleFor(x => x.LateFee).InclusiveBetween(0, 1_000_000_000);
        RuleFor(x => x.MinorHead).Must(m => m is null or "200" or "400").WithMessage("Minor head 200 (TDS payable by the deductor) or 400 (regular assessment).");
    }
}

public sealed class SaveTaxDeclarationRequestValidator : AbstractValidator<SaveTaxDeclarationRequest>
{
    private const string Amount = "Enter an amount from 0 to ₹10 crore.";

    public SaveTaxDeclarationRequestValidator()
    {
        RuleFor(x => x.Regime).IsInEnum();
        RuleFor(x => x.RentPaid).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.Section80C).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.Section80Ccd1B).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.Section80DSelf).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.Section80DParents).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.Section80E).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.Section80G).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.Section80Tta).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.HousingLoanInterest).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.OtherIncome).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.PreviousEmployerSalary).InclusiveBetween(0, 100_000_000).WithMessage(Amount);
        RuleFor(x => x.PreviousEmployerTds).InclusiveBetween(0, 100_000_000).WithMessage(Amount);

        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class FullAndFinalRequestValidator : AbstractValidator<FullAndFinalRequest>
{
    public FullAndFinalRequestValidator()
    {
        RuleFor(x => x.NoticeDaysRequired).InclusiveBetween(0, 365);
        RuleFor(x => x.NoticeDaysServed).InclusiveBetween(0, 365);
        RuleFor(x => x.UnpaidSalary).InclusiveBetween(0, 100_000_000);
        RuleFor(x => x.LeaveDays).InclusiveBetween(0, 1000).When(x => x.LeaveDays is not null);
        RuleFor(x => x.LeaveDivisor).InclusiveBetween(26, 31).WithMessage("Divide a month's wages by 26 or 30 (up to 31).");
        RuleFor(x => x.BonusRatePercent).InclusiveBetween(StatutoryBonus.MinimumRate, StatutoryBonus.MaximumRate)
            .WithMessage("The bonus rate is 8.33% to 20%.");
        RuleFor(x => x.MinimumWage).InclusiveBetween(0, 100_000);
        RuleFor(x => x.Recoveries).Must(r => r is null || r.Count <= 20).WithMessage("At most 20 recoveries.");
        RuleForEach(x => x.Recoveries).ChildRules(r =>
        {
            r.RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Must(c => c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')).WithMessage("Use letters, digits or '_'.");
            r.RuleFor(x => x.Description).NotEmpty().SafeText(200);
            r.RuleFor(x => x.Amount).InclusiveBetween(0, 100_000_000);
        });
    }
}
