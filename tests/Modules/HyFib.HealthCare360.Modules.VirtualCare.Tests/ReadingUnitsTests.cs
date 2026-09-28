using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Tests;

public sealed class ReadingUnitsTests
{
    [Theory]
    [InlineData(RpmReadingType.Temperature, "98.6", "°F", "37")]
    [InlineData(RpmReadingType.Temperature, "102.2", "degF", "39")]
    [InlineData(RpmReadingType.Temperature, "37.2", "°C", "37.2")]
    [InlineData(RpmReadingType.Glucose, "7", "mmol/L", "126.11")]
    [InlineData(RpmReadingType.Glucose, "126", "mg/dL", "126")]
    [InlineData(RpmReadingType.Weight, "154", "lb", "69.85")]
    [InlineData(RpmReadingType.Weight, "72500", "g", "72.5")]
    [InlineData(RpmReadingType.SystolicBp, "16", "kPa", "120.01")]
    [InlineData(RpmReadingType.SystolicBp, "128", "mm[Hg]", "128")]
    [InlineData(RpmReadingType.HeartRate, "72", "/min", "72")]
    [InlineData(RpmReadingType.SpO2, "97", "%", "97")]
    [InlineData(RpmReadingType.SpO2, "97", null, "97")]
    public void Values_are_converted_to_canonical_units(RpmReadingType type, string value, string? unit, string expected) =>
        ReadingUnits.Normalize(type, decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), unit).Value
            .ShouldBe(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Unsupported_units_are_rejected()
    {
        var result = ReadingUnits.Normalize(RpmReadingType.Glucose, 110m, "g/L");

        result.Error!.Fields!.ShouldContainKey("unit");
        result.Error.Message.ShouldContain("mg/dL");
    }

    [Theory]
    [InlineData(RpmReadingType.SystolicBp, 400)]
    [InlineData(RpmReadingType.SpO2, 101)]
    [InlineData(RpmReadingType.HeartRate, 5)]
    [InlineData(RpmReadingType.Temperature, 50)]
    [InlineData(RpmReadingType.Weight, 0)]
    public void Physiologically_impossible_values_are_rejected(RpmReadingType type, int value) =>
        ReadingUnits.Normalize(type, value, null).Error!.Fields!.ShouldContainKey("value");

    [Theory]
    [InlineData(RpmReadingType.SystolicBp, "mmHg", "Systolic BP")]
    [InlineData(RpmReadingType.Glucose, "mg/dL", "Blood glucose")]
    [InlineData(RpmReadingType.Temperature, "°C", "Temperature")]
    [InlineData(RpmReadingType.EcgRhythm, "class", "ECG rhythm")]
    public void Every_type_has_a_canonical_unit_and_label(RpmReadingType type, string unit, string label)
    {
        ReadingUnits.CanonicalUnit(type).ShouldBe(unit);
        ReadingUnits.Label(type).ShouldBe(label);
    }

    [Theory]
    [InlineData("Normal sinus rhythm", 0)]
    [InlineData("Sinus rhythm", 0)]
    [InlineData("Atrial fibrillation suspected", 2)]
    [InlineData("Atrial flutter", 2)]
    [InlineData("Ventricular tachycardia", 2)]
    [InlineData("Sinus tachycardia", 1)]
    [InlineData("Inconclusive, poor contact", 1)]
    [InlineData(null, 1)]
    public void Ecg_interpretations_are_classified(string? text, int expected) =>
        ReadingUnits.ClassifyRhythm(text).ShouldBe(expected);

    [Fact]
    public void Readings_are_described_for_people()
    {
        ReadingUnits.Describe(RpmReadingType.SystolicBp, 150m).ShouldBe("Systolic BP 150 mmHg");
        ReadingUnits.Describe(RpmReadingType.Temperature, 38.45m).ShouldBe("Temperature 38.45 °C");
        ReadingUnits.Describe(RpmReadingType.EcgRhythm, 2m, "Atrial fibrillation").ShouldBe("ECG rhythm: Atrial fibrillation");
    }
}
