using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Seed;

/// <summary>
/// Seeds a standard set of feedback forms for every active facility, and the message templates Engagement adds. The
/// Platform already seeds <c>feedback.request</c> and <c>followup.reminder</c>. The seeder is idempotent by facility
/// and code, and by template key and channel. It never re-creates something an administrator deleted. SMS templates
/// still need their DLT template id before they can be sent in India.
/// </summary>
internal sealed class EngagementSeeder : ISeeder
{
    public int Order => 130;

    private static readonly (string Code, string Title, FeedbackFormType Type, FeedbackAspect[] Aspects, string Intro)[] Forms =
    [
        ("OPD", "Out-patient feedback", FeedbackFormType.OP,
            [FeedbackAspect.Doctor, FeedbackAspect.Nursing, FeedbackAspect.FrontDesk, FeedbackAspect.Billing, FeedbackAspect.Cleanliness, FeedbackAspect.Overall],
            "Tell us about today's visit. It takes less than a minute."),
        ("IPD", "In-patient feedback", FeedbackFormType.IP,
            [FeedbackAspect.Doctor, FeedbackAspect.Nursing, FeedbackAspect.Food, FeedbackAspect.Cleanliness, FeedbackAspect.Billing, FeedbackAspect.Overall],
            "Thank you for choosing us. How was your stay?"),
        ("PORTAL", "Share your experience", FeedbackFormType.Portal,
            [FeedbackAspect.Doctor, FeedbackAspect.FrontDesk, FeedbackAspect.Billing, FeedbackAspect.Overall],
            "Your feedback helps us care for you better."),
        ("QR-GEN", "How are we doing?", FeedbackFormType.General,
            [FeedbackAspect.Cleanliness, FeedbackAspect.FrontDesk, FeedbackAspect.Overall],
            "Scan, rate and help us improve. No login needed."),
    ];

    private static readonly (string Key, NotificationChannel Channel, string Body, string Description)[] Templates =
    [
        ("campaign.message", NotificationChannel.Sms, "{{message}} Stop offers: {{optout}} - {{hospital}}",
            "Marketing campaign (consented patients only; set the DLT template id before use)"),
        ("campaign.message", NotificationChannel.WhatsApp, "{{message}}\n\nTo stop receiving offers from us, open {{optout}}",
            "Marketing campaign on WhatsApp (consented patients only; approved template name required)"),
        (FeedbackService.ComplaintAcknowledgedTemplate, NotificationChannel.Sms,
            "Dear {{name}}, we are sorry about your experience. Complaint {{reference}} is registered and our team will contact you soon. - {{hospital}}",
            "Acknowledges a complaint raised from low feedback when the patient asked for a call-back"),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var facilities = await db.Set<Facility>().Where(f => f.IsActive).Select(f => f.Id).ToListAsync(cancellationToken);
        var existingForms = await db.Set<FeedbackForm>().IgnoreQueryFilters()
            .Select(f => new { f.FacilityId, f.Code })
            .ToListAsync(cancellationToken);
        foreach (var facilityId in facilities)
        {
            foreach (var form in Forms.Where(f => !existingForms.Any(e => e.FacilityId == facilityId && e.Code == f.Code)))
            {
                db.Add(new FeedbackForm
                {
                    FacilityId = facilityId,
                    Code = form.Code,
                    Title = form.Title,
                    Type = form.Type,
                    Aspects = form.Aspects.Select(a => a.ToString()).ToList(),
                    IncludesNps = true,
                    AllowsComment = true,
                    IntroText = form.Intro,
                    ThankYouText = FeedbackService.DefaultThankYou,
                    PublicToken = OpaqueToken.New(),
                    IsActive = true,
                });
            }
        }

        var existingTemplates = await db.Set<NotificationTemplate>().IgnoreQueryFilters()
            .Where(t => t.FacilityId == null && t.Language == "en")
            .Select(t => new { t.Key, t.Channel })
            .ToListAsync(cancellationToken);
        foreach (var template in Templates.Where(t => !existingTemplates.Any(e => e.Key == t.Key && e.Channel == t.Channel)))
        {
            db.Add(new NotificationTemplate { Key = template.Key, Channel = template.Channel, Body = template.Body, Description = template.Description });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
