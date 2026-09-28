using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>Immunoassays (thyroid, vitamins, PSA) and serology (dengue NS1, Widal, HBsAg, HIV, HCV).</summary>
internal static partial class LabCatalog
{
    private static IReadOnlyList<string> Reactivity => ["Non-reactive", "Reactive", "Indeterminate"];

    private static IReadOnlyList<string> WidalTitres => ["No agglutination", "1:20", "1:40", "1:80", "1:160", "1:320", "1:640"];

    private static IEnumerable<AnalyteSeed> ImmunologyAnalytes()
    {
        const LabSection Section = LabSection.Immunology;
        const SpecimenType Serum = SpecimenType.Serum;
        const ContainerType Plain = ContainerType.Plain;
        var adult = Years(18);

        yield return new("T3", "Triiodothyronine (T3), total", Section, Serum, Plain, "3053-6",
            Numeric("ng/dL", 0, [Ages(Years(1), adult, 90, 240), Ages(adult, null, 80, 200)], "T3"));
        yield return new("T4", "Thyroxine (T4), total", Section, Serum, Plain, "3026-2",
            Numeric("µg/dL", 1, [Ages(0, 30, 10.0m, 22.0m), Ages(30, adult, 6.0m, 14.0m), Ages(adult, null, 5.1m, 14.1m)], "T4"));
        yield return new("TSH", "Thyroid stimulating hormone (TSH)", Section, Serum, Plain, "3016-3", Numeric("µIU/mL", 3,
        [
            Ages(0, 30, 0.700m, 15.200m),
            Ages(30, Years(1), 0.720m, 11.000m),
            Ages(Years(1), Years(6), 0.700m, 5.970m),
            Ages(Years(6), Years(12), 0.600m, 4.840m),
            Ages(Years(12), adult, 0.510m, 4.300m),
            Ages(adult, null, 0.270m, 4.200m),
        ], "TSH"));

        // Widal: in India a single O titre of 1:80 and H titre of 1:160 or more are usually taken as significant.
        const LabSection Serology = LabSection.Serology;
        yield return new("WIDAL-TO", "Salmonella Typhi O", Serology, Serum, Plain, null,
            Choice(WidalTitres, ["No agglutination", "1:20", "1:40"], text: "< 1:80"));
        yield return new("WIDAL-TH", "Salmonella Typhi H", Serology, Serum, Plain, null,
            Choice(WidalTitres, ["No agglutination", "1:20", "1:40", "1:80"], text: "< 1:160"));
        yield return new("WIDAL-AH", "Salmonella Paratyphi AH", Serology, Serum, Plain, null,
            Choice(WidalTitres, ["No agglutination", "1:20", "1:40", "1:80"], text: "< 1:160"));
        yield return new("WIDAL-BH", "Salmonella Paratyphi BH", Serology, Serum, Plain, null,
            Choice(WidalTitres, ["No agglutination", "1:20", "1:40", "1:80"], text: "< 1:160"));
    }

    private static IEnumerable<OrderableSeed> ImmunologyTests()
    {
        const LabSection Immunology = LabSection.Immunology;
        const LabSection Serology = LabSection.Serology;
        const SpecimenType Serum = SpecimenType.Serum;
        const ContainerType Plain = ContainerType.Plain;
        const string Clia = "Chemiluminescence immunoassay (CLIA)";

        yield return new OrderableSeed(
            "LAB-TFT", "Thyroid profile (T3, T4, TSH)", "TFT", 600m, Immunology, Serum, Plain, 8, Clia, null,
            Interpretation: "TSH varies through the day (highest early morning). In pregnancy, interpret against trimester-specific ranges.")
        {
            Components = [("T3", null), ("T4", null), ("TSH", null)],
        };

        yield return new OrderableSeed("LAB-VITD", "Vitamin D (25-hydroxy)", "VIT D", 1400m, Immunology, Serum, Plain, 24, Clia, "62292-8")
        {
            Result = Numeric("ng/mL", 1, [All(30.0m, 100.0m, text: "Deficient: < 20; Insufficient: 20 - 29; Sufficient: 30 - 100; Potential toxicity: > 100")], "VITD"),
        };

        yield return new OrderableSeed("LAB-VITB12", "Vitamin B12 (cobalamin)", "VIT B12", 1000m, Immunology, Serum, Plain, 24, Clia, "2132-9")
        {
            Result = Numeric("pg/mL", 0, [All(211, 911)], "B12"),
        };

        yield return new OrderableSeed(
            "LAB-PSA", "Prostate specific antigen (PSA), total", "PSA", 800m, Immunology, Serum, Plain, 24, Clia, "2857-1",
            Preparation: "Collect before digital rectal examination or catheterisation, or at least 48 hours after.")
        {
            // Age-specific upper limits (Oesterling); PSA is not reported against a female interval.
            Result = Numeric("ng/mL", 2,
            [
                Male(0, Years(50), null, 2.50m),
                Male(Years(50), Years(60), null, 3.50m),
                Male(Years(60), Years(70), null, 4.50m),
                Male(Years(70), null, null, 6.50m),
            ], "PSA"),
        };

        yield return new OrderableSeed(
            "LAB-DENGUE-NS1", "Dengue NS1 antigen", "NS1", 700m, Serology, Serum, Plain, 4, "ELISA / immunochromatography", "75377-2",
            Interpretation: "NS1 is detectable from day 1 to about day 9 of fever. A negative result does not exclude dengue; correlate with IgM/IgG serology and the platelet count.")
        {
            Result = Choice(["Negative", "Positive", "Equivocal"], ["Negative"]),
        };

        yield return new OrderableSeed(
            "LAB-WIDAL", "Widal test (tube agglutination)", "WIDAL", 250m, Serology, Serum, Plain, 24, "Tube agglutination", null,
            Interpretation: "A single titre has limited value in endemic areas; a four-fold rise in paired sera is significant. Blood culture is the test of choice in the first week of fever.")
        {
            Components = [("WIDAL-TO", null), ("WIDAL-TH", null), ("WIDAL-AH", null), ("WIDAL-BH", null)],
        };

        yield return new OrderableSeed("LAB-HBSAG", "Hepatitis B surface antigen (HBsAg)", "HBsAg", 350m, Serology, Serum, Plain, 4, Clia, "5196-1")
        {
            Result = Choice(Reactivity, ["Non-reactive"]),
        };

        yield return new OrderableSeed(
            "LAB-HIV", "HIV 1 & 2 antibodies with p24 antigen (4th generation)", "HIV", 450m, Serology, Serum, Plain, 4, Clia, "56888-1",
            Interpretation: "Reactive results are confirmed as per the NACO testing strategy before disclosure, with pre- and post-test counselling.")
        {
            Result = Choice(Reactivity, ["Non-reactive"]),
        };

        yield return new OrderableSeed("LAB-HCV", "Hepatitis C virus antibody (anti-HCV)", "HCV", 600m, Serology, Serum, Plain, 4, Clia, "13955-0")
        {
            Result = Choice(Reactivity, ["Non-reactive"]),
        };
    }
}
