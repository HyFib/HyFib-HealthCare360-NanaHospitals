using HyFib.HealthCare360.Modules.SupportServices.Domain.Housekeeping;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>
/// Housekeeping beyond the bed turnaround: the cleanings due on a day, the checklist answered item by item, the response
/// to a ward's request against its kind's target, and verification only by someone other than the cleaner.
/// </summary>
public sealed class HousekeepingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_daily_area_is_due_its_times_a_day_and_a_weekly_one_on_its_days()
    {
        var daily = new CleaningArea { Frequency = CleaningFrequency.Daily, TimesPerDay = 3 };
        daily.SlotsOn(new DateOnly(2026, 11, 8)).ShouldBe([1, 2, 3]);

        var weekly = new CleaningArea { Frequency = CleaningFrequency.Weekly, WeekDays = [DayOfWeek.Monday, DayOfWeek.Thursday] };
        weekly.SlotsOn(new DateOnly(2026, 11, 9)).ShouldBe([1]);
        weekly.SlotsOn(new DateOnly(2026, 11, 8)).ShouldBeEmpty();

        new CleaningArea { Frequency = CleaningFrequency.Daily, IsActive = false }.SlotsOn(new DateOnly(2026, 11, 8)).ShouldBeEmpty();
    }

    [Fact]
    public void Every_item_of_the_checklist_is_answered_and_one_not_done_says_why()
    {
        string[] expected = ["Floor mopped", "Bins emptied"];
        CleaningRules.CheckList(expected, [new("Floor mopped", true)]).Error!.Message.ShouldContain("Bins emptied");
        CleaningRules.CheckList(expected, [new("Floor mopped", true), new("Bins emptied", false)]).Error!.Message.ShouldContain("why");
        CleaningRules.CheckList(expected, [new("Floor mopped", true), new("Bins emptied", false, "No liners")]).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_cleaning_is_verified_only_by_someone_who_did_not_do_it_and_one_sent_back_says_what_was_not_clean()
    {
        var cleaner = Guid.NewGuid();
        var record = new CleaningRecord { CleanedBy = cleaner };
        record.Verify(cleaner, "Cleaner", true, null, Now).Error!.Code.ShouldBe("housekeeping.verify_own");
        record.Verify(Guid.NewGuid(), "Supervisor", false, null, Now).Error!.Fields!.Keys.ShouldContain("remarks");
        record.Verify(Guid.NewGuid(), "Supervisor", false, "Bins full", Now).IsSuccess.ShouldBeTrue();
        record.Status.ShouldBe(CleaningStatus.Redo);
        record.Verify(Guid.NewGuid(), "Supervisor", true, null, Now).Error!.Code.ShouldBe("housekeeping.already_verified");
    }

    [Fact]
    public void A_spill_is_answered_within_15_minutes_and_its_response_is_late_beyond()
    {
        CleaningRequest.Target(CleaningRequestKind.Spill).ShouldBe(TimeSpan.FromMinutes(15));
        CleaningRequest.Target(CleaningRequestKind.Terminal).ShouldBe(TimeSpan.FromMinutes(60));

        var spill = new CleaningRequest { Kind = CleaningRequestKind.Spill, RequestedAt = Now };
        spill.ResponseLate(Now.AddMinutes(10)).ShouldBeFalse();
        spill.ResponseLate(Now.AddMinutes(20)).ShouldBeTrue();
        spill.Acknowledge(Guid.NewGuid(), "Housekeeper", Now.AddMinutes(12)).IsSuccess.ShouldBeTrue();
        (spill.ResponseMinutes, spill.ResponseLate(Now.AddHours(2))).ShouldBe((12, false));
    }

    [Fact]
    public void A_request_is_done_against_its_kinds_checklist_and_verified_by_someone_else()
    {
        var cleaner = Guid.NewGuid();
        var terminal = new CleaningRequest { Kind = CleaningRequestKind.Terminal, RequestedAt = Now };
        terminal.Complete([new("Floor mopped with disinfectant", true)], null, cleaner, "Housekeeper", Now.AddMinutes(40)).Error!.Fields!.Keys.ShouldContain("checklist");

        var all = CleaningRequest.ChecklistFor(CleaningRequestKind.Terminal).Select(i => new ChecklistResult(i, true)).ToList();
        terminal.Complete(all, null, cleaner, "Housekeeper", Now.AddMinutes(40)).IsSuccess.ShouldBeTrue();
        (terminal.Status, terminal.ResponseMinutes, terminal.CompletionMinutes).ShouldBe((CleaningRequestStatus.AwaitingVerification, 40, 40));
        terminal.Verify(cleaner, "Housekeeper", true, null, Now).Error!.Code.ShouldBe("housekeeping.verify_own");
        terminal.Verify(Guid.NewGuid(), "Sister in charge", false, "Mattress not wiped", Now).IsSuccess.ShouldBeTrue();
        terminal.Status.ShouldBe(CleaningRequestStatus.Redo);

        // Done again, it is checked again.
        terminal.Complete(all, "Mattress wiped", cleaner, "Housekeeper", Now.AddMinutes(55)).IsSuccess.ShouldBeTrue();
        terminal.Verify(Guid.NewGuid(), "Sister in charge", true, null, Now).IsSuccess.ShouldBeTrue();
        terminal.Status.ShouldBe(CleaningRequestStatus.Verified);
    }
}
