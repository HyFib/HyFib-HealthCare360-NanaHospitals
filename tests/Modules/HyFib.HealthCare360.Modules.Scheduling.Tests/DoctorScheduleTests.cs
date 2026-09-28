using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.Modules.Scheduling.Tests.TestSchedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

public sealed class DoctorScheduleTests
{
    private static readonly TimeOnly Nine = new(9, 0);
    private static readonly TimeOnly One = new(13, 0);

    [Fact]
    public void A_session_counts_its_whole_slots() => MondayMorning().SlotsPerSession.ShouldBe(16);

    [Fact]
    public void Sessions_on_the_same_day_and_time_overlap()
    {
        var existing = MondayMorning();
        var candidate = Session(new TimeOnly(12, 0), new TimeOnly(14, 0));

        candidate.Overlaps(existing).ShouldBeTrue();
    }

    [Fact]
    public void Back_to_back_sessions_do_not_overlap() =>
        Session(One, new TimeOnly(15, 0)).Overlaps(MondayMorning()).ShouldBeFalse();

    [Fact]
    public void Sessions_on_other_days_or_for_other_doctors_do_not_overlap()
    {
        Session(Nine, One, Weekday.Tuesday).Overlaps(MondayMorning()).ShouldBeFalse();

        var colleague = MondayMorning();
        colleague.PractitionerId = Guid.CreateVersion7();
        colleague.Overlaps(MondayMorning()).ShouldBeFalse();
    }

    [Fact]
    public void Sessions_valid_in_different_periods_do_not_overlap()
    {
        var old = MondayMorning();
        old.EffectiveTo = new DateOnly(2026, 6, 30);
        var current = MondayMorning();
        current.EffectiveFrom = new DateOnly(2026, 7, 1);

        current.Overlaps(old).ShouldBeFalse();
    }

    [Fact]
    public void Inactive_sessions_and_the_same_row_never_overlap()
    {
        var schedule = MondayMorning();
        var inactive = MondayMorning();
        inactive.IsActive = false;

        schedule.Overlaps(schedule).ShouldBeFalse();
        schedule.Overlaps(inactive).ShouldBeFalse();
    }

    [Theory]
    [InlineData(9, 0, 13, 0, 15, 1, true)]
    [InlineData(9, 0, 9, 0, 15, 1, false)]
    [InlineData(10, 0, 9, 0, 15, 1, false)]
    [InlineData(9, 0, 9, 10, 15, 1, false)]
    [InlineData(9, 0, 13, 0, 3, 1, false)]
    [InlineData(9, 0, 13, 0, 180, 1, false)]
    [InlineData(9, 0, 13, 0, 15, 0, false)]
    [InlineData(9, 0, 13, 0, 15, 25, false)]
    public void Session_shape_is_validated(int startHour, int startMinute, int endHour, int endMinute, int slotMinutes, int capacity, bool valid) =>
        DoctorSchedule.Validate(new TimeOnly(startHour, startMinute), new TimeOnly(endHour, endMinute), slotMinutes, capacity, Monday, null)
            .IsSuccess.ShouldBe(valid);

    [Fact]
    public void Validation_errors_name_the_field_to_fix()
    {
        var result = DoctorSchedule.Validate(Nine, One, 15, 1, Monday, Monday.AddDays(-1));

        result.Error!.Fields!.ShouldContainKey("effectiveTo");
    }

    [Fact]
    public void Extra_sessions_need_times_and_a_department()
    {
        ScheduleException.Validate(ScheduleExceptionType.ExtraSession, Sunday, Sunday, null, null, Department, 15, 1)
            .Error!.Fields!.ShouldContainKey("startTime");
        ScheduleException.Validate(ScheduleExceptionType.ExtraSession, Sunday, Sunday, Nine, One, null, 15, 1)
            .Error!.Fields!.ShouldContainKey("departmentId");
        ScheduleException.Validate(ScheduleExceptionType.ExtraSession, Sunday, Sunday, Nine, One, Department, 15, 1)
            .IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Leave_may_cover_whole_days_but_not_half_a_time_window()
    {
        ScheduleException.Validate(ScheduleExceptionType.Leave, Monday, Tuesday, null, null, null, null, null).IsSuccess.ShouldBeTrue();
        ScheduleException.Validate(ScheduleExceptionType.Block, Monday, Monday, Nine, null, null, null, null).IsFailure.ShouldBeTrue();
        ScheduleException.Validate(ScheduleExceptionType.Leave, Tuesday, Monday, null, null, null, null, null).IsFailure.ShouldBeTrue();
        ScheduleException.Validate(ScheduleExceptionType.Leave, Monday, Monday.AddDays(400), null, null, null, null, null).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_block_applies_to_slots_it_touches_on_covered_dates_only()
    {
        var block = Absence(ScheduleExceptionType.Block, Monday, Monday, new TimeOnly(10, 0), new TimeOnly(11, 0));

        block.Blocks(Monday, new TimeOnly(10, 45), new TimeOnly(11, 0), Facility).ShouldBeTrue();
        block.Blocks(Monday, new TimeOnly(11, 0), new TimeOnly(11, 15), Facility).ShouldBeFalse();
        block.Blocks(Tuesday, new TimeOnly(10, 0), new TimeOnly(10, 15), Facility).ShouldBeFalse();
    }

    [Fact]
    public void An_extra_session_never_removes_slots()
    {
        var camp = ExtraSession(Monday, Nine, One);

        camp.RemovesSlots.ShouldBeFalse();
        camp.Blocks(Monday, Nine, new TimeOnly(9, 15), Facility).ShouldBeFalse();
    }
}
