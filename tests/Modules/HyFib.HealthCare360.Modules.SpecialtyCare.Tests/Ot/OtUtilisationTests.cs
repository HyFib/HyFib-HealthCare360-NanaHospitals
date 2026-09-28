using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

public sealed class OtUtilisationTests
{
    private static readonly Guid Theatre = Guid.CreateVersion7();
    private static readonly DateTimeOffset Nine = TestData.Now.AddHours(-1);

    private static OtCaseTiming Case(int startMinute, int bookedMinutes, int? inMinute, int? outMinute, SurgeryStatus status = SurgeryStatus.Completed, int day = 0) =>
        new(Theatre, TestData.Today.AddDays(day), status, Nine.AddDays(day).AddMinutes(startMinute), Nine.AddDays(day).AddMinutes(startMinute + bookedMinutes),
            inMinute is { } i ? Nine.AddDays(day).AddMinutes(i) : null, outMinute is { } o ? Nine.AddDays(day).AddMinutes(o) : null);

    [Fact]
    public void Booked_and_actual_minutes_are_compared_for_completed_cases()
    {
        var cases = new[] { Case(0, 120, 5, 155), Case(180, 60, 170, 230), Case(300, 60, null, null, SurgeryStatus.Cancelled) };

        var result = OtUtilisation.Calculate(Theatre, cases, 480);

        result.CasesCompleted.ShouldBe(2);
        result.CasesCancelled.ShouldBe(1);
        result.BookedMinutes.ShouldBe(180);
        result.ActualMinutes.ShouldBe(150 + 60);
        result.ActualToBookedPercent.ShouldBe(116.7);
        result.UtilisationPercent.ShouldBe(43.8);
    }

    [Fact]
    public void First_case_is_on_time_within_fifteen_minutes()
    {
        var onTime = Case(0, 60, 15, 60);
        var late = Case(0, 60, 16, 70, day: 1);

        var result = OtUtilisation.Calculate(Theatre, [onTime, late], 960);

        result.FirstCases.ShouldBe(2);
        result.FirstCasesOnTime.ShouldBe(1);
        result.FirstCaseOnTimePercent.ShouldBe(50);
    }

    [Fact]
    public void Only_the_earliest_scheduled_case_of_the_day_counts_as_first_case()
    {
        var result = OtUtilisation.Calculate(Theatre, [Case(0, 60, 0, 60), Case(90, 60, 200, 260)], 480);

        result.FirstCases.ShouldBe(1);
        result.FirstCasesOnTime.ShouldBe(1);
    }

    [Fact]
    public void Turnover_is_the_gap_between_consecutive_cases_and_ignores_idle_time()
    {
        var cases = new[]
        {
            Case(0, 60, 0, 60),
            Case(60, 60, 90, 150),   // 30 min turnover
            Case(150, 60, 170, 230), // 20 min turnover
            Case(500, 60, 500, 560), // 270 min gap: idle time, excluded
        };

        OtUtilisation.Calculate(Theatre, cases, 720).AverageTurnoverMinutes.ShouldBe(25);
    }

    [Fact]
    public void Other_theatres_are_ignored_and_empty_theatres_report_nulls()
    {
        var other = Case(0, 60, 0, 60) with { TheatreId = Guid.CreateVersion7() };

        var result = OtUtilisation.Calculate(Theatre, [other], 480);

        result.CasesCompleted.ShouldBe(0);
        result.ActualToBookedPercent.ShouldBeNull();
        result.FirstCaseOnTimePercent.ShouldBeNull();
        result.AverageTurnoverMinutes.ShouldBeNull();
        result.UtilisationPercent.ShouldBe(0);
    }
}
