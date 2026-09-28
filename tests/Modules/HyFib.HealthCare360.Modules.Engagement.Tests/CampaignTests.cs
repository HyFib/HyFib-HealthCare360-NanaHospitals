using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class CampaignTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);

    private static Campaign Draft() => new() { Name = "Heart check-up month", TemplateKey = "campaign.message", Message = "20% off", Status = CampaignStatus.Draft };

    [Fact]
    public void A_draft_with_a_consenting_audience_can_be_scheduled_for_the_future()
    {
        var campaign = Draft();
        var user = Guid.NewGuid();

        campaign.Schedule(Now.AddHours(3), 120, user, Now).IsSuccess.ShouldBeTrue();

        campaign.Status.ShouldBe(CampaignStatus.Scheduled);
        campaign.AudienceCount.ShouldBe(120);
        campaign.ScheduledByUserId.ShouldBe(user);
        campaign.IsEditable.ShouldBeFalse();
    }

    [Fact]
    public void Scheduling_needs_a_future_time_a_consenting_audience_and_a_draft()
    {
        Draft().Schedule(Now.AddMinutes(-5), 10, null, Now).Error!.Fields!.ShouldContainKey("scheduledAt");
        Draft().Schedule(Now.AddHours(1), 0, null, Now).Error!.Code.ShouldBe("campaign.empty_audience");

        var scheduled = Draft();
        scheduled.Schedule(Now.AddHours(1), 10, null, Now);
        scheduled.Schedule(Now.AddHours(2), 10, null, Now).Error!.Code.ShouldBe("campaign.not_draft");
    }

    [Fact]
    public void A_scheduled_campaign_can_go_back_to_draft_until_sending_starts()
    {
        var campaign = Draft();
        campaign.Schedule(Now.AddHours(1), 10, Guid.NewGuid(), Now);

        campaign.Unschedule().IsSuccess.ShouldBeTrue();
        campaign.Status.ShouldBe(CampaignStatus.Draft);
        campaign.ScheduledAt.ShouldBeNull();

        campaign.Schedule(Now.AddMinutes(10), 10, null, Now);
        campaign.BeginSending(Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();
        campaign.Unschedule().IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Sending_starts_only_when_due_and_completes_only_while_sending()
    {
        var campaign = Draft();
        campaign.Schedule(Now.AddHours(1), 10, null, Now);

        campaign.BeginSending(Now).Error!.Code.ShouldBe("campaign.not_due");
        campaign.Complete(Now).IsFailure.ShouldBeTrue();

        campaign.BeginSending(Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        campaign.RecordBatch(200, 0);
        campaign.RecordBatch(35, 2);
        campaign.Complete(Now.AddHours(2)).IsSuccess.ShouldBeTrue();

        campaign.QueuedCount.ShouldBe(235);
        campaign.SkippedCount.ShouldBe(2);
        campaign.Status.ShouldBe(CampaignStatus.Completed);
    }

    [Fact]
    public void Cancelling_stops_further_batches_but_finished_campaigns_stay_finished()
    {
        var campaign = Draft();

        campaign.Cancel(" Wrong offer text ", Now).IsSuccess.ShouldBeTrue();
        campaign.CancelReason.ShouldBe("Wrong offer text");
        campaign.Cancel("again", Now).Error!.Code.ShouldBe("campaign.closed");
    }

    [Fact]
    public void Segments_are_stored_trimmed_and_without_duplicate_departments()
    {
        var department = Guid.NewGuid();
        var campaign = Draft();

        campaign.SetSegment(new CampaignSegment(30, 60, Gender.Female, "  Chennai ", null, null, [department, department]));

        campaign.Segment.MinAge.ShouldBe(30);
        campaign.Segment.MaxAge.ShouldBe(60);
        campaign.Segment.Gender.ShouldBe(Gender.Female);
        campaign.Segment.City.ShouldBe("Chennai");
        campaign.Segment.DepartmentIds.ShouldBe([department]);
    }

    [Theory]
    [InlineData(9, 59, false)]
    [InlineData(10, 0, true)]
    [InlineData(20, 59, true)]
    [InlineData(21, 0, false)]
    [InlineData(2, 0, false)]
    public void Promotional_messages_go_out_only_between_10_and_21_hours(int hour, int minute, bool open) =>
        CampaignSendWindow.IsOpen(new DateTime(2026, 9, 26, hour, minute, 0)).ShouldBe(open);

    [Fact]
    public void Outside_the_window_sending_waits_for_the_next_opening()
    {
        CampaignSendWindow.NextOpening(new DateTime(2026, 9, 26, 7, 30, 0)).ShouldBe(new DateTime(2026, 9, 26, 10, 0, 0));
        CampaignSendWindow.NextOpening(new DateTime(2026, 9, 26, 22, 15, 0)).ShouldBe(new DateTime(2026, 9, 27, 10, 0, 0));
        CampaignSendWindow.NextOpening(new DateTime(2026, 9, 26, 12, 0, 0)).ShouldBe(new DateTime(2026, 9, 26, 12, 0, 0));
    }

    [Fact]
    public void The_window_is_judged_in_india_time()
    {
        // 04:00 UTC is 09:30 IST (closed); 04:30 UTC is 10:00 IST (open).
        CampaignSendWindow.IsOpen(EngagementClock.ToLocal(new DateTimeOffset(2026, 9, 26, 4, 0, 0, TimeSpan.Zero))).ShouldBeFalse();
        CampaignSendWindow.IsOpen(EngagementClock.ToLocal(new DateTimeOffset(2026, 9, 26, 4, 30, 0, TimeSpan.Zero))).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Hindi", "hi")]
    [InlineData("hi", "hi")]
    [InlineData("हिन्दी", "hi")]
    [InlineData("Tamil", "en")]
    [InlineData(null, "en")]
    public void Message_language_follows_the_patients_preference(string? preferred, string expected) =>
        CampaignService.LanguageCode(preferred).ShouldBe(expected);
}
