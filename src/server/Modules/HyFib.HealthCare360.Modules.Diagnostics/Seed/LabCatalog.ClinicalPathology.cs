using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>Clinical pathology (urine and stool routine) and microbiology (urine culture).</summary>
internal static partial class LabCatalog
{
    private static IReadOnlyList<string> StripGrades => ["Nil", "Trace", "1+", "2+", "3+", "4+"];

    private static IEnumerable<AnalyteSeed> ClinicalPathologyAnalytes()
    {
        const LabSection Section = LabSection.ClinicalPathology;
        const SpecimenType Urine = SpecimenType.Urine;
        const ContainerType UrinePot = ContainerType.UrineContainer;

        yield return new("UR-COLOUR", "Colour", Section, Urine, UrinePot, "5778-6",
            Choice(["Pale yellow", "Yellow", "Dark yellow", "Amber", "Red", "Brown", "Colourless"], ["Pale yellow", "Yellow"]));
        yield return new("UR-APPEAR", "Appearance", Section, Urine, UrinePot, "5767-9",
            Choice(["Clear", "Slightly turbid", "Turbid"], ["Clear"]));
        yield return new("UR-PH", "pH", Section, Urine, UrinePot, "5803-2", Numeric(null, 1, [All(4.5m, 8.0m)]));
        yield return new("UR-SG", "Specific gravity", Section, Urine, UrinePot, "5811-5", Numeric(null, 3, [All(1.005m, 1.030m)]));
        yield return new("UR-PROT", "Protein", Section, Urine, UrinePot, "20454-5", Choice(StripGrades, ["Nil"]));
        yield return new("UR-GLU", "Glucose", Section, Urine, UrinePot, "25428-4", Choice(StripGrades, ["Nil"]));
        yield return new("UR-KET", "Ketone bodies", Section, Urine, UrinePot, "2514-8", Choice(["Nil", "Trace", "1+", "2+", "3+"], ["Nil"]));
        yield return new("UR-BIL", "Bilirubin", Section, Urine, UrinePot, "5770-3", Choice(["Negative", "Positive"], ["Negative"]));
        yield return new("UR-URO", "Urobilinogen", Section, Urine, UrinePot, null, Choice(["Normal", "Increased"], ["Normal"]));
        yield return new("UR-BLD", "Blood", Section, Urine, UrinePot, "5794-3", Choice(["Nil", "Trace", "1+", "2+", "3+"], ["Nil"]));
        yield return new("UR-NIT", "Nitrite", Section, Urine, UrinePot, "5802-4", Choice(["Negative", "Positive"], ["Negative"]));
        yield return new("UR-LE", "Leucocyte esterase", Section, Urine, UrinePot, "5799-2",
            Choice(["Negative", "Trace", "1+", "2+", "3+"], ["Negative"]));
        yield return new("UR-PUS", "Pus cells", Section, Urine, UrinePot, "5821-4", FreeText("/hpf", "0 - 5"));
        yield return new("UR-RBC", "Red blood cells", Section, Urine, UrinePot, "13945-1", FreeText("/hpf", "Nil"));
        yield return new("UR-EPI", "Epithelial cells", Section, Urine, UrinePot, null, FreeText("/hpf", "Few"));
        yield return new("UR-CAST", "Casts", Section, Urine, UrinePot, null, FreeText(null, "Nil"));
        yield return new("UR-CRYS", "Crystals", Section, Urine, UrinePot, null, FreeText(null, "Nil"));
        yield return new("UR-BACT", "Bacteria", Section, Urine, UrinePot, null, FreeText(null, "Nil"));

        const SpecimenType Stool = SpecimenType.Stool;
        const ContainerType StoolPot = ContainerType.StoolContainer;
        yield return new("ST-COLOUR", "Colour", Section, Stool, StoolPot, null,
            Choice(["Brown", "Yellow", "Green", "Black", "Clay", "Red"], ["Brown", "Yellow"]));
        yield return new("ST-CONS", "Consistency", Section, Stool, StoolPot, null,
            Choice(["Formed", "Semi-formed", "Loose", "Watery"], ["Formed", "Semi-formed"]));
        yield return new("ST-MUCUS", "Mucus", Section, Stool, StoolPot, null, Choice(["Absent", "Present"], ["Absent"]));
        yield return new("ST-BLOOD", "Visible blood", Section, Stool, StoolPot, null, Choice(["Absent", "Present"], ["Absent"]));
        yield return new("ST-OB", "Occult blood", Section, Stool, StoolPot, null, Choice(["Negative", "Positive"], ["Negative"]));
        yield return new("ST-OVA", "Ova", Section, Stool, StoolPot, null, FreeText(null, "Not seen"));
        yield return new("ST-CYST", "Cysts", Section, Stool, StoolPot, null, FreeText(null, "Not seen"));
        yield return new("ST-PUS", "Pus cells", Section, Stool, StoolPot, null, FreeText("/hpf", "0 - 2"));
        yield return new("ST-RBC", "Red blood cells", Section, Stool, StoolPot, null, FreeText("/hpf", "Nil"));
    }

    private static IEnumerable<OrderableSeed> ClinicalPathologyTests()
    {
        const string Physical = "Physical examination";
        const string Chemical = "Chemical examination";
        const string Microscopy = "Microscopic examination";

        yield return new OrderableSeed(
            "LAB-URINE-RE", "Urine routine and microscopy", "URINE RE", 150m, LabSection.ClinicalPathology, SpecimenType.Urine,
            ContainerType.UrineContainer, 2, "Reagent strip and microscopy", "24356-8",
            Preparation: "Clean-catch mid-stream sample, preferably the first morning urine.")
        {
            Components =
            [
                ("UR-COLOUR", Physical), ("UR-APPEAR", Physical), ("UR-SG", Physical),
                ("UR-PH", Chemical), ("UR-PROT", Chemical), ("UR-GLU", Chemical), ("UR-KET", Chemical), ("UR-BIL", Chemical),
                ("UR-URO", Chemical), ("UR-BLD", Chemical), ("UR-NIT", Chemical), ("UR-LE", Chemical),
                ("UR-PUS", Microscopy), ("UR-RBC", Microscopy), ("UR-EPI", Microscopy), ("UR-CAST", Microscopy), ("UR-CRYS", Microscopy),
                ("UR-BACT", Microscopy),
            ],
        };

        yield return new OrderableSeed(
            "LAB-STOOL-RE", "Stool routine and microscopy", "STOOL RE", 150m, LabSection.ClinicalPathology, SpecimenType.Stool,
            ContainerType.StoolContainer, 4, "Macroscopy, occult blood and saline/iodine microscopy", null,
            Preparation: "Fresh sample in the clean container provided; deliver within 2 hours.")
        {
            Components =
            [
                ("ST-COLOUR", Physical), ("ST-CONS", Physical), ("ST-MUCUS", Physical), ("ST-BLOOD", Physical),
                ("ST-OB", Chemical),
                ("ST-OVA", Microscopy), ("ST-CYST", Microscopy), ("ST-PUS", Microscopy), ("ST-RBC", Microscopy),
            ],
        };

        yield return new OrderableSeed(
            "LAB-CULTURE-URINE", "Urine culture and sensitivity", "URINE C/S", 700m, LabSection.Microbiology, SpecimenType.Urine,
            ContainerType.SterileContainer, 72, "Semi-quantitative culture (CLED / blood agar) with antibiotic susceptibility (Kirby-Bauer, CLSI)", "630-4",
            Preparation: "Clean-catch mid-stream urine in a sterile container before starting antibiotics; deliver to the lab within an hour.",
            Interpretation: "Growth of ≥ 10⁵ CFU/mL of a single organism is significant in a clean-catch sample; mixed growth suggests contamination.")
        {
            Result = new ResultSeed(LabResultType.Culture, null, 0, [Answers(["No growth", "No growth after 48 hours of incubation", "Sterile"])]),
        };
    }
}
