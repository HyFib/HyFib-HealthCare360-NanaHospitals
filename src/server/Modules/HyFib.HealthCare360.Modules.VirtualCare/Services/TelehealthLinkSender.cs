using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.VirtualCare.Services;

/// <summary>
/// Builds and sends the patient's join link (TEL-01): <c>https://{portal.baseUrl}/portal/telehealth/{sessionId}?t={token}</c>
/// (a relative link when the portal address is not configured). Sent by SMS and WhatsApp through the outbox, so the
/// message goes out only if the booking commits, and expires with the link.
/// </summary>
public sealed class TelehealthLinkSender(HospitalDbContext db, INotificationService notifications, SchedulingSettings settings, TimeProvider time)
{
    public const string Template = "telehealth.link";

    private static readonly NotificationChannel[] Channels = [NotificationChannel.Sms, NotificationChannel.WhatsApp];

    public async Task<string> JoinUrlAsync(TelehealthSession session, string token, CancellationToken ct)
    {
        var baseUrl = await settings.PortalBaseUrlAsync(session.FacilityId, ct);
        var path = $"/portal/telehealth/{session.Id}?t={Uri.EscapeDataString(token)}";
        return baseUrl is null ? path : baseUrl + path;
    }

    /// <summary>Queues the link in the current unit of work. Returns false when the patient has no mobile or e-mail.</summary>
    public async Task<bool> SendAsync(TelehealthSession session, string joinUrl, CancellationToken ct)
    {
        var patient = await db.Set<Patient>().AsNoTracking()
            .Where(p => p.Id == session.PatientId)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.Email })
            .FirstOrDefaultAsync(ct);
        if (patient is null || (patient.Phone is null && patient.Email is null))
        {
            return false;
        }

        var doctor = await db.Set<Practitioner>().AsNoTracking().Where(p => p.Id == session.PractitionerId).Select(p => p.DisplayName).FirstOrDefaultAsync(ct);
        var facility = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == session.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        var local = HospitalCalendar.India.ToLocal(session.ScheduledStartAt);
        var now = time.GetUtcNow();
        var ttl = (session.JoinTokenExpiresAt ?? session.ScheduledEndAt) - now;

        notifications.Queue(
            Template,
            new NotificationRecipient(patient.Phone, patient.Email, patient.Id, patient.FullName),
            new Dictionary<string, string>
            {
                ["name"] = patient.FullName,
                ["doctor"] = doctor ?? "your doctor",
                ["time"] = local.ToString("dd MMM yyyy, hh:mm tt", CultureInfo.InvariantCulture),
                ["link"] = joinUrl,
                ["hospital"] = facility ?? "the hospital",
            },
            Channels,
            ttl > TimeSpan.Zero ? ttl : TimeSpan.FromMinutes(30),
            session.FacilityId);

        session.MarkLinkSent(now);
        return true;
    }
}
