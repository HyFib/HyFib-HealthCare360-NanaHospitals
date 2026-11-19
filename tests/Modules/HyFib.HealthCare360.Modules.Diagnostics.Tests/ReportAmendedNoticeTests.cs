using System.Text.RegularExpressions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Seed;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

/// <summary>
/// L3: a corrected report reaches the patient, who otherwise keeps the wrong one, and, when no doctor in HC360 can be
/// alerted of it (a walk-in or an outside referral), the laboratory, which phones the doctor named on the order. The
/// notice goes as the "report ready" one does, on its channels, under its own template, which the seeder adds in English,
/// Hindi and Tamil.
/// </summary>
public sealed partial class ReportAmendedNoticeTests
{
    private static readonly NotificationChannel[] Channels = [NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Email];

    private static IEnumerable<(string Key, NotificationChannel Channel, string Language, string? Subject, string Body, string Description)> Amended =>
        DiagnosticsSeeder.Templates.Where(t => t.Key == ReportDelivery.ReportAmendedTemplate);

    [Fact]
    public void The_amended_report_notice_is_seeded_for_every_channel_of_the_report_ready_one()
    {
        ReportDelivery.ReportAmendedTemplate.ShouldBe("lab.report_amended");
        Amended.Select(t => t.Channel).Distinct().ShouldBe(Channels, ignoreOrder: true);
    }

    [Fact]
    public void It_is_seeded_in_english_hindi_and_tamil_on_each_channel_once()
    {
        Amended.Select(t => (t.Channel, t.Language)).ShouldBe(
            [.. Channels.SelectMany(channel => new[] { "en", "hi", "ta" }.Select(language => (channel, language)))],
            ignoreOrder: true);
        Amended.Where(t => t.Language == "hi").ShouldAllBe(t => Devanagari().IsMatch(t.Body));
        Amended.Where(t => t.Language == "ta").ShouldAllBe(t => Tamil().IsMatch(t.Body));
        Amended.Where(t => t.Channel == NotificationChannel.Email).ShouldAllBe(t => !string.IsNullOrWhiteSpace(t.Subject));
    }

    [Fact]
    public void Each_text_names_the_patient_and_the_report_and_uses_nothing_the_notice_does_not_give()
    {
        foreach (var template in Amended)
        {
            var placeholders = Placeholder().Matches(template.Body + template.Subject).Select(m => m.Groups[1].Value).ToHashSet();
            placeholders.ShouldBeSubsetOf(["name", "reference", "hospital"], $"{template.Channel} {template.Language}");
            placeholders.ShouldContain("name", $"{template.Channel} {template.Language}");
            placeholders.ShouldContain("reference", $"{template.Channel} {template.Language}");
        }
    }

    [Fact]
    public void Its_whatsapp_templates_wait_for_meta_and_the_other_channels_go_at_once()
    {
        DiagnosticsSeeder.SeededNotInUse.ShouldContain((ReportDelivery.ReportAmendedTemplate, NotificationChannel.WhatsApp));
        DiagnosticsSeeder.SeededNotInUse.ShouldNotContain((ReportDelivery.ReportAmendedTemplate, NotificationChannel.Sms));
        DiagnosticsSeeder.SeededNotInUse.ShouldNotContain((ReportDelivery.ReportAmendedTemplate, NotificationChannel.Email));
    }

    [Fact]
    public void Without_a_doctor_to_alert_the_lab_is_told_whom_to_phone()
    {
        var report = new LabReport { ReportNumber = "NHLR2600042", Revision = 1 };

        var (title, body) = LabReportService.NoDoctorWords(report, " Dr. S. Murugan, Bodinayakanur ");

        title.ShouldBe("Lab report amended: NHLR2600042: tell the doctor");
        body.ShouldBe("Revision 1 of report NHLR2600042 corrects released results, and no doctor in HC360 is alerted of it. " +
                      "Phone Dr. S. Murugan, Bodinayakanur, the doctor named on the order, and tell them the changes.");
        LabReportService.NoDoctorWords(report, null).Body.ShouldEndWith(
            "No doctor is named on the order: ask the patient who their doctor is, and tell that doctor the changes.");
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z]+)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\p{IsDevanagari}")]
    private static partial Regex Devanagari();

    [GeneratedRegex(@"\p{IsTamil}")]
    private static partial Regex Tamil();
}
