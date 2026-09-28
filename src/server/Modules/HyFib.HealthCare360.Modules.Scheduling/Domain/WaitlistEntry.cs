namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

public enum WaitlistStatus
{
    Waiting = 0,

    /// <summary>A freed slot was offered by SMS/WhatsApp; the entry stays open until booked or closed.</summary>
    Offered,

    Booked,
    Cancelled,
    Expired,
}

/// <summary>Part of the day the patient can come (IST wall clock).</summary>
public enum PreferredSession
{
    Any = 0,

    /// <summary>Before 12:00.</summary>
    Morning,

    /// <summary>12:00 to 16:00.</summary>
    Afternoon,

    /// <summary>From 16:00.</summary>
    Evening,
}

/// <summary>
/// A patient waiting for a slot with a doctor (OA-05) when the wanted days are full. When a matching appointment is
/// cancelled, the earliest waiting patient is offered the freed slot by SMS/WhatsApp; staff or the patient then books
/// it normally (nothing is booked automatically, so patients are never charged or committed without consent).
/// </summary>
public sealed class WaitlistEntry : AuditableEntity, IFacilityScoped
{
    public const int MaxPreferredDays = 60;

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid PractitionerId { get; set; }
    public Guid? DepartmentId { get; set; }
    public DateOnly PreferredFrom { get; set; }
    public DateOnly PreferredTo { get; set; }
    public PreferredSession PreferredSession { get; set; }
    public bool IsTelehealth { get; set; }
    public AppointmentChannel Channel { get; set; }
    public string? Notes { get; set; }
    public WaitlistStatus Status { get; set; }
    public DateTimeOffset? OfferedAt { get; set; }
    public DateTimeOffset? OfferedSlotStartsAt { get; set; }
    public int OfferCount { get; set; }
    public Guid? AppointmentId { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public bool IsOpen => Status is WaitlistStatus.Waiting or WaitlistStatus.Offered;

    /// <summary>Would this patient take a slot on <paramref name="date"/> at local <paramref name="time"/>?</summary>
    public bool Wants(DateOnly date, TimeOnly time, bool telehealth) =>
        IsOpen && date >= PreferredFrom && date <= PreferredTo && telehealth == IsTelehealth && InSession(PreferredSession, time);

    public static bool InSession(PreferredSession session, TimeOnly time) => session switch
    {
        PreferredSession.Morning => time < new TimeOnly(12, 0),
        PreferredSession.Afternoon => time >= new TimeOnly(12, 0) && time < new TimeOnly(16, 0),
        PreferredSession.Evening => time >= new TimeOnly(16, 0),
        _ => true,
    };

    public static Result ValidateWindow(DateOnly from, DateOnly to, DateOnly today)
    {
        if (from < today)
        {
            return Error.Field("preferredFrom", "Choose today or a later date.");
        }

        if (to < from)
        {
            return Error.Field("preferredTo", "The last date must be on or after the first date.");
        }

        return to.DayNumber - from.DayNumber >= MaxPreferredDays
            ? Error.Field("preferredTo", $"A waitlist request can cover at most {MaxPreferredDays} days.")
            : Result.Success();
    }

    public Result Offer(DateTimeOffset slotStartsAt, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("waitlist.closed", "This waitlist request is closed.");
        }

        Status = WaitlistStatus.Offered;
        OfferedAt = now;
        OfferedSlotStartsAt = slotStartsAt;
        OfferCount++;
        return Result.Success();
    }

    public Result MarkBooked(Guid appointmentId, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("waitlist.closed", "This waitlist request is closed.");
        }

        Status = WaitlistStatus.Booked;
        AppointmentId = appointmentId;
        ClosedAt = now;
        return Result.Success();
    }

    public Result Cancel(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("waitlist.closed", "This waitlist request is already closed.");
        }

        Status = WaitlistStatus.Cancelled;
        ClosedAt = now;
        return Result.Success();
    }

    /// <summary>Closes the request once every preferred date has passed.</summary>
    public bool ExpireIfPast(DateOnly today, DateTimeOffset now)
    {
        if (!IsOpen || PreferredTo >= today)
        {
            return false;
        }

        Status = WaitlistStatus.Expired;
        ClosedAt = now;
        return true;
    }
}
