namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

/// <summary>What the reminder job should do for one appointment right now.</summary>
public readonly record struct ReminderDecision(bool SendDayBefore, bool SkipDayBefore, bool SendTwoHour, bool SkipTwoHour)
{
    public bool HasWork => SendDayBefore || SkipDayBefore || SendTwoHour || SkipTwoHour;

    public bool SendsMessage => SendDayBefore || SendTwoHour;
}

/// <summary>
/// When to send the 24-hour and 2-hour appointment reminders (OA-04). Each reminder is sent at most once: a
/// reminder whose moment had already passed when the appointment was booked is not needed (the confirmation
/// covers it) and is flagged as skipped; if the job was down and both are due, only the 2-hour reminder goes out,
/// so a patient never receives two reminders at the same time.
/// </summary>
public static class ReminderPolicy
{
    public static readonly TimeSpan DayBeforeLead = TimeSpan.FromHours(24);
    public static readonly TimeSpan TwoHourLead = TimeSpan.FromHours(2);

    public static ReminderDecision Decide(Appointment appointment, DateTimeOffset now) =>
        Decide(appointment.Status, appointment.StartsAt, appointment.BookedAt, appointment.Reminder24hSentAt, appointment.Reminder2hSentAt, now);

    public static ReminderDecision Decide(
        AppointmentStatus status,
        DateTimeOffset startsAt,
        DateTimeOffset bookedAt,
        DateTimeOffset? dayBeforeSentAt,
        DateTimeOffset? twoHourSentAt,
        DateTimeOffset now)
    {
        if (status is not (AppointmentStatus.Booked or AppointmentStatus.Confirmed) || now >= startsAt)
        {
            return default;
        }

        bool sendDayBefore = false, skipDayBefore = false, sendTwoHour = false, skipTwoHour = false;
        var twoHourDue = now >= startsAt - TwoHourLead;

        if (dayBeforeSentAt is null && now >= startsAt - DayBeforeLead)
        {
            var bookedLate = bookedAt > startsAt - DayBeforeLead;
            skipDayBefore = bookedLate || twoHourDue;
            sendDayBefore = !skipDayBefore;
        }

        if (twoHourSentAt is null && twoHourDue)
        {
            skipTwoHour = bookedAt > startsAt - TwoHourLead;
            sendTwoHour = !skipTwoHour;
        }

        return new ReminderDecision(sendDayBefore, skipDayBefore, sendTwoHour, skipTwoHour);
    }
}
