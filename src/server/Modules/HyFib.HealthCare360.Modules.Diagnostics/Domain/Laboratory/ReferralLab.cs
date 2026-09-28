namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>
/// External reference laboratory for outsourced tests (LIS-08). Samples are collected in-house, dispatched with a
/// manifest/courier reference, and the external report is received back and entered as results.
/// </summary>
public sealed class ReferralLab : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ContactPerson { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }

    /// <summary>NABL accreditation number (outsourcing to accredited labs is a NABH expectation).</summary>
    public string? AccreditationNumber { get; set; }

    /// <summary>Agreed turnaround in days, used to highlight overdue outsourced tests.</summary>
    public int TatDays { get; set; } = 3;

    public bool IsActive { get; set; } = true;
}
