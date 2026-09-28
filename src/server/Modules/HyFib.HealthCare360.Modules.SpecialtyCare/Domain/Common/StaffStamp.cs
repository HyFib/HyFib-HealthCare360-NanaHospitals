namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

/// <summary>
/// "Who did it and when" for clinical attestations (checklist items, readings, issues). The display name is copied
/// at the time of the act so medico-legal records stay readable even if the user account is later renamed or removed.
/// </summary>
public sealed record StaffStamp(Guid? UserId, string Name, DateTimeOffset At)
{
    public static StaffStamp System(DateTimeOffset at) => new(null, "System", at);
}
