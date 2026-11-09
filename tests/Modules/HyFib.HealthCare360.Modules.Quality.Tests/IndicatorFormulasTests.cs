using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Tests;

/// <summary>Worked examples of the computed NABH indicators' arithmetic.</summary>
public sealed class IndicatorFormulasTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 1, 4, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = IndicatorFormulas.ReturnWindow;

    private static DateTimeOffset H(double hours) => T0.AddHours(hours);

    [Fact]
    public void Average_wait_is_total_minutes_over_the_records_timed_and_a_backwards_record_is_left_out()
    {
        // Three ER patients seen after 6, 12 and 15 minutes; a fourth recorded as seen before arrival is a recording error.
        var (total, count) = IndicatorFormulas.Waits(
        [
            (T0, T0.AddMinutes(6)),
            (T0, T0.AddMinutes(12)),
            (T0, T0.AddMinutes(15)),
            (T0, T0.AddMinutes(-3)),
        ]);

        total.ShouldBe(33m);
        count.ShouldBe(3);
        IndicatorMath.Compute(total, count, 1).ShouldBe(11m); // ER-IA-TIME = 11 minutes
    }

    [Theory]
    [InlineData(10, 15, 1, 30, 5)] // admitted on the 10th, discharged on the 15th: midnights of the 10th..14th
    [InlineData(10, 10, 1, 30, 0)] // a same-day stay gives no census day
    [InlineData(25, null, 1, 30, 6)] // still in at the end of September: 25th..30th
    [InlineData(1, 40, 10, 19, 10)] // a long stay counts only the period's days
    public void Patient_days_follow_the_midnight_census(int admitted, int? discharged, int from, int to, int expected) =>
        IndicatorFormulas.CensusDays(admitted, discharged, from, to).ShouldBe(expected);

    [Fact]
    public void Medication_errors_per_thousand_patient_days_worked_example()
    {
        // 30 beds full all of September = 900 patient days; 3 medication errors → 3.33 per 1,000 patient days.
        var days = Enumerable.Range(0, 30).Sum(_ => IndicatorFormulas.CensusDays(0, null, 1, 30));
        days.ShouldBe(900);
        IndicatorMath.Compute(3, days, 1000).ShouldBe(3.33m);
    }

    [Theory]
    [InlineData(3, 7, 4)]
    [InlineData(3, 3, 1)]
    public void Length_of_stay_counts_a_same_day_stay_as_one(int admitted, int discharged, int expected) =>
        IndicatorFormulas.StayDays(admitted, discharged).ShouldBe(expected);

    [Fact]
    public void A_return_to_icu_within_48_hours_counts_against_the_transfer_out()
    {
        // ICU 0–24 h, ward 24–54 h, back in ICU at 54 h: out at 24 h, back after 30 h.
        var transfers = IndicatorFormulas.IcuTransfersOut(
        [
            new CareSegment(true, H(0), H(24)),
            new CareSegment(false, H(24), H(54)),
            new CareSegment(true, H(54), null),
        ], Window);

        transfers.Count.ShouldBe(1);
        transfers[0].At.ShouldBe(H(24));
        transfers[0].Returned.ShouldBeTrue();
        transfers[0].ReturnedAt.ShouldBe(H(54));
    }

    [Fact]
    public void A_return_after_48_hours_is_not_a_return()
    {
        var transfers = IndicatorFormulas.IcuTransfersOut(
        [
            new CareSegment(true, H(0), H(24)),
            new CareSegment(false, H(24), H(73)),
            new CareSegment(true, H(73), H(90)),
            new CareSegment(false, H(90), null),
        ], Window);

        transfers.Count.ShouldBe(2);
        transfers[0].Returned.ShouldBeFalse(); // 49 hours later
        transfers[1].Returned.ShouldBeFalse(); // never came back
    }

    [Fact]
    public void A_move_between_icu_beds_is_not_a_transfer_out_and_leaving_hospital_from_icu_is_not_one_either()
    {
        var transfers = IndicatorFormulas.IcuTransfersOut(
        [
            new CareSegment(true, H(0), H(10)),
            new CareSegment(true, H(10), H(30)), // another ICU bed
            new CareSegment(false, H(30), H(40)),
            new CareSegment(true, H(40), H(60)), // discharged or died from ICU: the stay ends here
        ], Window);

        transfers.Count.ShouldBe(1);
        transfers[0].At.ShouldBe(H(30));
        transfers[0].Returned.ShouldBeTrue();
    }

    [Fact]
    public void Return_to_icu_rate_worked_example()
    {
        // 2 of 40 transfers out came back within 48 hours → 5 %.
        IndicatorMath.Compute(2, 40, 100).ShouldBe(5m);
    }

    [Fact]
    public void An_unplanned_return_is_a_non_elective_operation_within_48_hours_of_the_previous_one_in_the_stay()
    {
        var patient = Guid.NewGuid();
        var stay = Guid.NewGuid();
        var first = new Operation(Guid.NewGuid(), patient, stay, true, H(0), H(2));
        var reExploration = new Operation(Guid.NewGuid(), patient, stay, false, H(22), H(23)); // emergency, 20 h after
        var stagedElective = new Operation(Guid.NewGuid(), patient, stay, true, H(40), H(41)); // planned second stage
        var late = new Operation(Guid.NewGuid(), patient, stay, false, H(100), H(101)); // 59 h after the previous

        var returns = IndicatorFormulas.UnplannedReturns([first, reExploration, stagedElective, late], Window);

        returns.ShouldBe([reExploration.Id], ignoreOrder: true);
    }

    [Fact]
    public void Operations_in_different_stays_are_not_returns()
    {
        var patient = Guid.NewGuid();
        var returns = IndicatorFormulas.UnplannedReturns(
        [
            new Operation(Guid.NewGuid(), patient, Guid.NewGuid(), true, H(0), H(2)),
            new Operation(Guid.NewGuid(), patient, Guid.NewGuid(), false, H(10), H(11)),
        ], Window);

        returns.ShouldBeEmpty();
    }

    [Fact]
    public void Reintubation_within_48_hours_of_an_extubation()
    {
        var stay = Guid.NewGuid();
        var spells = new[]
        {
            new VentilationSpell(Guid.NewGuid(), stay, H(0), H(72), EndedByExtubation: true),
            new VentilationSpell(Guid.NewGuid(), stay, H(108), H(200), EndedByExtubation: true), // 36 h after
            new VentilationSpell(Guid.NewGuid(), stay, H(249), H(300), EndedByExtubation: false), // 49 h after; then died on the ventilator
        };

        var extubations = IndicatorFormulas.Extubations(spells, Window);

        extubations.Count.ShouldBe(2);
        extubations[0].Reintubated.ShouldBeTrue();
        extubations[0].ReintubatedAt.ShouldBe(H(108));
        extubations[1].Reintubated.ShouldBeFalse();
        IndicatorMath.Compute(extubations.Count(e => e.Reintubated), extubations.Count, 100).ShouldBe(50m);
    }
}
