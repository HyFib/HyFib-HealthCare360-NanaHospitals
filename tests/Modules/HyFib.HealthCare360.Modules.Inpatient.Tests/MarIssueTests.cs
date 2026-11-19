using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>
/// What the pharmacy issues supplies the line already on the chart for it (<see cref="MarRules.OrderSupplied"/>): a
/// replacement ampoule of a ward order charts no second order and no dose, and an e-prescription supplied by a ward indent
/// is charted on its own line. ChartDispenseAsync applies these rules; PharmacyIssueChartTests (integration tests) runs it
/// end to end.
/// </summary>
public sealed class MarIssueTests
{
    private static readonly Guid Furosemide = Guid.Parse("0192a000-0000-7000-8000-00000000b001");
    private static readonly Guid Ceftriaxone = Guid.Parse("0192a000-0000-7000-8000-00000000b002");

    private static DateTimeOffset At(int day, int hour, int minute = 0) => IndiaClock.AtLocal(new DateOnly(2026, 12, day), new TimeOnly(hour, minute));

    [Fact]
    public void A_replacement_ampoule_of_a_stat_order_given_already_makes_no_second_order_and_no_dose()
    {
        // A verbal "Inj. Furosemide 40 mg IV STAT" charted and given at 10:00; at 10:15 an indented ampoule is issued, with no
        // prescription line.
        var stat = Order(Furosemide, "STAT", MedicationOrderSource.Manual, At(1, 10));
        stat.Mode = MedicationOrderMode.Verbal;

        var supplied = MarRules.OrderSupplied(null, Furosemide, [stat]);

        supplied.ShouldBe(stat, "the order on the chart is the one supplied: no \"Furosemide, as directed\" line beside it");
        stat.DosesToChart(At(1, 10, 15), [], hasDoses: true).ShouldBeEmpty("a STAT order is one dose, and it was given");
    }

    [Fact]
    public void An_e_prescription_supplied_by_a_ward_indent_is_charted_on_its_own_line()
    {
        // Inj. Ceftriaxone 1 g BD for 5 days, signed at 09:50 and on the chart with no dose until the pharmacy supplies it.
        var lineId = Guid.CreateVersion7();
        var prescribed = Order(Ceftriaxone, "BD", MedicationOrderSource.Prescription, At(1, 9, 50));
        prescribed.PrescriptionLineId = lineId;
        prescribed.EndAt = At(6, 9, 50);
        prescribed.KeptChartedAhead(hasDoses: false, At(1, 10)).ShouldBeFalse("not supplied yet");

        // Issued at 10:00 against an indent raised for the prescription (the issue names its line), or with no line at all.
        MarRules.OrderSupplied(lineId, Ceftriaxone, [prescribed]).ShouldBe(prescribed);
        MarRules.OrderSupplied(null, Ceftriaxone, [prescribed]).ShouldBe(prescribed);

        prescribed.LastDispensedAt = At(1, 10);
        prescribed.DosesToChart(At(1, 10), []).ShouldBe([At(1, 21), At(2, 9)]);
        prescribed.KeptChartedAhead(hasDoses: true, At(2, 10)).ShouldBeTrue("kept a day ahead from then");
    }

    [Fact]
    public void An_issue_with_no_line_supplies_an_order_with_a_dose_before_the_pharmacys_own_and_none_that_has_stopped()
    {
        var pharmacys = Order(Ceftriaxone, "As directed", MedicationOrderSource.Pharmacy, At(1, 11));
        var wards = Order(Ceftriaxone, "BD", MedicationOrderSource.Manual, At(1, 8));

        MarRules.OrderSupplied(null, Ceftriaxone, [pharmacys, wards]).ShouldBe(wards);
        MarRules.OrderSupplied(null, Furosemide, [pharmacys, wards]).ShouldBeNull("the chart has no line of furosemide: the pharmacy's own is made");

        wards.Stop(Guid.NewGuid(), "Changed to oral", At(1, 12)).IsSuccess.ShouldBeTrue();
        MarRules.OrderSupplied(null, Ceftriaxone, [pharmacys, wards]).ShouldBe(pharmacys);
        pharmacys.Stop(null, "Patient discharged", At(1, 13)).IsSuccess.ShouldBeTrue();
        MarRules.OrderSupplied(null, Ceftriaxone, [pharmacys, wards]).ShouldBeNull();
    }

    [Fact]
    public void The_pharmacys_own_line_has_no_round_and_is_never_charted_ahead()
    {
        var pharmacys = Order(Ceftriaxone, "As directed", MedicationOrderSource.Pharmacy, At(1, 11));
        pharmacys.LastDispensedAt = At(1, 11);

        pharmacys.DosesToChart(At(1, 11), []).ShouldBeEmpty();
        pharmacys.KeptChartedAhead(hasDoses: false, At(2, 11)).ShouldBeFalse();
    }

    private static MedicationOrder Order(Guid drugItemId, string frequency, MedicationOrderSource source, DateTimeOffset start) => new()
    {
        DrugItemId = drugItemId,
        DrugName = drugItemId == Furosemide ? "Inj. Furosemide" : "Inj. Ceftriaxone",
        Dose = drugItemId == Furosemide ? "40 mg" : "1 g",
        Route = "IV",
        Frequency = frequency,
        StartAt = start,
        Source = source,
        Status = MedicationOrderStatus.Active,
    };
}
