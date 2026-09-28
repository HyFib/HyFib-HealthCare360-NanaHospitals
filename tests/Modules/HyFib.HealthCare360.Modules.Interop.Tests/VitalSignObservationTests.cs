using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class VitalSignObservationTests
{
    private static readonly Guid VitalsId = Guid.Parse("01923a4b-0000-7000-8000-00000000b001");
    private static readonly Guid EncounterId = Guid.Parse("01923a4b-0000-7000-8000-00000000b002");

    private static VitalSignsRow Vitals() => new()
    {
        Id = VitalsId,
        PatientId = TestData.PatientId,
        EncounterId = EncounterId,
        RecordedAt = TestData.Now,
        CreatedAt = TestData.Now,
    };

    private static VitalSignsRow FullSet() => Vitals() with
    {
        SystolicBp = 128,
        DiastolicBp = 84,
        Pulse = 76,
        SpO2 = 97,
        Temperature = 37.20m,
        RespiratoryRate = 16,
        WeightKg = 68.50m,
        HeightCm = 162.00m,
        Bmi = 26.1m,
        PainScore = 3,
        Gcs = 15,
        BloodGlucose = 110.00m,
    };

    private static R4.Observation Single(VitalSignsRow row, string suffix) =>
        VitalSignObservations.Map(row).Single(o => o.Id == $"vs-{VitalsId:D}-{suffix}");

    [Theory]
    [InlineData("hr", "8867-4", 76, "/min")]
    [InlineData("spo2", "59408-5", 97, "%")]
    [InlineData("temp", "8310-5", 37.2, "Cel")]
    [InlineData("rr", "9279-1", 16, "/min")]
    [InlineData("wt", "29463-7", 68.5, "kg")]
    [InlineData("ht", "8302-2", 162, "cm")]
    [InlineData("bmi", "39156-5", 26.1, "kg/m2")]
    [InlineData("pain", "72514-3", 3, "{score}")]
    [InlineData("gcs", "9269-2", 15, "{score}")]
    [InlineData("glucose", "41653-7", 110, "mg/dL")]
    public void Codes_each_measurement_with_loinc_and_ucum(string suffix, string loinc, double value, string ucum)
    {
        var observation = Single(FullSet(), suffix);

        observation.Code.Coding[0].System.ShouldBe("http://loinc.org");
        observation.Code.Coding[0].Code.ShouldBe(loinc);
        var quantity = observation.Value.ShouldBeOfType<R4.Quantity>();
        quantity.Value.ShouldBe((decimal)value);
        quantity.System.ShouldBe("http://unitsofmeasure.org");
        quantity.Code.ShouldBe(ucum);
    }

    [Fact]
    public void Maps_every_recorded_measurement_once()
    {
        var observations = VitalSignObservations.Map(FullSet());

        observations.Count.ShouldBe(11);
        observations.Select(o => o.Id).Distinct().Count().ShouldBe(11);
        observations.ShouldAllBe(o => o.Subject.Reference == $"Patient/{TestData.PatientId:D}");
        observations.ShouldAllBe(o => o.Encounter.Reference == $"Encounter/enc-{EncounterId:D}");
        observations.ShouldAllBe(o => ((R4.FhirDateTime)o.Effective).Value == "2026-09-25T10:30:00+05:30");
        observations.ShouldAllBe(o => o.Status == R4.ObservationStatus.Final);
    }

    [Fact]
    public void Blood_pressure_is_a_panel_with_systolic_and_diastolic_components()
    {
        var bp = Single(FullSet(), "bp");

        bp.Code.Coding.ShouldHaveSingleItem().Code.ShouldBe("85354-9");
        bp.Value.ShouldBeNull();
        bp.Component.Count.ShouldBe(2);
        bp.Component[0].Code.Coding[0].Code.ShouldBe("8480-6");
        bp.Component[0].Value.ShouldBeOfType<R4.Quantity>().Value.ShouldBe(128m);
        bp.Component[0].Value.ShouldBeOfType<R4.Quantity>().Code.ShouldBe("mm[Hg]");
        bp.Component[1].Code.Coding[0].Code.ShouldBe("8462-4");
        bp.Component[1].Value.ShouldBeOfType<R4.Quantity>().Value.ShouldBe(84m);
        bp.Meta.Profile.ShouldBe(["http://hl7.org/fhir/StructureDefinition/bp"]);
    }

    [Fact]
    public void A_missing_blood_pressure_reading_is_stated_as_absent()
    {
        var bp = Single(Vitals() with { SystolicBp = 150 }, "bp");

        bp.Component[0].Value.ShouldNotBeNull();
        bp.Component[1].Value.ShouldBeNull();
        bp.Component[1].DataAbsentReason.Coding.ShouldHaveSingleItem().Code.ShouldBe("unknown");
    }

    [Fact]
    public void Oxygen_saturation_carries_both_loinc_codes_and_notes_supplemental_oxygen()
    {
        var spo2 = Single(Vitals() with { SpO2 = 93, OnSupplementalOxygen = true }, "spo2");

        spo2.Code.Coding.Select(c => c.Code).ShouldBe(["59408-5", "2708-6"]);
        spo2.Note.ShouldHaveSingleItem().Text.ShouldContain("supplemental oxygen");
        spo2.Meta.Profile.ShouldBe(["http://hl7.org/fhir/StructureDefinition/oxygensat"]);
    }

    [Fact]
    public void Keeps_a_fahrenheit_temperature_in_fahrenheit()
    {
        var temperature = Single(Vitals() with { Temperature = 99.5m, TemperatureUnit = TemperatureUnit.Fahrenheit }, "temp")
            .Value.ShouldBeOfType<R4.Quantity>();

        temperature.Value.ShouldBe(99.5m);
        temperature.Code.ShouldBe("[degF]");
    }

    [Fact]
    public void Bmi_is_derived_from_weight_and_height()
    {
        var bmi = Single(FullSet(), "bmi");

        bmi.DerivedFrom.Select(r => r.Reference).ShouldBe([$"Observation/vs-{VitalsId:D}-wt", $"Observation/vs-{VitalsId:D}-ht"]);
    }

    [Fact]
    public void Vital_signs_are_in_the_vital_signs_category_and_glucose_in_laboratory()
    {
        var observations = VitalSignObservations.Map(FullSet());

        Single(FullSet(), "hr").Category.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("vital-signs");
        Single(FullSet(), "glucose").Category[0].Coding[0].Code.ShouldBe("laboratory");
        Single(FullSet(), "pain").Category[0].Coding[0].Code.ShouldBe("survey");
        observations.Count(o => o.Category[0].Coding[0].Code == "vital-signs").ShouldBe(8);
    }

    [Theory]
    [InlineData(GlucoseTiming.Fasting, "Blood glucose (fasting)")]
    [InlineData(GlucoseTiming.PostPrandial, "Blood glucose (post-prandial)")]
    [InlineData(GlucoseTiming.Random, "Blood glucose (random)")]
    public void Names_the_glucose_timing(GlucoseTiming timing, string text) =>
        Single(Vitals() with { BloodGlucose = 98m, GlucoseTiming = timing }, "glucose").Code.Text.ShouldBe(text);

    [Fact]
    public void Missing_measurements_produce_no_observation()
    {
        VitalSignObservations.Map(Vitals()).ShouldBeEmpty();
        VitalSignObservations.Map(Vitals() with { Pulse = 80 }).ShouldHaveSingleItem().Id.ShouldEndWith("-hr");
    }

    [Fact]
    public void A_set_entered_in_error_keeps_its_values_with_that_status()
    {
        var observations = VitalSignObservations.Map(FullSet() with { IsEnteredInError = true });

        observations.ShouldAllBe(o => o.Status == R4.ObservationStatus.EnteredInError);
        Single(FullSet() with { IsEnteredInError = true }, "hr").Value.ShouldNotBeNull();
    }

    [Fact]
    public void Trailing_zeros_of_stored_decimals_are_not_published_as_precision() =>
        Single(FullSet(), "ht").Value.ShouldBeOfType<R4.Quantity>().Value!.Value.Scale.ShouldBe((byte)0);

    [Fact]
    public void Kinds_resolve_from_the_id_suffix()
    {
        VitalSignObservations.KindOf("bp").ShouldBe(VitalSignObservations.BloodPressure);
        VitalSignObservations.KindOf("nope").ShouldBeNull();
    }
}
