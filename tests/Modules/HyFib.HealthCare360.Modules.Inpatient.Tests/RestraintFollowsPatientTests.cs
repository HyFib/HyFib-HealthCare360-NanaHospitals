using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>
/// A restraint follows the patient: it moves with a transfer to another ward, it holds a discharge until a doctor releases
/// it (a death ends it), and its overdue checks and lapsed order are alerted once each period.
/// </summary>
public sealed class RestraintFollowsPatientTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    private static RestraintEpisode Order(Guid? ward = null, int hours = 2, int every = 15) =>
        RestraintEpisode.Order(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ward, RestraintType.Physical, "Soft wrist holders",
            "Pulling at the central line", "Talking down, a sitter", hours, every, Guid.NewGuid(), Guid.NewGuid(), "Dr. Rao", Now).Value;

    [Fact]
    public void A_transfer_moves_an_active_restraint_to_the_receiving_ward()
    {
        var wardA = Guid.NewGuid();
        var icu = Guid.NewGuid();
        var episode = Order(wardA);

        episode.MoveToWard(icu).ShouldBeTrue();
        episode.WardId.ShouldBe(icu);
        episode.MoveToWard(icu).ShouldBeFalse();
    }

    [Fact]
    public void A_released_restraint_stays_where_it_was_made()
    {
        var wardA = Guid.NewGuid();
        var episode = Order(wardA);
        episode.Release("Calm", null, null, Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();

        episode.MoveToWard(Guid.NewGuid()).ShouldBeFalse();
        episode.WardId.ShouldBe(wardA);
    }

    [Theory]
    [InlineData(DischargeType.Normal)]
    [InlineData(DischargeType.Referred)]
    [InlineData(DischargeType.Lama)]
    [InlineData(DischargeType.Dama)]
    [InlineData(DischargeType.Absconded)]
    public void A_discharge_is_refused_while_a_restraint_is_active(DischargeType type)
    {
        var refused = RestraintRules.DischargeHold(type, hasActiveRestraint: true);
        refused.ShouldNotBeNull();
        refused.Code.ShouldBe("discharge.restraint_active");
        refused.Message.ShouldContain("Release the restraint first");
        RestraintRules.DischargeHold(type, hasActiveRestraint: false).ShouldBeNull();
    }

    [Fact]
    public void An_expired_discharge_is_not_refused_and_ends_the_restraint_with_a_reason()
    {
        RestraintRules.DischargeHold(DischargeType.Expired, hasActiveRestraint: true).ShouldBeNull();

        var episode = Order();
        episode.EndForDeath(Guid.NewGuid(), "Nurse Devi", Now.AddMinutes(20)).IsSuccess.ShouldBeTrue();
        (episode.IsActive, episode.ReleasedAt, episode.ReleaseReason).ShouldBe((false, Now.AddMinutes(20), RestraintRules.ExpiredReason));
    }

    [Fact]
    public void An_overdue_check_is_alerted_once_until_the_next_check_goes_overdue()
    {
        var episode = Order();
        episode.IsCheckAlertDue(Now.AddMinutes(15)).ShouldBeFalse();
        episode.IsCheckAlertDue(Now.AddMinutes(16)).ShouldBeTrue();

        episode.MarkCheckAlerted();
        episode.IsCheckAlertDue(Now.AddMinutes(30)).ShouldBeFalse();

        // A check made late moves the due time on: its own overdue period raises a new alert.
        episode.RecordCheck(Now.AddMinutes(31), Now.AddMinutes(31)).IsSuccess.ShouldBeTrue();
        episode.IsCheckAlertDue(Now.AddMinutes(46)).ShouldBeFalse();
        episode.IsCheckAlertDue(Now.AddMinutes(47)).ShouldBeTrue();
    }

    [Fact]
    public void A_lapsed_order_is_alerted_once_and_again_only_after_a_renewal_lapses()
    {
        var episode = Order(hours: 1);
        episode.IsLapseAlertDue(Now.AddHours(1)).ShouldBeFalse();
        episode.IsLapseAlertDue(Now.AddMinutes(61)).ShouldBeTrue();

        episode.MarkLapseAlerted();
        episode.IsLapseAlertDue(Now.AddMinutes(90)).ShouldBeFalse();

        episode.Renew(1, "Still agitated on review", Guid.NewGuid(), "Dr. Rao", Now.AddMinutes(95)).IsSuccess.ShouldBeTrue();
        episode.IsLapseAlertDue(Now.AddMinutes(100)).ShouldBeFalse();
        episode.IsLapseAlertDue(Now.AddMinutes(156)).ShouldBeTrue();
    }

    [Fact]
    public void A_released_restraint_raises_no_alert()
    {
        var episode = Order();
        episode.Release("Calm", null, null, Now.AddMinutes(5)).IsSuccess.ShouldBeTrue();
        (episode.IsCheckAlertDue(Now.AddHours(5)), episode.IsLapseAlertDue(Now.AddHours(5))).ShouldBe((false, false));
    }

    [Fact]
    public void An_admission_is_not_cancelled_while_a_restraint_is_active()
    {
        var refused = RestraintRules.CancelHold(hasActiveRestraint: true);
        refused.ShouldNotBeNull();
        refused.Code.ShouldBe("admission.restraint_active");
        refused.Message.ShouldContain("Release the restraint first");
        RestraintRules.CancelHold(hasActiveRestraint: false).ShouldBeNull();
    }

    [Fact]
    public void A_death_records_the_requester_as_the_one_who_released_it()
    {
        var episode = Order();
        var requester = Guid.NewGuid();

        episode.EndForDeath(requester, "Dr. Requester", Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        (episode.ReleasedByUserId, episode.ReleasedByName).ShouldBe((requester, "Dr. Requester"));
    }
}
