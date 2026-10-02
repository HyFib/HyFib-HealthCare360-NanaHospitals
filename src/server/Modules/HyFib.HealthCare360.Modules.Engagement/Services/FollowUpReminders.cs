using System.Globalization;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// The "follow-up due" SMS (CRM-02), sent by staff from the worklist or by the reminder job. It goes only to a living
/// patient with a mobile number who accepted SMS updates. One message covers the tasks given (a patient's tasks falling
/// due together, such as several vaccines), names the earliest due date, and marks every task reminded. The caller saves.
/// </summary>
public sealed class FollowUpReminders(
    HospitalDbContext db,
    CommunicationConsentService consents,
    PublicLinks links,
    INotificationService notifications,
    TimeProvider time)
{
    public const string Template = "followup.reminder";

    public async Task<Result> QueueAsync(IReadOnlyList<FollowUpTask> tasks, CancellationToken ct)
    {
        var first = tasks.MinBy(t => t.DueDate) ?? throw new ArgumentException("There is no follow-up to remind the patient of.", nameof(tasks));
        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == first.PatientId)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.PreferredLanguage, p.IsDeceased })
            .FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), first.PatientId);
        }

        if (patient.IsDeceased)
        {
            return Error.Rule("followup.patient_deceased", "The patient is recorded as deceased.");
        }

        if (patient.Phone is null)
        {
            return Error.Rule("followup.no_phone", "The patient has no mobile number on record.");
        }

        if (!await consents.HasTransactionalConsentAsync(patient.Id, ct))
        {
            return Error.Rule("followup.no_consent", "The patient has not agreed to SMS updates.");
        }

        var doctor = first.VisitId is { } visitId
            ? await (from v in db.Set<Visit>() join pr in db.Set<Practitioner>() on v.PractitionerId equals pr.Id where v.Id == visitId select pr.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        var baseUrl = await links.BaseUrlAsync(first.FacilityId, ct);
        notifications.Queue(Template,
            new NotificationRecipient(patient.Phone, null, patient.Id, patient.FullName, CampaignService.LanguageCode(patient.PreferredLanguage)),
            new Dictionary<string, string>
            {
                ["doctor"] = doctor ?? "your doctor",
                ["date"] = first.DueDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
                ["link"] = baseUrl is null ? "the hospital front desk" : PublicLinks.PortalHome(baseUrl),
            },
            [NotificationChannel.Sms],
            TimeSpan.FromDays(1),
            first.FacilityId);

        var now = time.GetUtcNow();
        foreach (var task in tasks)
        {
            task.ReminderSentAt = now;
        }

        return Result.Success();
    }
}
