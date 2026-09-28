using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class DoseCalculatorTests
{
    [Theory]
    [InlineData("500 mg", 500, AmountUnit.Mg)]
    [InlineData("500mg", 500, AmountUnit.Mg)]
    [InlineData("0.5 g", 500, AmountUnit.Mg)]
    [InlineData("250 mcg", 0.25, AmountUnit.Mg)]
    [InlineData("1 tab", 1, AmountUnit.Count)]
    [InlineData("2 capsules", 2, AmountUnit.Count)]
    [InlineData("½ tab", 0.5, AmountUnit.Count)]
    [InlineData("1/2 tab", 0.5, AmountUnit.Count)]
    [InlineData("1", 1, AmountUnit.Count)]
    [InlineData("5 ml", 5, AmountUnit.Ml)]
    [InlineData("2 puffs", 2, AmountUnit.Other)]
    public void Doses_are_parsed(string dose, double value, AmountUnit unit)
    {
        var parsed = DoseCalculator.ParseDose(dose).ShouldNotBeNull();
        parsed.Value.ShouldBe((decimal)value);
        parsed.Unit.ShouldBe(unit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("one tablet")]
    [InlineData("as directed")]
    public void Unreadable_doses_return_null(string dose) => DoseCalculator.ParseDose(dose).ShouldBeNull();

    [Theory]
    [InlineData(DoseFrequency.OD, null, 1)]
    [InlineData(DoseFrequency.BD, null, 2)]
    [InlineData(DoseFrequency.TDS, null, 3)]
    [InlineData(DoseFrequency.QID, null, 4)]
    [InlineData(DoseFrequency.HS, null, 1)]
    [InlineData(DoseFrequency.Custom, "1-0-1", 2)]
    [InlineData(DoseFrequency.Custom, "2-0-2", 4)]
    [InlineData(DoseFrequency.Custom, "1-1-1-1", 4)]
    [InlineData(DoseFrequency.Custom, "½-0-½", 1)]
    [InlineData(DoseFrequency.Custom, "8 hourly", 3)]
    [InlineData(DoseFrequency.Custom, "q6h", 4)]
    [InlineData(DoseFrequency.Custom, "every 12 hours", 2)]
    [InlineData(DoseFrequency.Custom, "alternate days", 0.5)]
    public void Doses_per_day_are_derived(DoseFrequency frequency, string? custom, double expected) =>
        DoseCalculator.DosesPerDay(frequency, custom, null).ShouldBe((decimal)expected);

    [Fact]
    public void Sos_needs_an_explicit_maximum()
    {
        DoseCalculator.DosesPerDay(DoseFrequency.SOS, null, null).ShouldBeNull();
        DoseCalculator.DosesPerDay(DoseFrequency.SOS, null, 3m).ShouldBe(3m);
        DoseCalculator.DosesPerDay(DoseFrequency.Custom, "as needed", null).ShouldBeNull();
    }

    [Fact]
    public void Weekly_is_one_seventh_per_day() =>
        DoseCalculator.DosesPerDay(DoseFrequency.Weekly, null, null).ShouldBe(1m / 7m);

    [Theory]
    [InlineData("500 mg", new[] { 500.0 })]
    [InlineData("400 mg + 325 mg", new[] { 400.0, 325.0 })]
    [InlineData("400/325 mg", new[] { 400.0, 325.0 })]
    [InlineData("1 g", new[] { 1000.0 })]
    [InlineData("250 mcg", new[] { 0.25 })]
    public void Strengths_are_parsed_in_label_order(string strength, double[] expected) =>
        DoseCalculator.ParseStrengthMg(strength).ShouldBe(expected.Select(v => (decimal)v).ToList());

    [Theory]
    [InlineData("250 mg/5 ml")]
    [InlineData("60000 IU")]
    [InlineData("1% w/w")]
    [InlineData(null)]
    public void Liquid_iu_and_percentage_strengths_are_not_per_unit_masses(string? strength) =>
        DoseCalculator.ParseStrengthMg(strength).ShouldBeEmpty();

    [Theory]
    [InlineData("250 mg/5 ml", 50)]
    [InlineData("125 mg/5ml", 25)]
    [InlineData("2 mg/ml", 2)]
    public void Concentrations_are_parsed_per_ml(string strength, double mgPerMl) =>
        DoseCalculator.ParseConcentrationMgPerMl(strength).ShouldHaveSingleItem().ShouldBe((decimal)mgPerMl);

    [Theory]
    [InlineData("1 tab", "500 mg", DoseFrequency.TDS, null, 5, 15)]
    [InlineData("1 tab", "500 mg", DoseFrequency.BD, null, 30, 60)]
    [InlineData("500 mg", "500 mg", DoseFrequency.TDS, null, 5, 15)]
    [InlineData("250 mg", "500 mg", DoseFrequency.BD, null, 5, 5)]
    [InlineData("1 tab", "500 mg", DoseFrequency.STAT, null, 1, 1)]
    [InlineData("1 tab", "7.5 mg", DoseFrequency.Weekly, null, 28, 4)]
    [InlineData("1 tab", "10 mg", DoseFrequency.Custom, "1-0-1", 10, 20)]
    [InlineData("½ tab", "5 mg", DoseFrequency.OD, null, 7, 4)]
    public void Quantity_is_calculated_for_solid_forms(string dose, string strength, DoseFrequency frequency, string? custom, int days, int expected) =>
        DoseCalculator.QuantityToDispense(dose, strength, frequency, custom, null, days).ShouldBe(expected);

    [Theory]
    [InlineData("10 ml", "250 mg/5 ml", DoseFrequency.TDS)]
    [InlineData("1 tab", "500 mg", DoseFrequency.SOS)]
    [InlineData("300 mg", "500 mg", DoseFrequency.BD)]
    public void Quantity_is_not_guessed(string dose, string strength, DoseFrequency frequency) =>
        DoseCalculator.QuantityToDispense(dose, strength, frequency, null, null, 5).ShouldBeNull();
}
