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
/// Which WhatsApp messages reach a number, in three tiers (INT-WA):
/// <list type="number">
/// <item>Essential: only messages that must reach the person for security. They go even after STOP.</item>
/// <item>Service messages about care the patient is having now (an appointment booked, a report ready, a bill, a
/// payment link): they go unless the number replied STOP (or switched WhatsApp off in the portal). As in the consent
/// model, they need no separate consent: the patient gave the number for them at registration.</item>
/// <item>Reminders, feedback requests and campaigns (<see cref="OptInTemplates"/>): the hospital starts them, so they
/// go only to a number with a recorded WhatsApp opt-in (<see cref="WhatsAppOptIn"/>), and never after STOP. The module
/// that queues them still checks the patient's own consent as well (updates or marketing).</item>
/// </list>
/// A STOP stops everything but essential messages, including messages the patient asked for just now (such as a video
/// consultation's join link), because the patient still has SMS, e-mail and the portal. Template keys not listed here,
/// such as templates added by a module or an administrator, are service messages.
/// </summary>
public static class WhatsAppOptOutPolicy
{
    /// <summary>Recorded on the notification log of a message that was not sent because of the opt-out.</summary>
    public const string SuppressedReason =
        "Not sent: this number replied STOP on WhatsApp (or switched WhatsApp off in the patient portal). It gets only sign-in codes on WhatsApp until it replies START or switches WhatsApp on again.";

    /// <summary>Recorded on the notification log of a reminder or campaign message to a number that never opted in.</summary>
    public const string NotOptedInReason =
        "Not sent: this number has not opted in to reminders on WhatsApp. Record the opt-in on the patient's consents, or the patient can switch WhatsApp on in the patient portal or reply START.";

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

        // Asked for from the patient's ABHA app, and sent only by SMS.
        "abdm.link_code",

        // Alerts to staff numbers: they also go by SMS and in-app.
        "rpm.alert", "lab.critical", "system.alert",

        // Sent to staff by e-mail and in the app, never on WhatsApp.
        "auth.credentials_changed",
    };

    /// <summary>
    /// Messages the hospital starts rather than the patient's care: they go on WhatsApp only to numbers that opted in.
    /// Built-in and module templates alike; a campaign with a template of its own is limited to opted-in numbers when
    /// its audience is built.
    /// </summary>
    public static IReadOnlySet<string> OptInTemplates { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "appointment.reminder", "followup.reminder", "feedback.request",

        // Modules: marketing campaigns (Engagement), refill reminders (Pharmacy), "no readings lately" (VirtualCare).
        "campaign.message", "pharmacy.refill_reminder", "rpm.device_silent",
    };

    public static bool IsEssential(string templateKey) => EssentialTemplates.Contains(templateKey);

    /// <summary>Does this template go on WhatsApp only to numbers that opted in?</summary>
    public static bool NeedsOptIn(string templateKey) => OptInTemplates.Contains(templateKey);

    /// <summary>Does an opt-out stop this message? It stops only WhatsApp, and never an essential message.</summary>
    public static bool Stops(NotificationChannel channel, string templateKey, bool numberOptedOut) =>
        numberOptedOut && channel == NotificationChannel.WhatsApp && !IsEssential(templateKey);

    /// <summary>The number's opt-out while it is in force; <paramref name="phone"/> in E.164 (<c>Phone.Normalize</c>).</summary>
    public static Expression<Func<WhatsAppOptOut, bool>> InForceFor(string phone) => o => o.Phone == phone && o.OptedOut;
}

/// <summary>
/// Keeps the number's WhatsApp opt-out in step with its latest STOP or START reply, in the webhook's transaction. A
/// START is also the number's opt-in, recorded as one (<see cref="WhatsAppOptInSource.WhatsAppStart"/>).
/// </summary>
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

        if (!optOut.Apply(reply.OptedOut, reply.RepliedAt, reply.ProviderMessageId, reply.FacilityId) || reply.OptedOut)
        {
            return;
        }

        var recorded = db.Set<WhatsAppOptIn>().Local.Any(o => o.ProviderMessageId == reply.ProviderMessageId)
                       || await db.Set<WhatsAppOptIn>().AnyAsync(o => o.ProviderMessageId == reply.ProviderMessageId, cancellationToken);
        if (!recorded)
        {
            db.Add(new WhatsAppOptIn
            {
                Phone = reply.Phone,
                Source = WhatsAppOptInSource.WhatsAppStart,
                OptedInAt = reply.RepliedAt,
                FacilityId = reply.FacilityId,
                ProviderMessageId = reply.ProviderMessageId,
            });
        }
    }
}
