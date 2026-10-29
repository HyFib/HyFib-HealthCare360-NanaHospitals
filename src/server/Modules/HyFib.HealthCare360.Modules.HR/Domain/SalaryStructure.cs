namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum SalaryComponentKind
{
    Earning = 0,
    Deduction,
}

/// <summary>
/// How an earning counts in "wages", on which PF is paid: the Code on Social Security 2020, s.2(88), in force since
/// 21 November 2025, which defines them as the Code on Wages 2019 does (s.2(y)).
/// </summary>
public enum WageTreatment
{
    /// <summary>
    /// Wages: basic pay, dearness allowance, retaining allowance, and any other pay the definition does not leave out (a
    /// special allowance, for instance).
    /// </summary>
    Wages = 0,

    /// <summary>
    /// Left out of wages by clauses (a) to (i): bonus, house rent, conveyance, the special expenses of the job, overtime,
    /// commission and the like. What these come to above half of all the month's pay is wages all the same.
    /// </summary>
    Excluded,

    /// <summary>
    /// Paid on leaving, by clauses (j) and (k): gratuity, retrenchment compensation, other retirement benefits and ex gratia
    /// payments. Not wages, and not counted in the half.
    /// </summary>
    PaidOnLeaving,
}

public enum SalaryCalculationType
{
    /// <summary>A fixed monthly amount.</summary>
    Fixed = 0,

    /// <summary>A percentage of the basic salary (for example HRA 40 %).</summary>
    PercentOfBasic,
}

/// <summary>
/// A pay head (Basic, HRA, Conveyance, canteen recovery…). Statutory heads (PF, ESI, PT, TDS) are computed by the
/// payroll engine and cannot be put into salary structures by hand.
/// </summary>
public sealed class SalaryComponent : AuditableEntity
{
    public const string BasicCode = "BASIC";
    public const string PfCode = "PF";
    public const string EsiCode = "ESI";
    public const string ProfessionalTaxCode = "PT";
    public const string TdsCode = "TDS";

    public static readonly IReadOnlySet<string> StatutoryCodes = new HashSet<string>(StringComparer.Ordinal) { PfCode, EsiCode, ProfessionalTaxCode, TdsCode };

    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public SalaryComponentKind Kind { get; set; }
    public SalaryCalculationType CalculationType { get; set; }

    /// <summary>Suggested amount (Fixed) or percentage (PercentOfBasic) when building a structure.</summary>
    public decimal DefaultValue { get; set; }

    /// <summary>Counts towards taxable income (informational for TDS projections).</summary>
    public bool IsTaxable { get; set; } = true;

    /// <summary>How the earning counts in the wages PF is paid on. BASIC is always wages.</summary>
    public WageTreatment WageTreatment { get; set; }

    public bool IsStatutory { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// An employee's contracted pay from <see cref="EffectiveFrom"/>. A revision creates a new structure and closes the
/// previous one, so every past payslip can be reproduced from the structure in force at the time.
/// </summary>
public sealed class EmployeeSalaryStructure : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>Informational cost-to-company for offer letters and budgets.</summary>
    public decimal? AnnualCtc { get; set; }

    /// <summary>Projected monthly TDS from the employee's declared regime and investments (entered by HR/payroll).</summary>
    public decimal MonthlyTds { get; set; }

    public bool PfApplicable { get; set; } = true;

    /// <summary>
    /// PF on the full PF wages instead of the ₹15,000 wage ceiling (voluntary higher contribution). The name is from when PF
    /// was paid on basic pay alone.
    /// </summary>
    public bool PfOnActualBasic { get; set; }

    public bool EsiApplicable { get; set; } = true;

    /// <summary>State whose professional tax applies (TN, KA…); null when the state levies none.</summary>
    public string? ProfessionalTaxState { get; set; }

    public string? Notes { get; set; }
    public List<SalaryStructureLine> Lines { get; set; } = [];

    public bool IsEffectiveOn(DateOnly date) => date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo);
}

/// <summary>One pay head in a structure: a monthly amount, or a percentage of basic for percentage components.</summary>
public sealed class SalaryStructureLine : Entity
{
    public Guid StructureId { get; set; }
    public Guid SalaryComponentId { get; set; }
    public decimal Value { get; set; }
}
