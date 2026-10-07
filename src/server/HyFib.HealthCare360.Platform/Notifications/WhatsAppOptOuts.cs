using System.Linq.Expressions;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Platform.Notifications;

/// <summary>
/// A mobile number's latest STOP or START reply to the hospital's WhatsApp messages: one row per number (E.164), for
/// the whole hospital group whichever facility's number received it. While <see cref="OptedOut"/> is set, the WhatsApp
/// delivery handler sends the number only essential messages (<see cref="WhatsAppOptOutPolicy"/>). SMS and e-mail are
/// not affected.
/// </summary>
public sealed class WhatsAppOptOut : AuditableEntity
{
    public string Phone { get; set; } = string.Empty;

    public bool OptedOut { get; set; }

    /// <summary>When the person sent the reply that set <see cref="OptedOut"/> (WhatsApp's timestamp).</summary>
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>The WhatsApp message id (wamid) of that reply.</summary>
    public string? ProviderMessageId { get; set; }

    /// <summary>The facility whose WhatsApp number received that reply, when known.</summary>
    public Guid? FacilityId { get; set; }

    /// <summary>
    /// Applies a STOP (<paramref name="optedOut"/>) or START reply. WhatsApp can deliver replies late and out of order,
    /// so a reply sent before the one already applied changes nothing (returns false).
    /// </summary>
    public bool Apply(bool optedOut, DateTimeOffset repliedAt, string? providerMessageId, Guid? facilityId)
    {
        if (repliedAt < ChangedAt)
        {
            return false;
        }

        OptedOut = optedOut;
        ChangedAt = repliedAt;
        ProviderMessageId = providerMessageId;
        FacilityId = facilityId;
        return true;
    }
}

internal sealed class WhatsAppOptOutConfiguration : IEntityTypeConfiguration<WhatsAppOptOut>
{
    public void Configure(EntityTypeBuilder<WhatsAppOptOut> b)
    {
        b.ToTable("whatsapp_opt_outs", "platform");
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.ProviderMessageId).HasMaxLength(128);

        // One row per number; every WhatsApp delivery looks its number up.
        b.HasIndex(x => x.Phone).IsUnique();
    }
}

/// <summary>
/// Which messages still reach a number that replied STOP on WhatsApp. Only messages that must reach the person for
/// security are essential. Everything else stops on WhatsApp, including messages the patient asked for just now (such
/// as a video consultation's join link), because the patient still has SMS, e-mail and the portal. Template keys not
/// listed here, such as templates added by a module or an administrator, are not essential.
/// </summary>
public static class WhatsAppOptOutPolicy
{
    /// <summary>Recorded on the notification log of a message that was not sent because of the opt-out.</summary>
    public const string SuppressedReason =
        "Not sent: this number replied STOP on WhatsApp. It gets only sign-in codes on WhatsApp until it replies START.";

    /// <summary>Sent on WhatsApp even after STOP.</summary>
    public static IReadOnlySet<string> EssentialTemplates { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "auth.otp", // patient portal sign-in code
    };

    /// <summary>
    /// The built-in templates that STOP stops on WhatsApp. Any key not in <see cref="EssentialTemplates"/> stops anyway;
    /// the list exists so that adding a built-in template forces a decision (a unit test checks that every key in
    /// <see cref="DefaultNotificationTemplates"/> is in exactly one of the two lists).
    /// </summary>
    public static IReadOnlySet<string> StoppedTemplates { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // Service messages: most also go by SMS, and the portal has the same information.
        "appointment.confirmed", "appointment.reminder", "appointment.cancelled", "visit.token", "admission.admitted",
        "discharge.completed", "bill.receipt", "payment.link", "lab.report_ready", "prescription.shared",
        "feedback.request", "followup.reminder",

        // Asked for just now, but the portal has the link too.
        "telehealth.link",

        // Asked for by someone else, and sent only by SMS.
        "portal.family_code",

        // Alerts to staff numbers: they also go by SMS and in-app.
        "rpm.alert", "lab.critical", "system.alert",
    };

    public static bool IsEssential(string templateKey) => EssentialTemplates.Contains(templateKey);

    /// <summary>Does an opt-out stop this message? It stops only WhatsApp, and never an essential message.</summary>
    public static bool Stops(NotificationChannel channel, string templateKey, bool numberOptedOut) =>
        numberOptedOut && channel == NotificationChannel.WhatsApp && !IsEssential(templateKey);

    /// <summary>The number's opt-out while it is in force; <paramref name="phone"/> in E.164 (<c>Phone.Normalize</c>).</summary>
    public static Expression<Func<WhatsAppOptOut, bool>> InForceFor(string phone) => o => o.Phone == phone && o.OptedOut;
}

/// <summary>Keeps the number's WhatsApp opt-out in step with its latest STOP or START reply, in the webhook's transaction.</summary>
internal sealed class WhatsAppOptOutRecorder(HospitalDbContext db) : IDomainEventHandler<WhatsAppOptOutChanged>
{
    public async Task HandleAsync(WhatsAppOptOutChanged reply, CancellationToken cancellationToken)
    {
        // Local first: a row added earlier in the same save is not in the database yet.
        var optOut = db.Set<WhatsAppOptOut>().Local.FirstOrDefault(o => o.Phone == reply.Phone)
                     ?? await db.Set<WhatsAppOptOut>().FirstOrDefaultAsync(o => o.Phone == reply.Phone, cancellationToken);
        if (optOut is null)
        {
            optOut = new WhatsAppOptOut { Phone = reply.Phone };
            db.Add(optOut);
        }

        optOut.Apply(reply.OptedOut, reply.RepliedAt, reply.ProviderMessageId, reply.FacilityId);
    }
}
