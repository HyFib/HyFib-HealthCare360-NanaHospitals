using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;
using HyFib.HealthCare360.Modules.Diagnostics.Seed;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests.Seed;

/// <summary>
/// Guards the seeded catalogue: other modules depend on its codes, and every interval in it flags real patients'
/// results, so a typo here is a clinical error rather than a cosmetic one.
/// </summary>
public sealed class LabCatalogTests
{
    private static readonly string[] RequiredLabCodes =
    [
        "LAB-CBC", "LAB-FBS", "LAB-PPBS", "LAB-HBA1C", "LAB-LIPID", "LAB-LFT", "LAB-KFT", "LAB-TFT", "LAB-URINE-RE", "LAB-ESR",
        "LAB-CRP", "LAB-DENGUE-NS1", "LAB-MALARIA", "LAB-WIDAL", "LAB-HBSAG", "LAB-HIV", "LAB-HCV", "LAB-VITD", "LAB-VITB12",
        "LAB-PSA", "LAB-ELECTROLYTES", "LAB-COAG", "LAB-BLOOD-GROUP", "LAB-STOOL-RE", "LAB-CULTURE-URINE",
    ];

    private static readonly string[] RequiredImagingCodes =
    [
        "RAD-XR-CHEST", "RAD-XR-SPINE-LS", "RAD-USG-ABD", "RAD-USG-PELVIS", "RAD-CT-HEAD", "RAD-CT-ABD", "RAD-MRI-BRAIN",
        "RAD-MRI-SPINE", "RAD-MAMMO",
    ];

    private static readonly string[] RequiredCardiologyCodes = ["CARD-ECG", "CARD-ECHO", "CARD-TMT", "CARD-HOLTER"];

    /// <summary>Analytes reported against one sex only (no interval for the other sex is intended).</summary>
    private static readonly string[] SexSpecificTests = ["LAB-PSA"];

    [Fact]
    public void Every_code_other_modules_depend_on_is_seeded()
    {
        RequiredLabCodes.ShouldBeSubsetOf(LabCatalog.Orderables.Select(o => o.Code));
        RequiredImagingCodes.ShouldBeSubsetOf(ImagingCatalog.Procedures.Select(p => p.Code));
        RequiredCardiologyCodes.ShouldBeSubsetOf(ImagingCatalog.Cardiology.Select(c => c.Code));
    }

    [Fact]
    public void Test_and_service_codes_are_unique()
    {
        var labCodes = LabCatalog.Analytes.Select(a => a.Code).Concat(LabCatalog.Orderables.Select(o => o.Code)).ToList();
        var serviceCodes = LabCatalog.Orderables.Select(o => o.Code)
            .Concat(ImagingCatalog.Procedures.Select(p => p.Code))
            .Concat(ImagingCatalog.Cardiology.Select(c => c.Code))
            .ToList();

        labCodes.ShouldBeUnique();
        serviceCodes.ShouldBeUnique();
        RadiologyTemplates.All.Select(t => t.Code).ShouldBeUnique();
    }

    [Theory]
    [InlineData("LAB-CBC", new[]
    {
        "HB", "TLC", "NEUT", "LYMPH", "MONO", "EOS", "BASO", "IG", "NEUT-ABS", "LYMPH-ABS", "MONO-ABS", "EOS-ABS", "BASO-ABS", "IG-ABS",
        "RBC", "PCV", "MCV", "MCH", "MCHC", "RDW-CV", "RDW-SD", "PLT", "MPV", "PCT", "PDW", "P-LCR",
    })]
    [InlineData("LAB-LIPID", new[] { "CHOL", "TG", "HDL", "LDL", "VLDL" })]
    [InlineData("LAB-KFT", new[] { "UREA", "CREA", "UA", "NA", "K", "CL" })]
    [InlineData("LAB-TFT", new[] { "T3", "T4", "TSH" })]
    [InlineData("LAB-COAG", new[] { "PT", "INR", "APTT" })]
    [InlineData("LAB-ELECTROLYTES", new[] { "NA", "K", "CL", "HCO3" })]
    public void Panels_report_the_analytes_clinicians_expect(string code, string[] analytes) =>
        LabCatalog.Orderables.Single(o => o.Code == code).Components.Select(c => c.Code).ShouldBe(analytes, ignoreOrder: true);

    /// <summary>
    /// The Yumizen H500's CBC and DIF parameters (manual RAA085BEN, YumizenH500Tests), by its own codes: Nana Hospitals
    /// reports all of them (H500-1, H500-2, N6).
    /// </summary>
    private static readonly string[] YumizenH500Parameters =
    [
        "WBC", "RBC", "HGB", "HCT", "MCV", "MCH", "MCHC", "RDW-CV", "RDW-SD", "PLT", "MPV", "PCT", "PDW", "P-LCR",
        "NEU%", "LYM%", "MON%", "EOS%", "BAS%", "IMG%", "NEU#", "LYM#", "MON#", "EOS#", "BAS#", "IMG#",
    ];

    [Fact]
    public void The_cbc_reports_every_parameter_the_yumizen_h500_sends_each_on_its_own_analyte()
    {
        var cbc = LabCatalog.Orderables.Single(o => o.Code == "LAB-CBC").Components.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var analytes = LabCatalog.Analytes.Where(a => cbc.Contains(a.Code)).ToList();

        foreach (var parameter in YumizenH500Parameters)
        {
            var mapping = analytes.Where(a => ChannelMappings.Score(a.Result.Channels ?? [], "H500", parameter) > 0).ToList();
            mapping.Count.ShouldBe(1, $"{parameter} goes on exactly one CBC analyte");
            Analyte(mapping[0]).ChannelFor("H500").ShouldBe(parameter, $"a host query asks for {mapping[0].Code} by the H500's own code");
        }

        analytes.Count.ShouldBe(cbc.Count);
        analytes.Where(a => !YumizenH500Parameters.Any(p => ChannelMappings.Score(a.Result.Channels ?? [], "H500", p) > 0))
            .Select(a => a.Code).ShouldBeEmpty("every CBC analyte is one the H500 measures");
        LabCatalog.Orderables.Single(o => o.Code == "LAB-ESR").Result!.Channels.ShouldBe(["ESR"], "the H500 runs the ESR too");
    }

    [Theory]
    [InlineData("NEUT", "NEU%", "NEUT%")]
    [InlineData("LYMPH", "LYM%", "LYMPH%")]
    [InlineData("MONO", "MON%", "MONO%")]
    [InlineData("EOS", "EOS%", "EO%")]
    [InlineData("BASO", "BAS%", "BASO%")]
    public void The_differential_takes_the_h500s_codes_and_keeps_the_codes_it_had(string analyte, string h500, string before) =>
        LabCatalog.Analytes.Single(a => a.Code == analyte).Result.Channels.ShouldBe([h500, before]);

    [Theory]
    [InlineData("ALY%")]
    [InlineData("ALY#")]
    [InlineData("LIC%")]
    [InlineData("LIC#")]
    [InlineData("MIC")]
    [InlineData("MAC")]
    [InlineData("P-LCC")]
    public void The_h500s_research_parameters_and_its_large_platelet_count_go_on_no_report(string parameter) =>
        LabCatalog.Analytes.Where(a => ChannelMappings.Score(a.Result.Channels ?? [], "H500", parameter) > 0).ShouldBeEmpty();

    [Fact]
    public void The_new_cbc_parameters_have_a_unit_and_an_adult_interval_but_none_for_a_child()
    {
        string[] added = ["IG", "NEUT-ABS", "LYMPH-ABS", "MONO-ABS", "EOS-ABS", "BASO-ABS", "IG-ABS", "RDW-CV", "RDW-SD", "MPV", "PCT", "PDW", "P-LCR"];
        foreach (var code in added)
        {
            var result = LabCatalog.Analytes.Single(a => a.Code == code).Result;
            result.Unit.ShouldNotBeNullOrWhiteSpace(code);
            var ranges = result.Ranges.Select(r => r.ToEntity(Guid.Empty)).ToList();
            ReferenceRangeSelector.Select(ranges, Gender.Female, TestData.Years(35)).ShouldNotBeNull(code);
            ReferenceRangeSelector.Select(ranges, Gender.Male, TestData.Years(5)).ShouldBeNull($"{code}: a child is not read against an adult interval");
        }

        // Spot checks against the cited sources.
        LabCatalog.Analytes.Single(a => a.Code == "NEUT-ABS").Result.Ranges.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            r => r.Low.ShouldBe(2.00m), r => r.High.ShouldBe(7.00m));
        LabCatalog.Analytes.Single(a => a.Code == "PCT").Result.ShouldSatisfyAllConditions(
            r => r.Unit.ShouldBe("%"), r => r.Decimals.ShouldBe(3), r => r.Ranges.ShouldHaveSingleItem().High.ShouldBe(0.620m));
    }

    /// <summary>
    /// Bicarbonate, measured on the EXIAS e|1, which is not interfaced: on the electrolytes panel, entered by hand like the
    /// panel's other tests, with a channel should the analyser ever be interfaced.
    /// </summary>
    [Fact]
    public void Bicarbonate_is_on_the_electrolytes_panel_in_mmol_per_litre_with_an_adult_interval()
    {
        var bicarbonate = LabCatalog.Analytes.Single(a => a.Code == "HCO3");

        (bicarbonate.Name, bicarbonate.Specimen, bicarbonate.Container, bicarbonate.Loinc).ShouldBe(("Bicarbonate (total CO2)", SpecimenType.Serum, ContainerType.Plain, "1963-8"));
        bicarbonate.Result.ShouldSatisfyAllConditions(
            r => r.Type.ShouldBe(LabResultType.Numeric),
            r => r.Unit.ShouldBe("mmol/L"),
            r => r.Channels.ShouldBe(["HCO3"]));
        var range = ReferenceRangeSelector.Select(bicarbonate.Result.Ranges.Select(r => r.ToEntity(Guid.Empty)), Gender.Male, TestData.Years(40)).ShouldNotBeNull();
        (range.Low, range.High).ShouldBe((22m, 29m));
        LabCatalog.Orderables.Single(o => o.Code == "LAB-ELECTROLYTES").Name.ShouldBe("Serum electrolytes (Na, K, Cl, HCO3)");
    }

    [Fact]
    public void Every_test_is_either_a_panel_of_known_analytes_or_a_single_result()
    {
        var known = LabCatalog.Analytes.Select(a => a.Code).Concat(LabCatalog.Orderables.Select(o => o.Code)).ToHashSet(StringComparer.Ordinal);

        foreach (var test in LabCatalog.Orderables)
        {
            (test.IsPanel ^ test.Result is not null).ShouldBeTrue($"{test.Code} must be either a panel or a single result");
            test.Components.Select(c => c.Code).ShouldBeUnique($"{test.Code} lists an analyte twice");
            test.Components.ShouldAllBe(c => known.Contains(c.Code), $"{test.Code} refers to an unknown analyte");
        }
    }

    [Fact]
    public void Every_reference_interval_is_consistent()
    {
        var problems = ResultDefinitions()
            .SelectMany(d => d.Result.Ranges.Select(r => (d.Code, Error: r.ToEntity(Guid.Empty).Validate().Error)))
            .Where(x => x.Error is not null)
            .Select(x => $"{x.Code}: {x.Error!.Code}")
            .ToList();

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Age_bands_for_the_same_sex_never_overlap_so_exactly_one_interval_applies()
    {
        var problems = new List<string>();
        foreach (var (code, result) in ResultDefinitions())
        {
            var ranges = result.Ranges;
            for (var i = 0; i < ranges.Count; i++)
            {
                for (var j = i + 1; j < ranges.Count; j++)
                {
                    if (ranges[i].Gender == ranges[j].Gender && Overlaps(ranges[i], ranges[j]))
                    {
                        problems.Add($"{code}: bands {i} and {j} overlap");
                    }
                }
            }
        }

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Every_numeric_analyte_flags_adult_men_and_women()
    {
        var adult = TestData.Years(35);
        var missing = ResultDefinitions()
            .Where(d => d.Result.Type == LabResultType.Numeric && !SexSpecificTests.Contains(d.Code))
            .SelectMany(d => new[] { Gender.Male, Gender.Female }
                .Where(g => ReferenceRangeSelector.Select(d.Result.Ranges.Select(r => r.ToEntity(Guid.Empty)), g, adult) is null)
                .Select(g => $"{d.Code} ({g})"))
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Numeric_intervals_state_their_limits_and_answer_lists_their_values()
    {
        foreach (var (code, result) in ResultDefinitions())
        {
            if (result.Type == LabResultType.Numeric)
            {
                result.Ranges.ShouldAllBe(r => r.Low != null || r.High != null, $"{code} has an interval without limits");
                result.Unit?.Length.ShouldBeLessThanOrEqualTo(32);
                result.Decimals.ShouldBeInRange(0, 4);
            }

            if (result.Type == LabResultType.Options)
            {
                var options = result.Options.ShouldNotBeNull($"{code} has no answer list");
                options.Count.ShouldBeGreaterThanOrEqualTo(2);
                options.ShouldBeUnique();
                result.Ranges.SelectMany(r => (r.Normal ?? []).Concat(r.Critical ?? [])).ShouldAllBe(
                    answer => options.Contains(answer), $"{code} flags an answer that cannot be selected");
            }
        }
    }

    [Fact]
    public void Codes_and_texts_fit_their_columns()
    {
        foreach (var test in LabCatalog.Orderables)
        {
            test.Code.Length.ShouldBeLessThanOrEqualTo(32);
            test.Name.Length.ShouldBeLessThanOrEqualTo(200);
            test.ShortName.Length.ShouldBeLessThanOrEqualTo(32);
            (test.Method?.Length ?? 0).ShouldBeLessThanOrEqualTo(200);
            (test.Preparation?.Length ?? 0).ShouldBeLessThanOrEqualTo(500);
            (test.Interpretation?.Length ?? 0).ShouldBeLessThanOrEqualTo(2000);
            test.Components.ShouldAllBe(c => c.Group == null || c.Group.Length <= 100);
        }

        foreach (var analyte in LabCatalog.Analytes)
        {
            analyte.Code.Length.ShouldBeLessThanOrEqualTo(32);
            analyte.Name.Length.ShouldBeLessThanOrEqualTo(200);
        }

        ResultDefinitions().SelectMany(d => d.Result.Ranges).ShouldAllBe(r => r.Text == null || r.Text.Length <= 500);
    }

    [Fact]
    public void LOINC_codes_are_well_formed_with_a_valid_check_digit()
    {
        var codes = LabCatalog.Analytes.Select(a => (a.Code, a.Loinc))
            .Concat(LabCatalog.Orderables.Select(o => (o.Code, o.Loinc)))
            .Where(x => x.Loinc is not null)
            .ToList();

        codes.Count.ShouldBeGreaterThan(40);
        codes.Where(x => !IsValidLoinc(x.Loinc!)).Select(x => $"{x.Code}: {x.Loinc}").ShouldBeEmpty();
    }

    [Fact]
    public void Tariffs_are_whole_rupees_in_a_realistic_range()
    {
        var prices = LabCatalog.Orderables.Select(o => (o.Code, o.Price))
            .Concat(ImagingCatalog.Procedures.Select(p => (p.Code, p.Price)))
            .Concat(ImagingCatalog.Cardiology.Select(c => (c.Code, c.Price)));

        prices.ShouldAllBe(p => p.Price >= 50m && p.Price <= 20000m && p.Price == decimal.Truncate(p.Price));
        LabCatalog.Orderables.ShouldAllBe(o => o.TatHours > 0);
    }

    [Fact]
    public void Fasting_and_post_prandial_glucose_share_QC_but_never_a_tube()
    {
        var fasting = LabCatalog.Orderables.Single(o => o.Code == "LAB-FBS");
        var postPrandial = LabCatalog.Orderables.Single(o => o.Code == "LAB-PPBS");

        fasting.Result!.QcGroup.ShouldBe("GLU");
        postPrandial.Result!.QcGroup.ShouldBe("GLU");
        postPrandial.DrawSeparately.ShouldBeTrue();
        LabCatalog.Orderables.Where(o => o.DrawSeparately).Select(o => o.Code).ShouldBe(["LAB-PPBS", "LAB-GCT"], ignoreOrder: true);
    }

    /// <summary>
    /// The 50 g glucose challenge test (C150-2: "GCT sample marked for GLU code, not one code"): read on the glucose
    /// channel, from its own fluoride tube, with the glucose assay's QC; 140 mg/dL or more is a positive screen.
    /// </summary>
    [Fact]
    public void The_glucose_challenge_test_is_read_on_the_glucose_channel_from_its_own_fluoride_tube()
    {
        var challenge = LabCatalog.Orderables.Single(o => o.Code == "LAB-GCT");
        var fasting = LabCatalog.Orderables.Single(o => o.Code == "LAB-FBS");

        challenge.Name.ShouldBe("Glucose challenge test (50 g, 1 hour)");
        (challenge.Specimen, challenge.Container, challenge.Section).ShouldBe((SpecimenType.Plasma, ContainerType.Fluoride, LabSection.Biochemistry));
        challenge.DrawSeparately.ShouldBeTrue();
        challenge.Price.ShouldBe(fasting.Price, "its tariff is the other glucose tests'");
        var result = challenge.Result.ShouldNotBeNull();
        result.Channels.ShouldBe(["GLU"]);
        result.QcGroup.ShouldBe("GLU");
        result.Unit.ShouldBe("mg/dL");

        var test = new LabTest { Code = challenge.Code, Name = challenge.Name, ResultType = LabResultType.Numeric, Unit = "mg/dL" };
        var range = ReferenceRangeSelector.Select(result.Ranges.Select(r => r.ToEntity(Guid.Empty)), Gender.Female, TestData.Years(28)).ShouldNotBeNull();
        ResultInterpreter.Interpret(test, range, "139").Value.Flag.ShouldBe(ResultFlag.Normal);
        ResultInterpreter.Interpret(test, range, "140").Value.Flag.ShouldBe(ResultFlag.High, "140 mg/dL is a positive screen");
        ResultInterpreter.Interpret(test, range, "460").Value.Flag.ShouldBe(ResultFlag.CriticalHigh);
        range.Text.ShouldNotBeNull().ShouldStartWith("< 140");
    }

    [Fact]
    public void Critical_limits_exist_for_the_analytes_that_need_a_phone_call()
    {
        string[] mustHaveCriticalLimits = ["HB", "PLT", "K", "NA", "LAB-FBS", "INR"];

        mustHaveCriticalLimits.ShouldAllBe(code => ResultDefinitions().Any(
            d => d.Code == code && d.Result.Ranges.Any(r => r.CriticalLow != null || r.CriticalHigh != null)));
    }

    [Fact]
    public void Every_seeded_procedure_has_a_report_template_for_its_modality_and_body_part()
    {
        foreach (var procedure in ImagingCatalog.Procedures)
        {
            RadiologyTemplates.All.ShouldContain(
                t => t.Modality == procedure.Modality && t.BodyPart == procedure.BodyPart,
                $"{procedure.Code} has no report template");
        }
    }

    [Fact]
    public void Imaging_masters_fit_their_columns_and_flag_contrast_studies()
    {
        ImagingCatalog.Procedures.ShouldAllBe(p =>
            p.Code.Length <= 32 && p.Name.Length <= 200 && p.ShortName.Length <= 64 && p.BodyPart.Length <= 100
            && (p.Preparation == null || p.Preparation.Length <= 1000) && p.DurationMinutes > 0);
        ImagingCatalog.Cardiology.ShouldAllBe(c => c.Code.Length <= 32 && c.Name.Length <= 200 && c.ShortName.Length <= 64);
        RadiologyTemplates.All.ShouldAllBe(t =>
            t.Code.Length <= 32 && t.Name.Length <= 200 && (t.Technique == null || t.Technique.Length <= 2000)
            && t.Findings.Length <= 8000 && t.Impression.Length <= 4000 && t.Findings.Length > 0 && t.Impression.Length > 0);
        ImagingCatalog.Procedures.Single(p => p.Code == "RAD-CT-ABD").RequiresContrast.ShouldBeTrue();
        ImagingCatalog.Procedures.Single(p => p.Code == "RAD-CT-HEAD").RequiresContrast.ShouldBeFalse();
    }

    private static LabTest Analyte(AnalyteSeed seed) => new() { Code = seed.Code, InstrumentChannelCodes = [.. seed.Result.Channels ?? []] };

    private static IEnumerable<(string Code, ResultSeed Result)> ResultDefinitions() =>
        LabCatalog.Analytes.Select(a => (a.Code, a.Result))
            .Concat(LabCatalog.Orderables.Where(o => o.Result is not null).Select(o => (o.Code, o.Result!)));

    private static bool Overlaps(RangeSeed a, RangeSeed b) =>
        a.AgeFromDays < (b.AgeToDays ?? int.MaxValue) && b.AgeFromDays < (a.AgeToDays ?? int.MaxValue);

    /// <summary>LOINC "NNNNN-C": the check digit is the Luhn (mod 10) digit of the number part.</summary>
    private static bool IsValidLoinc(string loinc)
    {
        var parts = loinc.Split('-');
        if (parts.Length != 2 || parts[0].Length is 0 or > 7 || parts[1].Length != 1
            || !parts[0].All(char.IsAsciiDigit) || !char.IsAsciiDigit(parts[1][0]))
        {
            return false;
        }

        var sum = 0;
        for (var position = 0; position < parts[0].Length; position++)
        {
            var digit = parts[0][^(position + 1)] - '0';
            if (position % 2 == 0)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
        }

        return (10 - (sum % 10)) % 10 == parts[1][0] - '0';
    }
}
