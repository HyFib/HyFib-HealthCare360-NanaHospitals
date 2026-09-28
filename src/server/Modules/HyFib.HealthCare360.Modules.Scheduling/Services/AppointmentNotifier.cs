using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Scheduling.Services;

/// <summary>
/// Patient messages about appointments (OA-04): confirmation, cancellation, 24 h / 2 h reminders and waitlist offers,
/// by SMS and WhatsApp. Messages are queued in the caller's unit of work (outbox), so they are sent only if the
/// booking commits, and they expire at the appointment time — a reminder delivered after an internet outage but
/// after the appointment would only confuse the patient.
/// </summary>
public sealed class AppointmentNotifier(HospitalDbContext db, INotificationService notifications, ISettingsService settings, TimeProvider time)
{
    public const string ConfirmedTemplate = "appointment.confirmed";
    public const string ReminderTemplate = "appointment.reminder";
    public const string CancelledTemplate = "appointment.cancelled";
    public const string WaitlistTemplate = "appointment.waitlist";

    private static readonly NotificationChannel[] PatientChannels = [NotificationChannel.Sms, NotificationChannel.WhatsApp];

    public Task ConfirmedAsync(Appointment appointment, CancellationToken ct) => QueueAsync(ConfirmedTemplate, appointment, ct);

    public Task ReminderAsync(Appointment appointment, CancellationToken ct) => QueueAsync(ReminderTemplate, appointment, ct);

    public Task CancelledAsync(Appointment appointment, CancellationToken ct) => QueueAsync(CancelledTemplate, appointment, ct);

    /// <summary>Tells a waitlisted patient that a slot has opened, with a link to book it on the portal.</summary>
    public async Task WaitlistOfferAsync(WaitlistEntry entry, DateTimeOffset slotStartsAt, CancellationToken ct)
    {
        var contact = await ContactAsync(entry.PatientId, entry.PractitionerId, entry.FacilityId, ct);
        if (contact is null)
        {
            return;
        }

        var baseUrl = await PortalAddress.BaseUrlAsync(settings, entry.FacilityId, ct);
        var values = Values(contact, slotStartsAt, null);
        values["link"] = baseUrl is null ? "the patient portal" : PortalAddress.Page(baseUrl, $"doctors/{entry.PractitionerId}");
        Queue(WaitlistTemplate, contact, values, slotStartsAt, entry.FacilityId);
    }

    private async Task QueueAsync(string template, Appointment appointment, CancellationToken ct)
    {
        var contact = await ContactAsync(appointment.PatientId, appointment.PractitionerId, appointment.FacilityId, ct);
        if (contact is not null)
        {
            Queue(template, contact, Values(contact, appointment.StartsAt, appointment), appointment.StartsAt, appointment.FacilityId);
        }
    }

    private void Queue(string template, Contact contact, Dictionary<string, string> values, DateTimeOffset relevantUntil, Guid facilityId)
    {
        var ttl = relevantUntil - time.GetUtcNow();
        notifications.Queue(
            template,
            new NotificationRecipient(contact.Phone, contact.Email, contact.PatientId, contact.PatientName, contact.Language),
            values,
            PatientChannels,
            ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(30),
            facilityId);
    }

    private static Dictionary<string, string> Values(Contact contact, DateTimeOffset startsAt, Appointment? appointment)
    {
        var local = HospitalCalendar.India.ToLocal(startsAt);
        var values = new Dictionary<string, string>
        {
            ["name"] = contact.PatientName,
            ["doctor"] = contact.DoctorName,
            ["date"] = local.ToString("ddd, dd MMM yyyy", CultureInfo.InvariantCulture),
            ["time"] = local.ToString("hh:mm tt", CultureInfo.InvariantCulture),
            ["hospital"] = contact.FacilityName,
        };

        if (appointment is not null)
        {
            values["reference"] = appointment.Number;
            values["mode"] = appointment.IsTelehealth ? "video consultation" : "clinic visit";
        }

        return values;
    }

    private async Task<Contact?> ContactAsync(Guid patientId, Guid practitionerId, Guid facilityId, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == patientId)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.Email, p.PreferredLanguage })
            .FirstOrDefaultAsync(ct);
        if (patient is null || (patient.Phone is null && patient.Email is null))
        {
            return null;
        }

        var doctor = await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == practitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
        var facility = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        return new Contact(patient.Id, patient.FullName, patient.Phone, patient.Email, LanguageOf(patient.PreferredLanguage), doctor ?? "your doctor", facility ?? "the hospital");
    }

    /// <summary>Hindi-speaking patients get the Hindi template when one is configured (the renderer falls back to English).</summary>
    private static string LanguageOf(string? preferred) =>
        preferred is not null && (preferred.StartsWith("hi", StringComparison.OrdinalIgnoreCase) || preferred.Contains("हिन्दी", StringComparison.Ordinal))
            ? "hi"
            : "en";

    private sealed record Contact(Guid PatientId, string PatientName, string? Phone, string? Email, string Language, string DoctorName, string FacilityName);
}
