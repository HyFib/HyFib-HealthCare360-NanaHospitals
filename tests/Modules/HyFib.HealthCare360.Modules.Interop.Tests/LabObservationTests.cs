using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Interop.Mapping;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.Modules.Interop.Tests;

public sealed class LabObservationTests
{
    private static readonly Guid ResultId = Guid.Parse("01923a4b-0000-7000-8000-00000000c001");
    private static readonly Guid VisitId = Guid.Parse("01923a4b-0000-7000-8000-00000000c002");

    private static LabResultRow Haemoglobin() => new()
    {
        Id = ResultId,
        PatientId = TestData.PatientId,
        VisitId = VisitId,
        TestCode = "HB",
        TestName = "Haemoglobin",
        Loinc = "718-7",
        ResultType = LabResultType.Numeric,
        ValueText = "11.2",
        NumericValue = 11.2000m,
        Unit = "g/dL",
        Flag = ResultFlag.Low,
        ReferenceText = "12.0 - 15.0",
        OrderedAt = TestData.Now.AddHours(-6),
        CollectedAt = TestData.Now.AddHours(-5),
        VerifiedAt = TestData.Now.AddHours(-1),
        ReleasedAt = TestData.Now.AddHours(-1),
        CreatedAt = TestData.Now.AddHours(-5),
    };

    [Fact]
    public void Maps_a_released_numeric_result_with_loinc_and_ucum()
    {
        var observation = LabObservations.Map(Haemoglobin());

        observation.Id.ShouldBe($"lab-{ResultId:D}");
        observation.Status.ShouldBe(R4.ObservationStatus.Final);
        observation.Category.ShouldHaveSingleItem().Coding.ShouldHaveSingleItem().Code.ShouldBe("laboratory");
        observation.Code.Text.ShouldBe("Haemoglobin");
        observation.Code.Coding[0].System.ShouldBe("http://loinc.org");
        observation.Code.Coding[0].Code.ShouldBe("718-7");
        observation.Code.Coding[1].System.ShouldBe("urn:hc360:lab-test");
        observation.Code.Coding[1].Code.ShouldBe("HB");
        var quantity = observation.Value.ShouldBeOfType<R4.Quantity>();
        quantity.Value.ShouldBe(11.2m);
        quantity.Unit.ShouldBe("g/dL");
        quantity.System.ShouldBe("http://unitsofmeasure.org");
        quantity.Code.ShouldBe("g/dL");
        observation.Encounter.Reference.ShouldBe($"Encounter/visit-{VisitId:D}");
        ((R4.FhirDateTime)observation.Effective).Value.ShouldBe("2026-09-25T05:30:00+05:30");
        observation.Issued.ShouldBe(TestData.Now.AddHours(-1));
    }

    [Fact]
    public void Uses_the_order_time_when_the_collection_time_is_unknown() =>
        ((R4.FhirDateTime)LabObservations.Map(Haemoglobin() with { CollectedAt = null }).Effective).Value.ShouldBe("2026-09-25T04:30:00+05:30");

    [Fact]
    public void Codes_only_the_hc360_test_when_the_master_has_no_loinc()
    {
        var code = LabObservations.Map(Haemoglobin() with { Loinc = null }).Code;

        code.Coding.ShouldHaveSingleItem().System.ShouldBe("urn:hc360:lab-test");
    }

    [Theory]
    [InlineData(ResultFlag.Normal, "N")]
    [InlineData(ResultFlag.Low, "L")]
    [InlineData(ResultFlag.High, "H")]
    [InlineData(ResultFlag.CriticalLow, "LL")]
    [InlineData(ResultFlag.CriticalHigh, "HH")]
    [InlineData(ResultFlag.Abnormal, "A")]
    [InlineData(ResultFlag.Critical, "AA")]
    public void Maps_flags_to_v3_interpretation(ResultFlag flag, string code)
    {
        var interpretation = LabObservations.Map(Haemoglobin() with { Flag = flag }).Interpretation.ShouldHaveSingleItem();

        interpretation.Coding.ShouldHaveSingleItem().Code.ShouldBe(code);
        interpretation.Coding[0].System.ShouldBe("http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation");
    }

    [Fact]
    public void Parses_the_reference_interval_into_low_and_high()
    {
        var range = LabObservations.Map(Haemoglobin()).ReferenceRange.ShouldHaveSingleItem();

        range.Text.ShouldBe("12.0 - 15.0");
        range.Low.Value.ShouldBe(12m);
        range.High.Value.ShouldBe(15m);
        range.Low.Code.ShouldBe("g/dL");
    }

    [Theory]
    [InlineData("Up to 40", null, 40.0)]
    [InlineData("< 200", null, 200.0)]
    [InlineData("40 and above", 40.0, null)]
    [InlineData("≥ 60", 60.0, null)]
    [InlineData("3.5 - 5.1 mmol/L", 3.5, 5.1)]
    public void Understands_the_printed_interval_forms(string text, double? low, double? high)
    {
        var range = LabObservations.ReferenceRange(text, "mmol/L").ShouldNotBeNull();

        range.Low?.Value.ShouldBe(low is null ? null : (decimal)low.Value);
        range.High?.Value.ShouldBe(high is null ? null : (decimal)high.Value);
        (range.Low is null).ShouldBe(low is null);
        (range.High is null).ShouldBe(high is null);
    }

    [Fact]
    public void Keeps_a_text_interval_as_text_only()
    {
        var range = LabObservations.ReferenceRange("Non-reactive", null).ShouldNotBeNull();

        range.Text.ShouldBe("Non-reactive");
        range.Low.ShouldBeNull();
        range.High.ShouldBeNull();
    }

    [Theory]
    [InlineData("<0.005", R4.Quantity.QuantityComparator.LessThan, 0.005)]
    [InlineData(">600", R4.Quantity.QuantityComparator.GreaterThan, 600.0)]
    [InlineData("≤ 1.5", R4.Quantity.QuantityComparator.LessOrEqual, 1.5)]
    [InlineData(">= 90", R4.Quantity.QuantityComparator.GreaterOrEqual, 90.0)]
    public void Keeps_analyser_comparators(string printed, R4.Quantity.QuantityComparator comparator, double value)
    {
        var quantity = LabObservations.Map(Haemoglobin() with { ValueText = printed, NumericValue = null }).Value.ShouldBeOfType<R4.Quantity>();

        quantity.Comparator.ShouldBe(comparator);
        quantity.Value.ShouldBe((decimal)value);
    }

    [Fact]
    public void Reads_indian_digit_grouping() =>
        LabObservations.Map(Haemoglobin() with { ValueText = "1,20,000", Unit = "/µL" }).Value.ShouldBeOfType<R4.Quantity>().Value.ShouldBe(120000m);

    [Fact]
    public void Text_results_are_strings()
    {
        var observation = LabObservations.Map(Haemoglobin() with
        {
            ResultType = LabResultType.Culture,
            ValueText = "Growth of Escherichia coli",
            Organism = "Escherichia coli",
            ColonyCount = ">10^5 CFU/mL",
            Flag = ResultFlag.Abnormal,
            ReferenceText = null,
        });

        observation.Value.ShouldBeOfType<R4.FhirString>().Value.ShouldBe("Growth of Escherichia coli");
        observation.Note.Select(n => n.Text).ShouldBe(["Organism: Escherichia coli", "Colony count: >10^5 CFU/mL"]);
        observation.ReferenceRange.ShouldBeEmpty();
    }

    [Fact]
    public void A_unit_unknown_to_ucum_is_sent_as_text_without_a_system()
    {
        var quantity = LabObservations.Map(Haemoglobin() with { Unit = "copies/rxn" }).Value.ShouldBeOfType<R4.Quantity>();

        quantity.Unit.ShouldBe("copies/rxn");
        quantity.System.ShouldBeNull();
        quantity.Code.ShouldBeNull();
    }

    [Theory]
    [InlineData("10³/µL", "10*3/uL")]
    [InlineData("10⁶/µL", "10*6/uL")]
    [InlineData("µIU/mL", "u[IU]/mL")]
    [InlineData("mm/hr", "mm/h")]
    [InlineData("seconds", "s")]
    [InlineData("/hpf", "/[HPF]")]
    public void Maps_printed_lab_units_to_ucum(string printed, string ucum) => UcumUnits.Code(printed).ShouldBe(ucum);

    [Fact]
    public void A_result_corrected_after_release_is_amended()
    {
        LabObservations.Map(Haemoglobin() with { VerifiedAt = TestData.Now }).Status.ShouldBe(R4.ObservationStatus.Amended);
        LabObservations.IsAmended(Haemoglobin()).ShouldBeFalse();
    }

    [Fact]
    public void A_missing_value_is_stated_as_absent()
    {
        var observation = LabObservations.Map(Haemoglobin() with { ValueText = null, NumericValue = null });

        observation.Value.ShouldBeNull();
        observation.DataAbsentReason.Coding.ShouldHaveSingleItem().Code.ShouldBe("unknown");
    }
}
