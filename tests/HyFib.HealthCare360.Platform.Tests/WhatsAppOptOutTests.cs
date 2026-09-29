using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// A WhatsApp STOP stops every non-essential WhatsApp message to the number, whichever module queued it, and START
/// resumes them. SMS is a separate channel and is never stopped by a WhatsApp reply.
/// </summary>
public sealed class WhatsAppOptOutTests
{
    private const string Number = "+919876543210";
    private static readonly DateTimeOffset Stop = new(2026, 9, 29, 4, 30, 0, TimeSpan.Zero);

    private static WhatsAppOptOut Replied(bool optedOut, DateTimeOffset at, string phone = Number)
    {
        var optOut = new WhatsAppOptOut { Phone = phone };
        optOut.Apply(optedOut, at, "wamid.IN1", null);
        return optOut;
    }

    [Fact]
    public void Every_built_in_template_is_classified_exactly_once()
    {
        // Adding a template to DefaultNotificationTemplates fails here until someone decides whether a WhatsApp STOP
        // stops it (WhatsAppOptOutPolicy.StoppedTemplates) or it must still reach the person (EssentialTemplates).
        var keys = DefaultNotificationTemplates.All.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            (WhatsAppOptOutPolicy.EssentialTemplates.Contains(key) ^ WhatsAppOptOutPolicy.StoppedTemplates.Contains(key))
                .ShouldBeTrue($"Classify the template '{key}' in WhatsAppOptOutPolicy: essential (still sent after STOP) or stopped.");
        }

        WhatsAppOptOutPolicy.EssentialTemplates.Concat(WhatsAppOptOutPolicy.StoppedTemplates)
            .Where(k => !keys.Contains(k))
            .ShouldBeEmpty("WhatsAppOptOutPolicy lists templates that are no longer built in.");
    }

    [Fact]
    public void Only_sign_in_codes_are_essential() =>
        WhatsAppOptOutPolicy.EssentialTemplates.ShouldBe(["auth.otp"]);

    [Theory]
    [InlineData("appointment.reminder")]
    [InlineData("telehealth.link")]
    [InlineData("payment.link")]
    [InlineData("lab.report_ready")]
    [InlineData("campaign.message")]
    [InlineData("appointment.waitlist")]
    public void A_stop_stops_non_essential_whatsapp_messages_including_templates_of_modules(string templateKey) =>
        WhatsAppOptOutPolicy.Stops(NotificationChannel.WhatsApp, templateKey, numberOptedOut: true).ShouldBeTrue();

    [Fact]
    public void Sign_in_codes_still_go_out_on_whatsapp_after_a_stop() =>
        WhatsAppOptOutPolicy.Stops(NotificationChannel.WhatsApp, "auth.otp", numberOptedOut: true).ShouldBeFalse();

    [Theory]
    [InlineData(NotificationChannel.Sms)]
    [InlineData(NotificationChannel.Email)]
    public void A_whatsapp_stop_never_stops_other_channels(NotificationChannel channel) =>
        WhatsAppOptOutPolicy.Stops(channel, "appointment.reminder", numberOptedOut: true).ShouldBeFalse();

    [Fact]
    public void Numbers_that_did_not_opt_out_get_everything() =>
        WhatsAppOptOutPolicy.Stops(NotificationChannel.WhatsApp, "appointment.reminder", numberOptedOut: false).ShouldBeFalse();

    [Fact]
    public void Start_resumes_delivery_after_a_stop()
    {
        var optOut = Replied(optedOut: true, Stop);
        WhatsAppOptOutPolicy.Stops(NotificationChannel.WhatsApp, "appointment.reminder", optOut.OptedOut).ShouldBeTrue();

        optOut.Apply(optedOut: false, Stop.AddMinutes(5), "wamid.IN2", null).ShouldBeTrue();

        optOut.OptedOut.ShouldBeFalse();
        optOut.ChangedAt.ShouldBe(Stop.AddMinutes(5));
        optOut.ProviderMessageId.ShouldBe("wamid.IN2");
        WhatsAppOptOutPolicy.Stops(NotificationChannel.WhatsApp, "appointment.reminder", optOut.OptedOut).ShouldBeFalse();
    }

    [Fact]
    public void A_reply_sent_before_the_one_applied_changes_nothing()
    {
        // START at 10:05 was applied; the STOP sent at 10:00 arrives late (Meta redelivers out of order).
        var optOut = Replied(optedOut: false, Stop.AddMinutes(5));

        optOut.Apply(optedOut: true, Stop, "wamid.LATE", null).ShouldBeFalse();

        optOut.OptedOut.ShouldBeFalse();
        optOut.ProviderMessageId.ShouldBe("wamid.IN1");
    }

    [Fact]
    public void Of_two_replies_sent_in_the_same_second_the_last_one_processed_wins()
    {
        var optOut = Replied(optedOut: false, Stop);

        optOut.Apply(optedOut: true, Stop, "wamid.IN2", null).ShouldBeTrue();

        optOut.OptedOut.ShouldBeTrue();
    }

    [Fact]
    public void Only_an_opt_out_in_force_for_the_number_blocks_it()
    {
        List<WhatsAppOptOut> optOuts = [Replied(optedOut: true, Stop), Replied(optedOut: false, Stop, "+919876543211")];

        optOuts.AsQueryable().Any(WhatsAppOptOutPolicy.InForceFor(Number)).ShouldBeTrue();
        optOuts.AsQueryable().Any(WhatsAppOptOutPolicy.InForceFor("+919876543211")).ShouldBeFalse();
        optOuts.AsQueryable().Any(WhatsAppOptOutPolicy.InForceFor("+919876543212")).ShouldBeFalse();
    }

    [Fact]
    public void The_delivery_lookup_is_one_indexed_query_on_the_number()
    {
        using var db = ModelOnlyContext();

        var sql = db.Set<WhatsAppOptOut>().Where(WhatsAppOptOutPolicy.InForceFor(Number)).ToQueryString();

        sql.ShouldContain("platform.whatsapp_opt_outs");
        sql.ShouldContain("phone");
        sql.ShouldContain("opted_out");
        sql.ShouldContain("is_deleted");
        var index = db.Model.FindEntityType(typeof(WhatsAppOptOut))!.GetIndexes().Single(i => i.IsUnique);
        index.Properties.Select(p => p.Name).ShouldBe([nameof(WhatsAppOptOut.Phone)]);
    }

    /// <summary>The platform's EF model with the Npgsql provider; queries are translated, never run.</summary>
    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>()
            .UseNpgsql("Host=localhost;Database=hc360_model_check")
            .UseSnakeCaseNamingConvention()
            .Options,
        new ModuleCatalog([]));
}
