namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

/// <summary>
/// A reminder the reminder job sent (or deliberately skipped) for an appointment, once per kind (OA-04). It is kept here
/// and not on the appointment, so a reminder pass never changes the appointment row: the row's version is what the desk
/// holds while it checks the patient in or changes the booking, and a reminder is not a change to the booking.
/// </summary>
public sealed class AppointmentReminderSent
{
    public const string Table = "appointment_reminders";

    public Guid AppointmentId { get; set; }

    public AppointmentReminder Kind { get; set; }

    public DateTimeOffset SentAt { get; set; }
}
