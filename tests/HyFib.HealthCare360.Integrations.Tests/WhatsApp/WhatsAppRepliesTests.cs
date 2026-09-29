using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.WhatsApp;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Modules;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HyFib.HealthCare360.Integrations.Tests.WhatsApp;

/// <summary>
/// STOP and START replies are acted on once, when their message is first stored, and only the most recent reply of
/// each number counts (Meta redelivers webhooks, sometimes late and out of order).
/// </summary>
public sealed class WhatsAppRepliesTests
{
    private const string Number = "+919876543210";
    private static readonly Guid Facility = Guid.Parse("0f3e2d1c-0000-4000-8000-00000000000a");
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 4, 30, 0, TimeSpan.Zero);

    private static WhatsAppInbound Inbound(string id, string text, DateTimeOffset? sentAt = null, string from = "919876543210") =>
        new(id, from, "Ravi Kumar", "text", text, null, null, sentAt ?? Now.AddMinutes(-1), "106540352242922");

    private static List<InboundWhatsAppMessage> Receive(IReadOnlyList<WhatsAppInbound> batch, IReadOnlyCollection<string>? known = null,
        IReadOnlyDictionary<string, DateTimeOffset>? latestStored = null)
    {
        var received = WhatsAppInboundStore.NewMessages(batch, known ?? [], Facility, Now);
        WhatsAppReplies.Decide(received, latestStored ?? new Dictionary<string, DateTimeOffset>());
        return received;
    }

    private static WhatsAppOptOutChanged Event(InboundWhatsAppMessage message) =>
        message.DomainEvents.OfType<WhatsAppOptOutChanged>().ShouldHaveSingleItem();

    [Fact]
    public void A_new_stop_opts_the_number_out()
    {
        var message = Receive([Inbound("wamid.IN1", "STOP", Now.AddMinutes(-2))]).ShouldHaveSingleItem();

        message.KeywordAction.ShouldBe(WhatsAppKeywordAction.OptedOut);
        var raised = Event(message);
        (raised.Phone, raised.OptedOut, raised.RepliedAt, raised.ProviderMessageId, raised.FacilityId)
            .ShouldBe((Number, true, Now.AddMinutes(-2), "wamid.IN1", (Guid?)Facility));
    }

    [Fact]
    public void A_new_start_opts_the_number_back_in()
    {
        var message = Receive([Inbound("wamid.IN1", "Start")]).ShouldHaveSingleItem();

        message.KeywordAction.ShouldBe(WhatsAppKeywordAction.OptedIn);
        Event(message).OptedOut.ShouldBeFalse();
        Event(message).Phone.ShouldBe(Number);
    }

    [Fact]
    public void A_redelivered_stop_is_not_stored_again_and_changes_nothing()
    {
        Receive([Inbound("wamid.IN1", "STOP")], known: ["wamid.IN1"]).ShouldBeEmpty();
    }

    [Fact]
    public void A_reply_repeated_within_one_webhook_is_acted_on_once()
    {
        var received = Receive([Inbound("wamid.IN1", "STOP"), Inbound("wamid.IN1", "STOP")]);

        received.ShouldHaveSingleItem().DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void Ordinary_messages_change_nothing()
    {
        var message = Receive([Inbound("wamid.IN1", "please stop sending reminders on sunday")]).ShouldHaveSingleItem();

        message.KeywordAction.ShouldBe(WhatsAppKeywordAction.None);
        message.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Of_two_replies_in_one_webhook_only_the_later_one_counts()
    {
        // Sent STOP then START a minute later; the batch lists them out of order.
        var received = Receive([Inbound("wamid.START", "START", Now.AddMinutes(-1)), Inbound("wamid.STOP", "STOP", Now.AddMinutes(-2))]);

        received.Single(m => m.ProviderMessageId == "wamid.START").KeywordAction.ShouldBe(WhatsAppKeywordAction.OptedIn);
        received.Single(m => m.ProviderMessageId == "wamid.STOP").KeywordAction.ShouldBe(WhatsAppKeywordAction.Superseded);
        received.SelectMany(m => m.DomainEvents).OfType<WhatsAppOptOutChanged>().ShouldHaveSingleItem().OptedOut.ShouldBeFalse();
    }

    [Fact]
    public void A_reply_older_than_one_already_stored_changes_nothing()
    {
        // START sent at 09:58 was stored; this STOP from 09:55 arrives late.
        var message = Receive([Inbound("wamid.OLD", "STOP", Now.AddMinutes(-35))], latestStored: new Dictionary<string, DateTimeOffset> { [Number] = Now.AddMinutes(-32) })
            .ShouldHaveSingleItem();

        message.KeywordAction.ShouldBe(WhatsAppKeywordAction.Superseded);
        message.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Replies_from_different_numbers_are_independent()
    {
        var received = Receive([Inbound("wamid.A", "STOP"), Inbound("wamid.B", "START", from: "919876543211")],
            latestStored: new Dictionary<string, DateTimeOffset> { ["+919876543211"] = Now.AddHours(-1) });

        received.Select(m => m.KeywordAction).ShouldBe([WhatsAppKeywordAction.OptedOut, WhatsAppKeywordAction.OptedIn]);
    }

    [Fact]
    public void A_reply_without_a_timestamp_is_dated_when_it_arrived()
    {
        var message = Receive([new WhatsAppInbound("wamid.IN1", "919876543210", null, "text", "STOP", null, null, null, null)]).ShouldHaveSingleItem();

        Event(message).RepliedAt.ShouldBe(Now);
    }

    [Fact]
    public void Finds_the_latest_reply_of_each_number_ignoring_ordinary_messages()
    {
        List<InboundWhatsAppMessage> stored =
        [
            new() { FromPhone = Number, Keyword = WhatsAppKeyword.OptOut, SentAt = Now.AddHours(-3), ReceivedAt = Now.AddHours(-3) },
            new() { FromPhone = Number, Keyword = WhatsAppKeyword.OptIn, SentAt = null, ReceivedAt = Now.AddHours(-2) },
            new() { FromPhone = Number, Keyword = WhatsAppKeyword.None, SentAt = Now.AddHours(-1), ReceivedAt = Now.AddHours(-1) },
            new() { FromPhone = "+919876543211", Keyword = WhatsAppKeyword.OptOut, SentAt = Now.AddHours(-5), ReceivedAt = Now.AddHours(-5) },
        ];

        var latest = WhatsAppReplies.Latest(stored.AsQueryable(), [Number]).ToList();

        latest.ShouldBe([new LatestReply(Number, Now.AddHours(-2))]);
    }

    [Fact]
    public void The_latest_reply_query_translates_to_one_grouped_query()
    {
        using var db = ModelOnlyContext();

        var sql = WhatsAppReplies.Latest(db.Set<InboundWhatsAppMessage>(), [Number]).ToQueryString();

        sql.ShouldContain("integrations.whatsapp_inbound_messages");
        sql.ShouldContain("GROUP BY");
        sql.ShouldContain("COALESCE");
    }

    [Fact]
    public void Messages_stored_before_replies_were_acted_on_keep_an_empty_action()
    {
        // WhatsAppReplyBacklog finds the replies to act on by their empty action, so the column must stay nullable.
        using var db = ModelOnlyContext();

        var action = db.Model.FindEntityType(typeof(InboundWhatsAppMessage))!.FindProperty(nameof(InboundWhatsAppMessage.KeywordAction))!;

        action.IsNullable.ShouldBeTrue();
        action.GetColumnName().ShouldBe("keyword_action");
    }

    /// <summary>The EF model with the Npgsql provider; queries are translated, never run.</summary>
    private static HospitalDbContext ModelOnlyContext() => new(
        new DbContextOptionsBuilder<HospitalDbContext>().UseNpgsql("Host=localhost;Database=hc360_model_check").UseSnakeCaseNamingConvention().Options,
        new ModuleCatalog([new IntegrationsModule()]));

    [Fact]
    public void The_confirmation_says_what_the_reply_did_in_plain_words()
    {
        WhatsAppReplyConfirmationHandler.Text(optedOut: true, "Nana Hospitals")
            .ShouldBe("You won't get WhatsApp messages or offers from Nana Hospitals any more, except sign-in codes. Reply START to get them again.");
        WhatsAppReplyConfirmationHandler.Text(optedOut: false, "Nana Hospitals")
            .ShouldBe("You'll get WhatsApp messages from Nana Hospitals again. Reply STOP to stop them.");
    }
}
