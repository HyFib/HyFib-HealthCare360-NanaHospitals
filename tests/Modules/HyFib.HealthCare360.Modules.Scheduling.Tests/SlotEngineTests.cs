using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Scheduling.Tests.TestSchedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

public sealed class SlotEngineTests
{
    [Fact]
    public void Fixture_date_is_a_monday() => HospitalCalendar.WeekdayOf(Monday).ShouldBe(Weekday.Monday);

    [Fact]
    public void Wall_clock_times_are_converted_from_ist_to_utc()
    {
        Calendar.ToUtc(Monday, new TimeOnly(9, 0)).ShouldBe(new DateTimeOffset(2026, 9, 28, 3, 30, 0, TimeSpan.Zero));
        Calendar.DateOf(new DateTimeOffset(2026, 9, 28, 20, 0, 0, TimeSpan.Zero)).ShouldBe(Tuesday); // 01:30 IST next day
    }

    [Fact]
    public void Weekly_session_produces_whole_slots_in_ist()
    {
        var slots = Compute([MondayMorning()], Query(Monday));

        slots.Count.ShouldBe(16);
        slots[0].LocalStart.ShouldBe(new TimeOnly(9, 0));
        slots[0].StartsAt.ShouldBe(Ist(Monday, 9, 0));
        slots[0].EndsAt.ShouldBe(Ist(Monday, 9, 15));
        slots[^1].LocalStart.ShouldBe(new TimeOnly(12, 45));
        slots[^1].LocalEnd.ShouldBe(new TimeOnly(13, 0));
        slots.ShouldAllBe(s => s.FacilityId == Facility && s.DepartmentId == Department && s.Room == "OPD-01" && s.Available == 1);
    }

    [Fact]
    public void Trailing_time_shorter_than_a_slot_is_not_bookable()
    {
        var slots = Compute([Session(new TimeOnly(9, 0), new TimeOnly(10, 10), slotMinutes: 20)], Query(Monday));

        slots.Select(s => s.LocalStart).ShouldBe([new TimeOnly(9, 0), new TimeOnly(9, 20), new TimeOnly(9, 40)]);
    }

    [Fact]
    public void Sessions_apply_only_on_their_weekday_within_the_effective_dates()
    {
        var schedule = MondayMorning();
        schedule.EffectiveFrom = Monday.AddDays(7);

        Compute([schedule], Query(Monday)).ShouldBeEmpty();
        Compute([MondayMorning()], Query(Tuesday)).ShouldBeEmpty();
        Compute([schedule], Query(Monday.AddDays(7))).Count.ShouldBe(16);
    }

    [Fact]
    public void Inactive_sessions_produce_no_slots()
    {
        var schedule = MondayMorning();
        schedule.IsActive = false;

        Compute([schedule], Query(Monday)).ShouldBeEmpty();
    }

    [Fact]
    public void All_day_leave_removes_every_slot_of_those_days()
    {
        var leave = Absence(ScheduleExceptionType.Leave, Monday, Tuesday);

        Compute([MondayMorning()], Query(Monday), [leave]).ShouldBeEmpty();
        Compute([MondayMorning()], Query(Monday.AddDays(7)), [leave]).Count.ShouldBe(16);
    }

    [Fact]
    public void A_block_removes_only_the_slots_it_overlaps()
    {
        var block = Absence(ScheduleExceptionType.Block, Monday, Monday, new TimeOnly(10, 0), new TimeOnly(11, 0));

        var slots = Compute([MondayMorning()], Query(Monday), [block]);

        slots.Count.ShouldBe(12);
        slots.ShouldNotContain(s => s.LocalStart >= new TimeOnly(10, 0) && s.LocalStart < new TimeOnly(11, 0));
    }

    [Fact]
    public void A_block_ending_mid_slot_still_removes_that_slot()
    {
        var block = Absence(ScheduleExceptionType.Block, Monday, Monday, new TimeOnly(9, 0), new TimeOnly(9, 20));

        Compute([MondayMorning()], Query(Monday), [block])[0].LocalStart.ShouldBe(new TimeOnly(9, 30));
    }

    [Fact]
    public void Leave_at_another_branch_does_not_remove_slots_here()
    {
        var leave = Absence(ScheduleExceptionType.Leave, Monday, Monday, facility: OtherFacility);

        Compute([MondayMorning()], Query(Monday), [leave]).Count.ShouldBe(16);
    }

    [Fact]
    public void Extra_session_adds_slots_on_its_dates_only()
    {
        var camp = ExtraSession(Sunday, new TimeOnly(10, 0), new TimeOnly(12, 0));

        var slots = Compute([MondayMorning()], Query(Monday, Sunday), [camp]);

        slots.Count(s => s.Date == Sunday).ShouldBe(4);
        slots.Where(s => s.Date == Sunday).ShouldAllBe(s => s.Mode == ConsultationMode.Both && s.ScheduleId == null);
        slots.Count(s => s.Date == Monday).ShouldBe(16);
    }

    [Fact]
    public void Full_slots_are_hidden_unless_requested()
    {
        var busy = new[] { new BusyInterval(Ist(Monday, 9, 0), Ist(Monday, 9, 15)) };

        var open = Compute([MondayMorning()], Query(Monday), busy: busy);
        var all = Compute([MondayMorning()], Query(Monday) with { IncludeFull = true }, busy: busy);

        open.Count.ShouldBe(15);
        open[0].LocalStart.ShouldBe(new TimeOnly(9, 15));
        all.Count.ShouldBe(16);
        all[0].Booked.ShouldBe(1);
        all[0].IsFull.ShouldBeTrue();
        all[0].Available.ShouldBe(0);
    }

    [Fact]
    public void Capacity_allows_several_patients_per_slot()
    {
        var one = new[] { new BusyInterval(Ist(Monday, 9, 0), Ist(Monday, 9, 15)) };
        var two = one.Append(new BusyInterval(Ist(Monday, 9, 0), Ist(Monday, 9, 15))).ToArray();

        Compute([MondayMorning(capacity: 2)], Query(Monday), busy: one)[0].Available.ShouldBe(1);
        Compute([MondayMorning(capacity: 2)], Query(Monday), busy: two)[0].LocalStart.ShouldBe(new TimeOnly(9, 15));
    }

    [Fact]
    public void A_longer_booking_occupies_every_slot_it_overlaps()
    {
        var busy = new[] { new BusyInterval(Ist(Monday, 9, 0), Ist(Monday, 9, 30)) };

        Compute([MondayMorning()], Query(Monday), busy: busy)[0].LocalStart.ShouldBe(new TimeOnly(9, 30));
    }

    [Fact]
    public void Past_slots_are_never_offered()
    {
        var query = new SlotQuery(Monday, Monday, Ist(Monday, 10, 5));

        Compute([MondayMorning()], query)[0].LocalStart.ShouldBe(new TimeOnly(10, 15));
    }

    [Fact]
    public void Online_cutoff_hides_slots_that_start_too_soon()
    {
        var now = Ist(Monday, 9, 50);
        var query = new SlotQuery(Monday, Monday, now + TimeSpan.FromMinutes(30));

        Compute([MondayMorning()], query)[0].LocalStart.ShouldBe(new TimeOnly(10, 30));
    }

    [Theory]
    [InlineData(ConsultationMode.InPerson, ConsultationMode.InPerson, 16)]
    [InlineData(ConsultationMode.InPerson, ConsultationMode.Telehealth, 0)]
    [InlineData(ConsultationMode.Telehealth, ConsultationMode.InPerson, 0)]
    [InlineData(ConsultationMode.Both, ConsultationMode.Telehealth, 16)]
    [InlineData(ConsultationMode.Both, ConsultationMode.InPerson, 16)]
    public void Mode_filter_matches_session_modes(ConsultationMode offered, ConsultationMode requested, int expected)
    {
        var slots = Compute([MondayMorning(mode: offered)], Query(Monday) with { Mode = requested });

        slots.Count.ShouldBe(expected);
    }

    [Fact]
    public void Facility_filter_keeps_only_that_branch()
    {
        var here = MondayMorning();
        var there = Session(new TimeOnly(17, 0), new TimeOnly(19, 0), facility: OtherFacility);

        var slots = Compute([here, there], Query(Monday) with { FacilityId = OtherFacility });

        slots.Count.ShouldBe(8);
        slots.ShouldAllBe(s => s.FacilityId == OtherFacility);
    }

    [Fact]
    public void Slots_are_ordered_and_never_duplicated()
    {
        var evening = Session(new TimeOnly(17, 0), new TimeOnly(19, 0));
        var morning = MondayMorning();
        var duplicate = MondayMorning();

        var slots = Compute([evening, morning, duplicate], Query(Monday));

        slots.Count.ShouldBe(24);
        slots.Select(s => s.StartsAt).ShouldBe(slots.Select(s => s.StartsAt).Order().ToList());
        slots.Select(s => s.StartsAt).Distinct().Count().ShouldBe(24);
    }

    [Fact]
    public void Range_is_capped_to_protect_the_server()
    {
        var everyDay = Enum.GetValues<Weekday>().Select(day => Session(new TimeOnly(9, 0), new TimeOnly(10, 0), day, slotMinutes: 60)).ToList();

        var slots = Compute(everyDay, Query(Monday, Monday.AddDays(400)));

        slots.Count.ShouldBe(SlotEngine.MaxRangeDays);
    }

    [Fact]
    public void Reversed_range_returns_nothing() => Compute([MondayMorning()], Query(Tuesday, Monday)).ShouldBeEmpty();
}
