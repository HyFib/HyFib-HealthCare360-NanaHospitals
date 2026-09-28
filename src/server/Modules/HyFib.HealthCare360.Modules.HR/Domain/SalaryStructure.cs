namespace HyFib.HealthCare360.Modules.HR.Domain;

public enum SalaryComponentKind
{
    Earning = 0,
    Deduction,
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

    /// <summary>PF on the full basic instead of the ₹15,000 wage ceiling (voluntary higher contribution).</summary>
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
