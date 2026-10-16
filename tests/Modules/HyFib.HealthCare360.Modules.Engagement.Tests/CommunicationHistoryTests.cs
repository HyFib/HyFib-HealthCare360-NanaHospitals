using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Patient360;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Engagement.Services.Patient360Service;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class CommunicationHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.FromHours(5.5));

    [Fact]
    public void Merges_messages_campaigns_and_calls_newest_first()
    {
        CommunicationEntry[] entries =
        [
            Message(Now.AddDays(-3)),
            Call(Now.AddDays(-1)),
            Campaign(Now.AddDays(-2)),
            Message(Now),
        ];

        var page = Page(entries, total: 4, PageRequest.Create(1, 25));

        page.Items.Select(e => e.OccurredAt).ShouldBe([Now, Now.AddDays(-1), Now.AddDays(-2), Now.AddDays(-3)]);
        page.Items.Select(e => e.Kind).ShouldBe(
            [CommunicationKind.Message, CommunicationKind.FollowUpCall, CommunicationKind.Campaign, CommunicationKind.Message]);
        page.TotalCount.ShouldBe(4);
    }

    [Fact]
    public void A_later_page_continues_across_the_sources_and_keeps_the_full_count()
    {
        // Each source is read only as far as the end of the requested page, so page two sees the first four of each.
        CommunicationEntry[] entries = [.. Enumerable.Range(0, 4).SelectMany(i => new[] { Message(Now.AddHours(-2 * i)), Call(Now.AddHours(-2 * i - 1)) })];

        var page = Page(entries, total: 11, PageRequest.Create(2, 2));

        page.Items.Select(e => e.OccurredAt).ShouldBe([Now.AddHours(-2), Now.AddHours(-3)]);
        page.Page.ShouldBe(2);
        page.PageSize.ShouldBe(2);
        page.TotalCount.ShouldBe(11);
        page.TotalPages.ShouldBe(6);
    }

    [Fact]
    public void A_page_past_the_end_is_empty()
    {
        Page([Message(Now)], total: 1, PageRequest.Create(3, 25)).Items.ShouldBeEmpty();
    }

    private static CommunicationEntry Message(DateTimeOffset at) => new(
        Guid.NewGuid(), CommunicationKind.Message, at, NotificationChannel.Sms, "appointment.reminder", "Appointment reminder", "+91******3210",
        NotificationStatus.Delivered, at, at, CampaignId: null, CampaignName: null, SkipReason: null, FollowUpId: null, FollowUpType: null,
        FollowUpStatus: null, CallOutcome: null, CallAttempts: null);

    private static CommunicationEntry Campaign(DateTimeOffset at) => new(
        Guid.NewGuid(), CommunicationKind.Campaign, at, NotificationChannel.WhatsApp, "campaign.general", Purpose: null, Recipient: null,
        NotificationStatus.Skipped, SentAt: null, DeliveredAt: null, Guid.NewGuid(), "Diabetes camp", "No marketing consent", FollowUpId: null,
        FollowUpType: null, FollowUpStatus: null, CallOutcome: null, CallAttempts: null);

    private static CommunicationEntry Call(DateTimeOffset at)
    {
        var id = Guid.NewGuid();
        return new(
            id, CommunicationKind.FollowUpCall, at, Channel: null, TemplateKey: null, Purpose: null, Recipient: null, Status: null, SentAt: null,
            DeliveredAt: null, CampaignId: null, CampaignName: null, SkipReason: null, id, FollowUpType.PostDischarge, FollowUpStatus.InProgress,
            FollowUpOutcome.NotReachable, 1);
    }
}
