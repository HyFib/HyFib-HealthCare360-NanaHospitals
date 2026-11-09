using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>
/// The rules of a restraint (Mental Healthcare Act 2017 s.97): a time-limited order with the reason and the alternatives
/// tried, the next check due an interval after the last, an order that runs out, renewal and release while restrained
/// only, and the debrief after the release, once.
/// </summary>
public sealed class RestraintTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static Result<RestraintEpisode> Order(int hours = 2, int every = 15, string alternatives = "Talking down, a sitter") =>
        RestraintEpisode.Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, RestraintType.Physical, "Soft wrist holders",
            "Pulling at the central line", alternatives, hours, every, Guid.NewGuid(), Guid.NewGuid(), "Dr. Rao", Now);

    [Theory]
    [InlineData(0, 15)]
    [InlineData(5, 15)]
    [InlineData(2, 10)]
    [InlineData(2, 90)]
    public void An_order_runs_one_to_four_hours_with_checks_every_15_to_60_minutes(int hours, int every) =>
        Order(hours, every).IsFailure.ShouldBeTrue();

    [Fact]
    public void An_order_names_the_less_restrictive_measures_tried_first() => Order(alternatives: " ").IsFailure.ShouldBeTrue();

    [Fact]
    public void The_next_check_is_due_an_interval_after_the_last_and_overdue_after_it()
    {
        var episode = Order().Value;
        (episode.OrderedUntil, episode.NextCheckDue).ShouldBe((Now.AddHours(2), Now.AddMinutes(15)));
        episode.IsCheckOverdue(Now.AddMinutes(15)).ShouldBeFalse();
        episode.IsCheckOverdue(Now.AddMinutes(16)).ShouldBeTrue();

        episode.RecordCheck(Now.AddMinutes(14), Now.AddMinutes(16)).IsSuccess.ShouldBeTrue();
        episode.NextCheckDue.ShouldBe(Now.AddMinutes(29));
        episode.IsCheckOverdue(Now.AddMinutes(16)).ShouldBeFalse();

        // A check entered late, for an earlier time, does not move the next one back.
        episode.RecordCheck(Now.AddMinutes(5), Now.AddMinutes(17)).IsSuccess.ShouldBeTrue();
        episode.NextCheckDue.ShouldBe(Now.AddMinutes(29));

        // Not before the order, not in the future.
        episode.RecordCheck(Now.AddMinutes(-1), Now.AddMinutes(17)).IsFailure.ShouldBeTrue();
        episode.RecordCheck(Now.AddMinutes(30), Now.AddMinutes(17)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void The_order_lapses_unless_a_doctor_renews_it_from_now()
    {
        var episode = Order(hours: 1).Value;
        episode.IsOrderLapsed(Now.AddMinutes(61)).ShouldBeTrue();

        episode.Renew(2, "Still agitated on review", Guid.NewGuid(), "Dr. Rao", Now.AddMinutes(61)).IsSuccess.ShouldBeTrue();
        episode.OrderedUntil.ShouldBe(Now.AddMinutes(181));
        episode.IsOrderLapsed(Now.AddMinutes(61)).ShouldBeFalse();
        episode.Renewals.ShouldHaveSingleItem().Reason.ShouldBe("Still agitated on review");
        episode.Renew(5, "Still agitated", null, null, Now.AddMinutes(62)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Released_it_takes_no_check_or_renewal_and_the_debrief_follows_once()
    {
        var episode = Order().Value;
        episode.RecordDebrief("Pain was the trigger", "Nurse", Now).Error!.Code.ShouldBe("restraint.active");

        episode.Release("Calm", Guid.NewGuid(), "Dr. Rao", Now.AddMinutes(30)).IsSuccess.ShouldBeTrue();
        (episode.IsActive, episode.IsCheckOverdue(Now.AddHours(5)), episode.IsOrderLapsed(Now.AddHours(5))).ShouldBe((false, false, false));
        episode.RecordCheck(Now.AddMinutes(31), Now.AddMinutes(31)).Error!.Code.ShouldBe("restraint.released");
        episode.Renew(1, "Agitated again", null, null, Now.AddMinutes(31)).Error!.Code.ShouldBe("restraint.released");
        episode.Release("Again", null, null, Now.AddMinutes(31)).Error!.Code.ShouldBe("restraint.released");

        episode.RecordDebrief("Pain was the trigger; analgesia before line care", "Nurse", Now.AddMinutes(40)).IsSuccess.ShouldBeTrue();
        episode.RecordDebrief("Again", "Nurse", Now.AddMinutes(41)).Error!.Code.ShouldBe("restraint.debriefed");
    }
}
