namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>Staff cadre used for rostering pools, statutory returns and head-count reports.</summary>
public enum StaffCategory
{
    Medical = 0,
    Nursing,
    Paramedical,
    Administrative,
    Support,
}

/// <summary>
/// Job title such as "Staff Nurse" or "Consultant". Shared by every facility of the group so that head-count and
/// payroll reports compare like with like across branches.
/// </summary>
public sealed class Designation : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public StaffCategory Category { get; set; }

    /// <summary>Seniority grade (1 = most senior); used to sort organisation charts.</summary>
    public int Grade { get; set; }

    public bool IsActive { get; set; } = true;
}
