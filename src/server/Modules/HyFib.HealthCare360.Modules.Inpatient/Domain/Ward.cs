namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>Ward class. It decides the bed tariff, the consultant-visit tariff and where the ward appears in census reports.</summary>
public enum WardType
{
    General = 0,
    SemiPrivate,
    Private,
    Deluxe,
    Icu,
    Nicu,
    Picu,
    Hdu,
    Maternity,
    Isolation,
    DayCare,
    Emergency,
}

/// <summary>Which patients a ward may accept (general wards in Indian hospitals are usually segregated by sex).</summary>
public enum WardGenderPolicy
{
    Any = 0,
    MaleOnly,
    FemaleOnly,
}

/// <summary>
/// A nursing unit whose beds share one class. <see cref="BedServiceItemId"/> is the per-day bed charge (BED-GEN,
/// BED-ICU…) snapshotted onto each bed assignment, so re-classing a ward re-prices new stays only and never rewrites
/// days already billed.
/// </summary>
public sealed class Ward : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public WardType Type { get; set; }
    public string? Floor { get; set; }
    public string? Block { get; set; }
    public WardGenderPolicy GenderPolicy { get; set; }
    public string? NurseStation { get; set; }
    public string? Phone { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? BedServiceItemId { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    /// <summary>Critical-care wards bill the ICU consultant-visit tariff (VISIT-DR-ICU).</summary>
    public bool IsCriticalCare => IsCriticalCareType(Type);

    public static bool IsCriticalCareType(WardType type) => type is WardType.Icu or WardType.Nicu or WardType.Picu or WardType.Hdu;
}

/// <summary>A room inside a ward (private or semi-private rooms, isolation rooms). Open wards may have no rooms.</summary>
public sealed class Room : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid WardId { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsIsolation { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Admission and transfer eligibility for a ward. Only the opposite sex is refused on single-sex wards, so patients whose
/// sex is recorded as other or unknown (for example unidentified trauma cases) are left to nursing judgement. Age limits
/// are applied only when a date of birth is known.
/// </summary>
public static class WardEligibility
{
    public const int NeonatalMaxAgeDays = 28;
    public const int PaediatricMaxAgeYears = 18;

    public static Result Check(WardType type, WardGenderPolicy policy, Gender gender, DateOnly? dateOfBirth, DateOnly today)
    {
        if (policy == WardGenderPolicy.MaleOnly && gender == Gender.Female)
        {
            return Error.Rule("ward.gender", "This ward admits male patients only.");
        }

        if (policy == WardGenderPolicy.FemaleOnly && gender == Gender.Male)
        {
            return Error.Rule("ward.gender", "This ward admits female patients only.");
        }

        if (type == WardType.Maternity && gender == Gender.Male)
        {
            return Error.Rule("ward.maternity", "The maternity ward admits female patients only.");
        }

        if (dateOfBirth is { } dob)
        {
            if (type == WardType.Nicu && Age.InDays(dob, today) > NeonatalMaxAgeDays)
            {
                return Error.Rule("ward.neonatal_only", $"The NICU admits neonates ({NeonatalMaxAgeDays} days old or younger). Choose a PICU or ICU bed.");
            }

            if (type == WardType.Picu && Age.InYears(dob, today) >= PaediatricMaxAgeYears)
            {
                return Error.Rule("ward.paediatric_only", $"The PICU admits children under {PaediatricMaxAgeYears}. Choose an adult ICU bed.");
            }
        }

        return Result.Success();
    }
}
