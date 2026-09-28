using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Tests;

public sealed class VitalSignsAssessorTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly DateOnly AdultDob = new(1990, 1, 1);

    private static VitalReading Reading(
        decimal? temperature = null,
        int? pulse = null,
        int? respiration = null,
        int? systolic = null,
        int? diastolic = null,
        int? spo2 = null,
        bool onOxygen = false,
        decimal? bmi = null,
        int? pain = null,
        decimal? glucose = null,
        GlucoseTiming timing = GlucoseTiming.Random,
        int? gcs = null) =>
        new(temperature, pulse, respiration, systolic, diastolic, spo2, onOxygen, bmi, pain, glucose, timing, gcs);

    private static IReadOnlyList<VitalFlag> Adult(VitalReading reading) => VitalSignsAssessor.Assess(reading, AdultDob, Today);

    [Fact]
    public void Normal_adult_vitals_raise_no_flags() =>
        Adult(Reading(37.0m, 80, 16, 120, 80, 98, bmi: 21.0m, pain: 2, glucose: 110m, gcs: 15)).ShouldBeEmpty();

    [Theory]
    [InlineData(38.5, FlagDirection.High, FlagSeverity.Abnormal, "Fever")]
    [InlineData(37.8, FlagDirection.High, FlagSeverity.Abnormal, "Raised temperature")]
    [InlineData(40.5, FlagDirection.High, FlagSeverity.Critical, "Hyperpyrexia (≥ 40 °C)")]
    [InlineData(35.5, FlagDirection.Low, FlagSeverity.Abnormal, "Low temperature")]
    [InlineData(34.5, FlagDirection.Low, FlagSeverity.Critical, "Hypothermia (< 35 °C)")]
    public void Temperature_is_graded(double celsius, FlagDirection direction, FlagSeverity severity, string message)
    {
        var flag = Adult(Reading(temperature: (decimal)celsius)).ShouldHaveSingleItem();
        flag.Parameter.ShouldBe("temperature");
        flag.Direction.ShouldBe(direction);
        flag.Severity.ShouldBe(severity);
        flag.Message.ShouldBe(message);
    }

    [Fact]
    public void Fahrenheit_readings_are_converted_before_flagging()
    {
        var vitals = new VitalSigns { Temperature = 102.2m, TemperatureUnit = TemperatureUnit.Fahrenheit };

        vitals.TemperatureCelsius.ShouldBe(39.0m);
        VitalSignsAssessor.Assess(vitals.ToReading(), AdultDob, Today).ShouldHaveSingleItem().Message.ShouldBe("Fever");
    }

    [Fact]
    public void Pulse_ranges_depend_on_age()
    {
        var infantDob = Today.AddMonths(-6);

        VitalSignsAssessor.Assess(Reading(pulse: 150), infantDob, Today).ShouldBeEmpty();
        var adult = Adult(Reading(pulse: 150)).ShouldHaveSingleItem();
        adult.Severity.ShouldBe(FlagSeverity.Critical);
        adult.Message.ShouldBe("Critical tachycardia");
    }

    [Fact]
    public void Neonatal_respiratory_rate_of_fifty_is_normal_but_critical_in_an_adult()
    {
        VitalSignsAssessor.Assess(Reading(respiration: 50), Today.AddDays(-10), Today).ShouldBeEmpty();
        Adult(Reading(respiration: 50)).ShouldHaveSingleItem().Severity.ShouldBe(FlagSeverity.Critical);
    }

    [Fact]
    public void Paediatric_flags_mention_age()
    {
        var flag = VitalSignsAssessor.Assess(Reading(pulse: 190), Today.AddYears(-4), Today).ShouldHaveSingleItem();
        flag.Severity.ShouldBe(FlagSeverity.Critical);
        flag.Message.ShouldBe("Critical tachycardia for age");
    }

    [Fact]
    public void Adult_bradycardia_is_flagged_low()
    {
        var flag = Adult(Reading(pulse: 48)).ShouldHaveSingleItem();
        flag.Direction.ShouldBe(FlagDirection.Low);
        flag.Severity.ShouldBe(FlagSeverity.Abnormal);
    }

    [Fact]
    public void Hypertensive_crisis_is_critical()
    {
        var flags = Adult(Reading(systolic: 185, diastolic: 125));

        flags.Count.ShouldBe(2);
        flags.ShouldAllBe(f => f.Severity == FlagSeverity.Critical && f.Direction == FlagDirection.High);
    }

    [Fact]
    public void Stage_two_hypertension_is_abnormal_not_critical()
    {
        var flags = Adult(Reading(systolic: 150, diastolic: 95));

        flags.Select(f => f.Parameter).ShouldBe(["systolicBp", "diastolicBp"]);
        flags.ShouldAllBe(f => f.Severity == FlagSeverity.Abnormal);
    }

    [Fact]
    public void Young_children_are_checked_for_hypotension_only()
    {
        var fiveYearOld = Today.AddYears(-5);

        var low = VitalSignsAssessor.Assess(Reading(systolic: 75), fiveYearOld, Today).ShouldHaveSingleItem();
        low.Severity.ShouldBe(FlagSeverity.Critical);
        low.Message.ShouldContain("below 80");
        VitalSignsAssessor.Assess(Reading(systolic: 150, diastolic: 95), fiveYearOld, Today).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(93, FlagSeverity.Abnormal)]
    [InlineData(88, FlagSeverity.Critical)]
    public void Low_oxygen_saturation_is_flagged(int spo2, FlagSeverity severity) =>
        Adult(Reading(spo2: spo2)).ShouldHaveSingleItem().Severity.ShouldBe(severity);

    [Fact]
    public void Saturation_on_supplemental_oxygen_is_always_highlighted() =>
        Adult(Reading(spo2: 97, onOxygen: true)).ShouldHaveSingleItem().Message.ShouldBe("Patient is on supplemental oxygen");

    [Theory]
    [InlineData(15.5, FlagDirection.Low, "Severe thinness (BMI < 16)")]
    [InlineData(17.0, FlagDirection.Low, "Underweight (BMI < 18.5)")]
    [InlineData(24.0, FlagDirection.High, "Overweight (Asian-Indian cut-off, BMI ≥ 23)")]
    [InlineData(26.0, FlagDirection.High, "Obese (Asian-Indian cut-off, BMI ≥ 25)")]
    public void Adult_bmi_uses_asian_indian_cut_offs(double bmi, FlagDirection direction, string message)
    {
        var flag = Adult(Reading(bmi: (decimal)bmi)).ShouldHaveSingleItem();
        flag.Direction.ShouldBe(direction);
        flag.Message.ShouldBe(message);
    }

    [Fact]
    public void Bmi_is_not_flagged_for_children() =>
        VitalSignsAssessor.Assess(Reading(bmi: 26m), Today.AddYears(-10), Today).ShouldBeEmpty();

    [Theory]
    [InlineData(110, GlucoseTiming.Fasting, FlagSeverity.Abnormal, "Impaired fasting glucose (100–125 mg/dL)")]
    [InlineData(130, GlucoseTiming.Fasting, FlagSeverity.Abnormal, "Fasting glucose in the diabetic range (≥ 126 mg/dL)")]
    [InlineData(210, GlucoseTiming.Random, FlagSeverity.Abnormal, "Glucose in the diabetic range (≥ 200 mg/dL)")]
    [InlineData(60, GlucoseTiming.Random, FlagSeverity.Abnormal, "Hypoglycaemia level 1 (54–69 mg/dL)")]
    [InlineData(50, GlucoseTiming.Fasting, FlagSeverity.Critical, "Hypoglycaemia level 2 (< 54 mg/dL)")]
    [InlineData(450, GlucoseTiming.PostPrandial, FlagSeverity.Critical, "Severe hyperglycaemia (≥ 400 mg/dL)")]
    public void Glucose_thresholds_depend_on_timing(int glucose, GlucoseTiming timing, FlagSeverity severity, string message)
    {
        var flag = Adult(Reading(glucose: glucose, timing: timing)).ShouldHaveSingleItem();
        flag.Severity.ShouldBe(severity);
        flag.Message.ShouldBe(message);
    }

    [Fact]
    public void Random_glucose_of_130_is_normal() => Adult(Reading(glucose: 130m)).ShouldBeEmpty();

    [Theory]
    [InlineData(7, FlagSeverity.Critical)]
    [InlineData(10, FlagSeverity.Abnormal)]
    [InlineData(14, FlagSeverity.Abnormal)]
    public void Reduced_gcs_is_flagged(int gcs, FlagSeverity severity) =>
        Adult(Reading(gcs: gcs)).ShouldHaveSingleItem().Severity.ShouldBe(severity);

    [Fact]
    public void Severe_pain_is_flagged() => Adult(Reading(pain: 8)).ShouldHaveSingleItem().Message.ShouldBe("Severe pain (7–10)");

    [Fact]
    public void Unknown_age_uses_adult_ranges() =>
        VitalSignsAssessor.Assess(Reading(pulse: 150), null, Today).ShouldHaveSingleItem().Severity.ShouldBe(FlagSeverity.Critical);

    [Fact]
    public void Bmi_is_computed_from_height_and_weight()
    {
        BodyMassIndex.Compute(170m, 70m).ShouldBe(24.2m);
        BodyMassIndex.Compute(null, 70m).ShouldBeNull();
        BodyMassIndex.Compute(170m, 0m).ShouldBeNull();
    }

    [Fact]
    public void Vitals_entered_in_error_need_a_single_void()
    {
        var vitals = new VitalSigns();

        vitals.MarkEnteredInError("wrong patient").IsSuccess.ShouldBeTrue();
        vitals.MarkEnteredInError("again").Error!.Code.ShouldBe("vitals.already_voided");
    }
}
