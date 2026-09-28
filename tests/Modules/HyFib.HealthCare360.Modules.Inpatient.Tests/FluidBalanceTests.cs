using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class FluidBalanceTests
{
    private static readonly DateOnly DutyDate = new(2026, 9, 25);

    private static DateTimeOffset At(int day, int hour, int minute = 0) => IndiaClock.AtLocal(new DateOnly(2026, 9, day), new TimeOnly(hour, minute));

    private static FluidEntry In(DateTimeOffset at, FluidRoute route, int ml) => new(at, FluidDirection.Intake, route, ml);

    private static FluidEntry Out(DateTimeOffset at, FluidRoute route, int ml) => new(at, FluidDirection.Output, route, ml);

    [Fact]
    public void The_chart_day_runs_from_8am_to_8am_ist()
    {
        var (from, to) = FluidBalance.ChartDay(DutyDate);

        from.ShouldBe(At(25, 8));
        to.ShouldBe(At(26, 8));
        from.UtcDateTime.Hour.ShouldBe(2);
        from.UtcDateTime.Minute.ShouldBe(30);
    }

    [Fact]
    public void Balance_is_intake_minus_output_within_the_chart_day()
    {
        var (from, to) = FluidBalance.ChartDay(DutyDate);
        FluidEntry[] entries =
        [
            In(At(25, 7, 59), FluidRoute.Oral, 300),
            In(At(25, 9), FluidRoute.Oral, 500),
            In(At(25, 15), FluidRoute.IntravenousFluid, 1000),
            Out(At(25, 22), FluidRoute.Urine, 800),
            Out(At(26, 7, 59), FluidRoute.Urine, 400),
            In(At(26, 8), FluidRoute.Oral, 200),
        ];

        var balance = FluidBalance.Summarize(entries, from, to);

        balance.Total.IntakeMl.ShouldBe(1500);
        balance.Total.OutputMl.ShouldBe(1200);
        balance.Total.BalanceMl.ShouldBe(300);
    }

    [Fact]
    public void Each_shift_hands_over_its_own_balance()
    {
        var (from, to) = FluidBalance.ChartDay(DutyDate);
        FluidEntry[] entries =
        [
            In(At(25, 9), FluidRoute.Oral, 500),
            In(At(25, 15), FluidRoute.IntravenousFluid, 1000),
            Out(At(25, 16), FluidRoute.Drain, 100),
            Out(At(26, 2), FluidRoute.Urine, 1200),
        ];

        var byShift = FluidBalance.Summarize(entries, from, to).ByShift.ToDictionary(s => s.Shift);

        byShift[NursingShift.Morning].BalanceMl.ShouldBe(500);
        byShift[NursingShift.Evening].BalanceMl.ShouldBe(900);
        byShift[NursingShift.Night].BalanceMl.ShouldBe(-1200);
    }

    [Fact]
    public void Totals_are_grouped_by_route()
    {
        var (from, to) = FluidBalance.ChartDay(DutyDate);
        FluidEntry[] entries = [Out(At(25, 9), FluidRoute.Urine, 300), Out(At(25, 13), FluidRoute.Urine, 250), In(At(25, 10), FluidRoute.TubeFeed, 200)];

        var byRoute = FluidBalance.Summarize(entries, from, to).ByRoute;

        byRoute.Single(r => r.Route == FluidRoute.Urine).VolumeMl.ShouldBe(550);
        byRoute.Single(r => r.Route == FluidRoute.TubeFeed).Direction.ShouldBe(FluidDirection.Intake);
    }

    [Fact]
    public void An_empty_chart_balances_to_zero()
    {
        var (from, to) = FluidBalance.ChartDay(DutyDate);

        var balance = FluidBalance.Summarize([], from, to);

        balance.Total.BalanceMl.ShouldBe(0);
        balance.ByShift.Count.ShouldBe(3);
        balance.ByRoute.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(FluidDirection.Intake, FluidRoute.Oral, true)]
    [InlineData(FluidDirection.Intake, FluidRoute.BloodProduct, true)]
    [InlineData(FluidDirection.Output, FluidRoute.Oral, false)]
    [InlineData(FluidDirection.Output, FluidRoute.Urine, true)]
    [InlineData(FluidDirection.Intake, FluidRoute.Urine, false)]
    [InlineData(FluidDirection.Output, FluidRoute.NasogastricAspirate, true)]
    [InlineData(FluidDirection.Intake, FluidRoute.Other, true)]
    [InlineData(FluidDirection.Output, FluidRoute.Other, true)]
    public void Routes_must_match_the_direction(FluidDirection direction, FluidRoute route, bool matches) =>
        FluidBalance.RouteMatches(direction, route).ShouldBe(matches);
}
