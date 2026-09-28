namespace HyFib.HealthCare360.Modules.HR.Domain;

/// <summary>
/// One roster cell: an employee on a shift (or on a rostered week off) on a date. Hospitals run 24×7, so rostered
/// duty on a public holiday overrides the holiday for attendance; a double duty is two entries on the same date.
/// </summary>
public sealed class RosterEntry : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>Null for a rostered week off.</summary>
    public Guid? ShiftId { get; set; }

    public bool IsWeekOff { get; set; }
    public string? Notes { get; set; }
}
