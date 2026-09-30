using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Privacy.Seed;

/// <summary>
/// Seeds the privacy messages: a request received, a request answered, and the notice to people affected by a breach
/// (DPDP Rules 2025, rule 7(1)). Each names whom to contact (rule 9). Idempotent by key and channel, and never
/// re-creates a template an administrator deleted. SMS templates still need their DLT template id before they can be
/// sent in India; e-mail values are HTML-encoded by the renderer.
/// </summary>
internal sealed class PrivacySeeder : ISeeder
{
    public int Order => 195;

    private const string Paragraph = "<p style=\"white-space:pre-line\">";

    private static readonly (string Key, NotificationChannel Channel, string? Subject, string Body, string Description)[] Templates =
    [
        (DataRequestService.ReceivedTemplate, NotificationChannel.Sms, null,
            "Dear {{name}}, we received your privacy request {{reference}} on {{received}}. We will reply by {{due}}. Questions: {{contact}} - {{hospital}}",
            "Privacy request received (set the DLT template id before use)"),
        (DataRequestService.ReceivedTemplate, NotificationChannel.Email, "We received your privacy request {{reference}}",
            "<p>Dear {{name}},</p>"
            + "<p>We received your request {{reference}} ({{type}}) on {{received}}. We will reply by <strong>{{due}}</strong>.</p>"
            + "<p>You can follow it in the patient portal, under Your privacy.</p>"
            + "<p>If you have questions, contact our {{contact}}.</p>"
            + "<p>{{hospital}}</p>",
            "Privacy request received"),
        (DataRequestService.ClosedTemplate, NotificationChannel.Sms, null,
            "Dear {{name}}, your privacy request {{reference}} is {{outcome}}. See our reply in the patient portal or at the front desk. Questions: {{contact}} - {{hospital}}",
            "Privacy request answered (set the DLT template id before use)"),
        (DataRequestService.ClosedTemplate, NotificationChannel.Email, "Our reply to your privacy request {{reference}}",
            "<p>Dear {{name}},</p>"
            + "<p>Your request {{reference}} is <strong>{{outcome}}</strong>. Our reply:</p>"
            + Paragraph + "{{response}}</p>"
            + "<p>If you have questions, or you are not satisfied with this reply, contact our {{contact}}. You may also complain to the Data Protection Board of India.</p>"
            + "<p>{{hospital}}</p>",
            "Privacy request answered, with the reply"),
        (BreachService.NoticeTemplate, NotificationChannel.Sms, null,
            "Important notice from {{hospital}} about your personal data ({{reference}}): {{summary}} Full notice: {{link}} Questions: {{contact}}",
            "Personal data breach notice (set the DLT template id before use)"),
        (BreachService.NoticeTemplate, NotificationChannel.Email, "Important: a notice about your personal data ({{reference}})",
            "<p>Dear {{name}},</p>"
            + "<p>We are writing to tell you about an incident that affected personal data we hold about you.</p>"
            + "<p><strong>What happened</strong></p>" + Paragraph + "{{description}}</p>"
            + "<p><strong>When</strong>: {{when}}<br><strong>What data</strong>: {{data}}</p>"
            + "<p><strong>What this may mean for you</strong></p>" + Paragraph + "{{consequences}}</p>"
            + "<p><strong>What we are doing</strong></p>" + Paragraph + "{{mitigation}}</p>"
            + "<p><strong>What you can do</strong></p>" + Paragraph + "{{steps}}</p>"
            + "<p>If you have questions, contact our {{contact}}. You can also read this notice in the patient portal.</p>"
            + "<p>{{hospital}}</p>",
            "Personal data breach notice (DPDP Rules 2025, rule 7(1))"),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.Set<NotificationTemplate>().IgnoreQueryFilters()
            .Where(t => t.FacilityId == null && t.Language == "en")
            .Select(t => new { t.Key, t.Channel })
            .ToListAsync(cancellationToken);
        foreach (var template in Templates.Where(t => !existing.Any(e => e.Key == t.Key && e.Channel == t.Channel)))
        {
            db.Add(new NotificationTemplate
            {
                Key = template.Key,
                Channel = template.Channel,
                Language = "en",
                Subject = template.Subject,
                Body = template.Body,
                Description = template.Description,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
