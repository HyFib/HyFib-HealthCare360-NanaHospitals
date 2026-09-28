using System.Globalization;
using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class BedDayCalculatorTests
{
    private static readonly Guid General = Guid.NewGuid();
    private static readonly Guid Icu = Guid.NewGuid();
    private static readonly Guid GeneralWard = Guid.NewGuid();
    private static readonly Guid IcuWard = Guid.NewGuid();

    private static DateTimeOffset At(int day, int hour, int minute = 0) => IndiaClock.AtLocal(new DateOnly(2026, 9, day), new TimeOnly(hour, minute));

    private static DateOnly Day(int day) => new(2026, 9, day);

    private static IReadOnlyList<BedStay> InGeneral(DateTimeOffset from) => [new BedStay(from, null, General, GeneralWard)];

    [Fact]
    public void Midnight_census_charges_each_midnight_spent_in_a_bed()
    {
        var days = BedDayCalculator.DueDays(At(20, 10), null, InGeneral(At(20, 10)), At(23, 9), BedDayPolicy.MidnightCensus);

        days.Select(d => d.Date).ShouldBe([Day(20), Day(21), Day(22)]);
        days.ShouldAllBe(d => d.BedServiceItemId == General);
    }

    [Fact]
    public void Midnight_census_does_not_charge_the_current_day_before_midnight() =>
        BedDayCalculator.DueDays(At(20, 10), null, InGeneral(At(20, 10)), At(20, 23, 59), BedDayPolicy.MidnightCensus).ShouldBeEmpty();

    [Fact]
    public void A_stay_that_crosses_no_midnight_is_charged_one_day_once_it_ends()
    {
        var days = BedDayCalculator.DueDays(At(20, 10), At(20, 18), InGeneral(At(20, 10)), At(20, 19), BedDayPolicy.MidnightCensus);

        days.Select(d => d.Date).ShouldBe([Day(20)]);
    }

    [Fact]
    public void Discharge_the_next_morning_charges_the_night_only()
    {
        var days = BedDayCalculator.DueDays(At(20, 10), At(21, 9), InGeneral(At(20, 10)), At(21, 10), BedDayPolicy.MidnightCensus);

        days.Select(d => d.Date).ShouldBe([Day(20)]);
    }

    [Fact]
    public void The_bed_occupied_at_midnight_decides_the_bed_class()
    {
        IReadOnlyList<BedStay> stays =
        [
            new(At(20, 10), At(21, 20), General, GeneralWard),
            new(At(21, 20), null, Icu, IcuWard),
        ];

        var days = BedDayCalculator.DueDays(At(20, 10), null, stays, At(23, 1), BedDayPolicy.MidnightCensus);

        days.Select(d => (d.Date, d.BedServiceItemId)).ShouldBe([(Day(20), (Guid?)General), (Day(21), Icu), (Day(22), Icu)]);
        days.Last().WardId.ShouldBe(IcuWard);
    }

    [Fact]
    public void A_transfer_exactly_at_midnight_charges_the_old_bed_for_the_night_before()
    {
        IReadOnlyList<BedStay> stays =
        [
            new(At(20, 10), At(21, 0), General, GeneralWard),
            new(At(21, 0), null, Icu, IcuWard),
        ];

        var days = BedDayCalculator.DueDays(At(20, 10), null, stays, At(22, 1), BedDayPolicy.MidnightCensus);

        days.Select(d => d.BedServiceItemId).ShouldBe([General, Icu]);
    }

    [Fact]
    public void Calendar_day_policy_charges_every_day_touched_by_the_stay()
    {
        var days = BedDayCalculator.DueDays(At(20, 10), At(22, 9), InGeneral(At(20, 10)), At(22, 10), BedDayPolicy.CalendarDay);

        days.Select(d => d.Date).ShouldBe([Day(20), Day(21), Day(22)]);
    }

    [Fact]
    public void Calendar_day_policy_charges_today_for_a_running_stay() =>
        BedDayCalculator.DueDays(At(20, 10), null, InGeneral(At(20, 10)), At(20, 11), BedDayPolicy.CalendarDay)
            .Select(d => d.Date).ShouldBe([Day(20)]);

    [Fact]
    public void Nothing_is_due_before_the_admission() =>
        BedDayCalculator.DueDays(At(20, 10), null, InGeneral(At(20, 10)), At(20, 9), BedDayPolicy.CalendarDay).ShouldBeEmpty();

    [Fact]
    public void A_ward_without_a_bed_tariff_yields_days_without_a_service_item()
    {
        IReadOnlyList<BedStay> stays = [new(At(20, 10), null, null, GeneralWard)];

        var days = BedDayCalculator.DueDays(At(20, 10), null, stays, At(22, 1), BedDayPolicy.MidnightCensus);

        days.Count.ShouldBe(2);
        days.ShouldAllBe(d => d.BedServiceItemId == null);
    }

    [Fact]
    public void Daily_charge_keys_are_admission_and_date()
    {
        var admissionId = Guid.Parse("0199a7d2-3c4f-7a10-9d2e-6b1f0c8e4a55");

        DailyChargeKeys.For(admissionId, new DateOnly(2026, 9, 5)).ShouldBe("0199a7d2-3c4f-7a10-9d2e-6b1f0c8e4a55:2026-09-05");
    }

    [Fact]
    public void Daily_charge_keys_do_not_depend_on_the_server_culture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            DailyChargeKeys.For(Guid.Empty, new DateOnly(2026, 9, 5)).ShouldEndWith(":2026-09-05");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Bed_and_nursing_charges_of_a_day_use_different_sources()
    {
        DailyChargeKeys.SourceFor(DailyChargeKind.Bed).ShouldBe("inpatient.bed");
        DailyChargeKeys.SourceFor(DailyChargeKind.Nursing).ShouldBe("inpatient.nursing");
        DailyChargeKeys.ConsultantVisitSource.ShouldBe("inpatient.visit");
    }
}
