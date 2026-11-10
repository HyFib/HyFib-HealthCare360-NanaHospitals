namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

/// <summary>
/// An employee's income-tax choice and declarations for one financial year (Form 12BB): the regime, rent, investments
/// and other income, and salary and tax from a previous employer in the same year (Form 12B). Payroll computes TDS from it
/// each month; without one, the new regime with no declarations applies (s.115BAC(1A)).
/// </summary>
public sealed class TaxDeclaration : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EmployeeId { get; set; }

    /// <summary>The year the financial year starts: 2026 for April 2026 to March 2027.</summary>
    public int FinancialYear { get; set; }

    public TaxRegime Regime { get; set; }
    public decimal RentPaid { get; set; }
    public bool MetroCity { get; set; }
    public decimal Section80C { get; set; }
    public decimal Section80Ccd1B { get; set; }
    public decimal Section80DSelf { get; set; }
    public decimal Section80DParents { get; set; }
    public bool ParentsSenior { get; set; }
    public decimal Section80E { get; set; }
    public decimal Section80G { get; set; }
    public decimal Section80Tta { get; set; }
    public decimal HousingLoanInterest { get; set; }
    public decimal OtherIncome { get; set; }
    public decimal PreviousEmployerSalary { get; set; }
    public decimal PreviousEmployerTds { get; set; }

    public string? Notes { get; set; }

    public TaxDeclarationAmounts ToAmounts() => new(
        RentPaid, MetroCity, Section80C, Section80Ccd1B, Section80DSelf, Section80DParents, ParentsSenior, Section80E, Section80G, Section80Tta,
        HousingLoanInterest, OtherIncome, PreviousEmployerSalary, PreviousEmployerTds);
}

/// <summary>
/// What the statutory returns need to know about the facility as an employer and deductor: TAN and PAN, the deductor type,
/// the person responsible for deducting tax, the EPFO establishment and ESIC employer codes, and the salary account the
/// bank files debit.
/// </summary>
public sealed class StatutoryEmployerProfile : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string? Tan { get; set; }
    public string? Pan { get; set; }
    public string? DeductorName { get; set; }
    public string? Branch { get; set; }

    /// <summary>The deductor type code in the return file: K company, E individual or HUF, others as listed by Protean.</summary>
    public string DeductorType { get; set; } = "K";

    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? AddressLine3 { get; set; }
    public string? City { get; set; }

    /// <summary>The state's two-digit code in the TDS return (Tamil Nadu 22, Karnataka 15…).</summary>
    public string? StateCode { get; set; }

    public string? Pin { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? ResponsibleName { get; set; }
    public string? ResponsibleDesignation { get; set; }
    public string? ResponsiblePan { get; set; }
    public string? ResponsibleMobile { get; set; }
    public string? ResponsibleEmail { get; set; }
    public string? EpfEstablishmentCode { get; set; }
    public string? EsicEmployerCode { get; set; }

    /// <summary>The hospital's salary account the bank upload files debit.</summary>
    public string? SalaryDebitAccount { get; set; }

    /// <summary>The bank of <see cref="SalaryDebitAccount"/>: decides which payees are paid by internal transfer.</summary>
    public BankFileFormat SalaryBank { get; set; }
}

public enum TdsForm
{
    /// <summary>Salary (s.192).</summary>
    Form24Q = 0,

    /// <summary>Payments other than salary to residents (s.194J doctors' fees).</summary>
    Form26Q,
}

/// <summary>
/// A TDS challan (ITNS 281) deposited for a month: the bank's BSR code, the date and the challan serial number, and the
/// amounts. The quarterly return lists each challan with the deductions it paid, which are the month's deductions under
/// the form.
/// </summary>
public sealed class TdsChallan : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public TdsForm Form { get; set; }

    /// <summary>The month the deductions were made in (the deposit is by the 7th of the next month; April for March).</summary>
    public int Year { get; set; }

    public int Month { get; set; }
    public string BsrCode { get; set; } = string.Empty;
    public DateOnly DepositedOn { get; set; }
    public string ChallanSerial { get; set; } = string.Empty;
    public decimal Tax { get; set; }
    public decimal Surcharge { get; set; }
    public decimal Cess { get; set; }
    public decimal Interest { get; set; }
    public decimal Others { get; set; }
    public decimal LateFee { get; set; }

    /// <summary>200: tax deducted by the deductor; 400: on a demand raised by the department.</summary>
    public string MinorHead { get; set; } = "200";
}
