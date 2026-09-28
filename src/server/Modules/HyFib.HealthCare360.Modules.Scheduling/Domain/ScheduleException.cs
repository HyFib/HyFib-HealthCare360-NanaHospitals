namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

public enum ScheduleExceptionType
{
    /// <summary>The doctor is away (conference, sick leave, vacation): no slots.</summary>
    Leave = 0,

    /// <summary>Time taken out of OPD (surgery list, meeting, ward round): no slots in that window.</summary>
    Block,

    /// <summary>An additional OPD session on specific dates (camp, Sunday clinic).</summary>
    ExtraSession,
}

/// <summary>
/// A dated change to a practitioner's recurring schedule (PMS-01). Leave and blocks remove slots; an extra session
/// adds slots on those dates. An exception without times covers whole days; otherwise its time window applies on
/// every date of the range. Leave normally applies at every facility (<see cref="FacilityId"/> = null) because a
/// doctor on leave is away from all branches.
/// </summary>
public sealed class ScheduleException : AuditableEntity
{
    public const int MaxRangeDays = 366;

    public Guid PractitionerId { get; set; }

    /// <summary>Null = applies at every facility; required for an extra session.</summary>
    public Guid? FacilityId { get; set; }

    public ScheduleExceptionType Type { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public string Reason { get; set; } = string.Empty;

    // Extra-session details (ignored for leave and blocks).
    public Guid? DepartmentId { get; set; }
    public int? SlotMinutes { get; set; }
    public int? CapacityPerSlot { get; set; }
    public ConsultationMode? Mode { get; set; }
    public string? Room { get; set; }

    public bool IsAllDay => StartTime is null || EndTime is null;

    public bool RemovesSlots => Type is ScheduleExceptionType.Leave or ScheduleExceptionType.Block;

    public bool Covers(DateOnly date) => date >= FromDate && date <= ToDate;

    public bool AppliesTo(Guid facilityId) => FacilityId is null || FacilityId == facilityId;

    /// <summary>Does this leave/block remove the local slot [<paramref name="start"/>, <paramref name="end"/>) on <paramref name="date"/>?</summary>
    public bool Blocks(DateOnly date, TimeOnly start, TimeOnly end, Guid facilityId) =>
        RemovesSlots && Covers(date) && AppliesTo(facilityId) && (IsAllDay || (StartTime < end && start < EndTime));

    /// <summary>True when an extra session carries everything needed to generate slots.</summary>
    public bool IsCompleteExtraSession =>
        Type == ScheduleExceptionType.ExtraSession && StartTime is not null && EndTime is not null && FacilityId is not null
        && DepartmentId is not null && SlotMinutes is > 0 && CapacityPerSlot is > 0;

    /// <summary>Shape rules for an exception; the facility of an extra session is the caller's current facility.</summary>
    public static Result Validate(
        ScheduleExceptionType type,
        DateOnly from,
        DateOnly to,
        TimeOnly? start,
        TimeOnly? end,
        Guid? departmentId,
        int? slotMinutes,
        int? capacity)
    {
        if (to < from)
        {
            return Error.Field("toDate", "The end date must be on or after the start date.");
        }

        if (to.DayNumber - from.DayNumber >= MaxRangeDays)
        {
            return Error.Field("toDate", "An exception can cover at most one year.");
        }

        if (start is null != end is null)
        {
            return Error.Field("endTime", "Enter both a start and an end time, or neither for the whole day.");
        }

        if (start is { } s && end is { } e && e <= s)
        {
            return Error.Field("endTime", "The end time must be after the start time.");
        }

        if (type != ScheduleExceptionType.ExtraSession)
        {
            return Result.Success();
        }

        if (start is null || end is null)
        {
            return Error.Field("startTime", "An extra session needs a start and an end time.");
        }

        if (departmentId is null)
        {
            return Error.Field("departmentId", "Choose the department of the extra session.");
        }

        return DoctorSchedule.Validate(start.Value, end.Value, slotMinutes ?? 0, capacity ?? 0, from, to);
    }
}
