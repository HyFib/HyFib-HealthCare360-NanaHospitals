namespace HyFib.HealthCare360.Modules.Engagement.Domain;

/// <summary>
/// The follow-ups Engagement creates by itself, how they close and when the patient is reminded (CRM-02):
/// <list type="bullet">
/// <item>a signed consultation with a follow-up date: "after consultation", due on that date;</item>
/// <item>a vaccine dose with a next-due date: "vaccination", due then, until the next dose of the same vaccine;</item>
/// <item>a visit by a patient with a chronic problem: the next "chronic review", replacing the previous one;</item>
/// <item>a completed health check-up: the next "check-up due", a year later by default.</item>
/// </list>
/// Each automatic task is keyed by what started it, so a replayed event never adds a second one.
/// </summary>
public static class FollowUpAutomation
{
    /// <summary>Days before the due date when the patient gets the "follow-up due" SMS (setting <c>crm.followUpReminderDays</c>).</summary>
    public const int DefaultReminderDays = 2;

    /// <summary>Months from a chronic patient's visit to the next review (setting <c>crm.chronicReviewMonths</c>).</summary>
    public const int DefaultChronicReviewMonths = 3;

    /// <summary>Months from a health check-up to the next one (setting <c>crm.healthCheckupRecallMonths</c>).</summary>
    public const int DefaultHealthCheckupRecallMonths = 12;

    /// <summary>
    /// A visit or an appointment this many days before the due date, or any time after it, fulfils a "come back" task.
    /// An earlier one, likely for something else, leaves it open: a booking next week does not settle a vaccine due in May.
    /// </summary>
    public const int FulfilmentWindowDays = 30;

    private const string ImmunizationPrefix = "immunization:";

    public static string ConsultationReference(Guid encounterId) => $"encounter:{encounterId:N}";

    public static string ImmunizationReference(Guid immunizationId) => $"{ImmunizationPrefix}{immunizationId:N}";

    public static string ChronicReviewReference(Guid encounterId) => $"chronic:{encounterId:N}";

    public static string HealthCheckupReference(Guid bookingId) => $"checkup:{bookingId:N}";

    /// <summary>The dose an automatic vaccination task was started by, or null for any other task.</summary>
    public static Guid? ImmunizationOf(string? sourceReference) =>
        sourceReference is not null && sourceReference.StartsWith(ImmunizationPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(sourceReference[ImmunizationPrefix.Length..], "N", out var id)
            ? id
            : null;

    /// <summary>Is a visit or an appointment on <paramref name="date"/> close enough to <paramref name="dueDate"/> to fulfil it?</summary>
    public static bool IsAround(DateOnly dueDate, DateOnly date) => date >= dueDate.AddDays(-FulfilmentWindowDays);

    /// <summary>A visit fulfils "after consultation" and "chronic review" tasks; a vaccination needs its dose and a check-up the check-up.</summary>
    public static bool ClosesOnVisit(FollowUpType type) => type is FollowUpType.PostConsultation or FollowUpType.ChronicReview;

    public static DateOnly ChronicReviewDate(DateOnly seenOn, int months) => seenOn.AddMonths(months);

    /// <summary>A chronic review is scheduled unless the doctor already asked to see the patient on or after its date.</summary>
    public static bool NeedsChronicReview(DateOnly? followUpDate, DateOnly reviewDate) => followUpDate is not { } asked || asked < reviewDate;

    public static DateOnly HealthCheckupRecallDate(DateOnly completedOn, int months) => completedOn.AddMonths(months);

    /// <summary>Two doses are of the same vaccine when their card codes match or, without codes, their names.</summary>
    public static bool IsSameVaccine(string name, string? code, string otherName, string? otherCode) =>
        !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(otherCode)
            ? string.Equals(code.Trim(), otherCode.Trim(), StringComparison.OrdinalIgnoreCase)
            : string.Equals(name.Trim(), otherName.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The tasks that ask the patient to come back, which the "follow-up due" SMS is about.</summary>
    public static bool RemindsPatient(FollowUpType type) => FollowUpTask.ClosesOnBooking(type);

    /// <summary>
    /// Does an open task need its reminder today? Once, from <paramref name="daysBefore"/> days before the due date until
    /// the due date itself; a task already reminded, by staff or the job, is not reminded again.
    /// </summary>
    public static bool IsReminderDue(FollowUpTask task, DateOnly today, int daysBefore) =>
        task.IsOpen && RemindsPatient(task.Type) && task.ReminderSentAt is null
        && task.DueDate >= today && task.DueDate.DayNumber - today.DayNumber <= daysBefore;
}
