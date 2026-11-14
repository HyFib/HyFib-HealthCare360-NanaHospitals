using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// All facilities is a grant of its own, and an empty list of facilities is none (<see cref="FacilityGrant"/>): taking a
/// user's facilities away only narrows what they reach, at once, and giving them every facility waits for a second
/// administrator like any widening.
/// </summary>
public sealed class FacilityGrantTests
{
    private static readonly Guid Main = Guid.Parse("0192a000-0000-7000-8000-0000000000f1");
    private static readonly Guid Annexe = Guid.Parse("0192a000-0000-7000-8000-0000000000f2");

    [Fact]
    public void Taking_away_a_users_last_facility_leaves_them_none_at_once_and_never_every_facility()
    {
        var (allNow, now, add, addAll) = FacilityGrant.Split(allBefore: false, [Main], all: false, [], pending: null);

        allNow.ShouldBeFalse();
        now.ShouldBeEmpty();
        add.ShouldBeEmpty();
        addAll.ShouldBeFalse();
    }

    [Fact]
    public void All_facilities_is_asked_for_and_the_user_keeps_their_own_until_it_is_approved()
    {
        var (allNow, now, add, addAll) = FacilityGrant.Split(allBefore: false, [Main], all: true, [], pending: null);

        addAll.ShouldBeTrue();
        allNow.ShouldBeFalse();
        now.ShouldBe([Main]);
        add.ShouldBeEmpty();
    }

    [Fact]
    public void All_facilities_given_or_already_waiting_is_not_asked_for_again()
    {
        var given = FacilityGrant.Split(allBefore: true, [], all: true, [], pending: null);
        (given.AllNow, given.AddAll).ShouldBe((true, false));
        given.Now.ShouldBeEmpty();

        var waiting = FacilityGrant.Split(allBefore: false, [Main], all: true, [], Pending(all: true));
        (waiting.AllNow, waiting.AddAll).ShouldBe((false, false));
        waiting.Now.ShouldBe([Main]);
    }

    [Fact]
    public void Taking_all_facilities_back_to_some_keeps_those_at_once_and_asks_for_nothing()
    {
        var (allNow, now, add, addAll) = FacilityGrant.Split(allBefore: true, [], all: false, [Main, Annexe], pending: null);

        allNow.ShouldBeFalse();
        now.ShouldBe([Main, Annexe]);
        add.ShouldBeEmpty();
        addAll.ShouldBeFalse();
    }

    [Fact]
    public void A_facility_added_waits_unless_it_waits_already()
    {
        var (_, now, add, _) = FacilityGrant.Split(allBefore: false, [Main], all: false, [Main, Annexe], pending: null);
        now.ShouldBe([Main]);
        add.ShouldBe([Annexe]);

        FacilityGrant.Split(allBefore: false, [Main], all: false, [Main, Annexe], Pending(all: false, Annexe)).Add.ShouldBeEmpty();
    }

    private static PendingUserGrant Pending(bool all, params Guid[] facilities) =>
        new(Guid.NewGuid(), [], facilities, Activate: false, "Hospital Admin", DateTimeOffset.UnixEpoch, all);
}
