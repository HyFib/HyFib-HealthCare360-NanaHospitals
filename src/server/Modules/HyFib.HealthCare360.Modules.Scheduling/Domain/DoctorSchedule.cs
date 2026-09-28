namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

/// <summary>How a session (and each of its slots) can be consulted.</summary>
public enum ConsultationMode
{
    InPerson = 0,
    Telehealth,
    Both,
}

/// <summary>
/// One recurring weekly OPD session of a practitioner at a facility and department (PMS-01), for example
/// "Dr. Arjun Mehta, General Medicine, Mondays 09:00–13:00, 15-minute slots, one patient per slot". A doctor's
/// week is several rows (morning and evening sessions are separate rows). Dated leave, blocks and extra sessions
/// are <see cref="ScheduleException"/> rows, so the recurring pattern never has to be edited for one-off changes.
/// </summary>
public sealed class DoctorSchedule : AuditableEntity, IFacilityScoped
{
    public const int MinSlotMinutes = 5;
    public const int MaxSlotMinutes = 120;
    public const int MaxCapacityPerSlot = 20;

    public Guid FacilityId { get; set; }
    public Guid PractitionerId { get; set; }
    public Guid DepartmentId { get; set; }
    public Weekday Weekday { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int SlotMinutes { get; set; } = 15;

    /// <summary>Patients booked into the same slot (group OPD, vaccination clinics); usually 1.</summary>
    public int CapacityPerSlot { get; set; } = 1;

    public ConsultationMode Mode { get; set; }

    /// <summary>Consultation room printed on the appointment slip and announced by the queue display.</summary>
    public string? Room { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }

    /// <summary>Whole slots that fit in one session (a trailing remainder shorter than a slot is not bookable).</summary>
    public int SlotsPerSession => SlotMinutes <= 0 ? 0 : (int)((EndTime - StartTime).TotalMinutes / SlotMinutes);

    public bool AppliesOn(DateOnly date) =>
        IsActive && date >= EffectiveFrom && (EffectiveTo is null || date <= EffectiveTo) && Weekday == HospitalCalendar.WeekdayOf(date);

    /// <summary>
    /// True when both rows could produce slots for the same doctor at the same time on some date — the doctor
    /// cannot be in two consultation rooms (or two branches) at once, so such rows are rejected.
    /// </summary>
    public bool Overlaps(DoctorSchedule other) =>
        other.Id != Id && IsActive && other.IsActive && other.PractitionerId == PractitionerId && other.Weekday == Weekday
        && StartTime < other.EndTime && other.StartTime < EndTime
        && EffectiveFrom <= (other.EffectiveTo ?? DateOnly.MaxValue) && other.EffectiveFrom <= (EffectiveTo ?? DateOnly.MaxValue);

    /// <summary>Shape rules shared by the API validators and the seeders (a session must hold at least one whole slot).</summary>
    public static Result Validate(TimeOnly start, TimeOnly end, int slotMinutes, int capacity, DateOnly effectiveFrom, DateOnly? effectiveTo)
    {
        if (slotMinutes is < MinSlotMinutes or > MaxSlotMinutes)
        {
            return Error.Field("slotMinutes", $"Slot length must be between {MinSlotMinutes} and {MaxSlotMinutes} minutes.");
        }

        if (end <= start)
        {
            return Error.Field("endTime", "The session must end after it starts.");
        }

        if ((end - start).TotalMinutes < slotMinutes)
        {
            return Error.Field("endTime", "The session is shorter than one slot.");
        }

        if (capacity is < 1 or > MaxCapacityPerSlot)
        {
            return Error.Field("capacityPerSlot", $"Patients per slot must be between 1 and {MaxCapacityPerSlot}.");
        }

        if (effectiveTo is { } to && to < effectiveFrom)
        {
            return Error.Field("effectiveTo", "The end date must be on or after the start date.");
        }

        return Result.Success();
    }
}
