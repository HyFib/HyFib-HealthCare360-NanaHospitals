using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>Haematology: CBC, ESR, coagulation, blood grouping and malaria antigen.</summary>
internal static partial class LabCatalog
{
    private static IEnumerable<AnalyteSeed> HaematologyAnalytes()
    {
        const LabSection Section = LabSection.Hematology;
        const SpecimenType Blood = SpecimenType.WholeBlood;
        const ContainerType Edta = ContainerType.Edta;
        var adult = Years(18);

        // Haemoglobin: neonatal, infant, child and sex-specific adolescent/adult intervals; critical < 7 or > 20 g/dL.
        yield return new("HB", "Haemoglobin", Section, Blood, Edta, "718-7", Numeric("g/dL", 1,
        [
            Ages(0, 30, 14.0m, 22.0m, 9.5m, 24.0m),
            Ages(30, Months(6), 9.5m, 14.0m, 7.0m, 20.0m),
            Ages(Months(6), Years(6), 11.0m, 14.0m, 7.0m, 20.0m),
            Ages(Years(6), Years(12), 11.5m, 15.5m, 7.0m, 20.0m),
            Male(Years(12), adult, 13.0m, 16.0m, 7.0m, 20.0m),
            Female(Years(12), adult, 12.0m, 16.0m, 7.0m, 20.0m),
            Male(adult, null, 13.0m, 17.0m, 7.0m, 20.0m),
            Female(adult, null, 12.0m, 15.0m, 7.0m, 20.0m),
        ], "HGB", delta: 20m));

        yield return new("TLC", "Total leucocyte count", Section, Blood, Edta, "6690-2", Numeric("10³/µL", 2,
        [
            Ages(0, 30, 9.0m, 30.0m, 5.0m, 40.0m),
            Ages(30, Years(2), 6.0m, 17.5m, 2.0m, 30.0m),
            Ages(Years(2), Years(6), 5.0m, 15.5m, 2.0m, 30.0m),
            Ages(Years(6), adult, 4.5m, 13.5m, 2.0m, 30.0m),
            Ages(adult, null, 4.0m, 11.0m, 2.0m, 30.0m),
        ], "WBC", delta: 50m));

        yield return new("NEUT", "Neutrophils", Section, Blood, Edta, "770-8",
            Numeric("%", 0, [Ages(0, 30, 40, 80), Ages(30, Years(6), 25, 60), Ages(Years(6), null, 40, 80)], "NEUT%"));
        yield return new("LYMPH", "Lymphocytes", Section, Blood, Edta, "736-9",
            Numeric("%", 0, [Ages(0, 30, 20, 50), Ages(30, Years(6), 35, 65), Ages(Years(6), null, 20, 40)], "LYMPH%"));
        yield return new("MONO", "Monocytes", Section, Blood, Edta, "5905-5", Numeric("%", 0, [All(2, 10)], "MONO%"));
        yield return new("EOS", "Eosinophils", Section, Blood, Edta, "713-8", Numeric("%", 0, [All(1, 6)], "EO%"));
        yield return new("BASO", "Basophils", Section, Blood, Edta, "706-2", Numeric("%", 0, [All(0, 2)], "BASO%"));

        yield return new("RBC", "RBC count", Section, Blood, Edta, "789-8", Numeric("10⁶/µL", 2,
        [
            Ages(0, 30, 4.00m, 6.60m),
            Ages(30, adult, 3.90m, 5.30m),
            Male(adult, null, 4.50m, 5.50m),
            Female(adult, null, 3.80m, 4.80m),
        ], "RBC"));

        yield return new("PCV", "Packed cell volume (haematocrit)", Section, Blood, Edta, "4544-3", Numeric("%", 1,
        [
            Ages(0, 30, 42.0m, 65.0m, 20.0m, 70.0m),
            Ages(30, adult, 33.0m, 45.0m, 20.0m, 60.0m),
            Male(adult, null, 40.0m, 50.0m, 20.0m, 60.0m),
            Female(adult, null, 36.0m, 46.0m, 20.0m, 60.0m),
        ], "HCT"));

        yield return new("MCV", "Mean corpuscular volume (MCV)", Section, Blood, Edta, "787-2",
            Numeric("fL", 1, [Ages(0, 30, 88.0m, 123.0m), Ages(30, Years(12), 70.0m, 90.0m), Ages(Years(12), null, 83.0m, 101.0m)], "MCV"));
        yield return new("MCH", "Mean corpuscular haemoglobin (MCH)", Section, Blood, Edta, "785-6",
            Numeric("pg", 1, [Ages(0, 30, 31.0m, 37.0m), Ages(30, Years(12), 24.0m, 31.0m), Ages(Years(12), null, 27.0m, 32.0m)], "MCH"));
        yield return new("MCHC", "Mean corpuscular haemoglobin concentration (MCHC)", Section, Blood, Edta, "786-4",
            Numeric("g/dL", 1, [All(31.5m, 34.5m)], "MCHC"));
        yield return new("PLT", "Platelet count", Section, Blood, Edta, "777-3",
            Numeric("10³/µL", 0, [All(150, 410, 20, 1000)], "PLT", delta: 50m));

        // Coagulation (3.2 % citrate plasma).
        yield return new("PT", "Prothrombin time (PT)", Section, SpecimenType.Plasma, ContainerType.Citrate, "5902-2",
            Numeric("seconds", 1, [All(11.0m, 13.5m)], "PT"));
        yield return new("INR", "International normalised ratio (INR)", Section, SpecimenType.Plasma, ContainerType.Citrate, "6301-6",
            Numeric(null, 2, [All(0.80m, 1.20m, null, 5.00m, "0.80 - 1.20 (target on oral anticoagulants: 2.0 - 3.0)")], "INR"));
        yield return new("APTT", "Activated partial thromboplastin time (APTT)", Section, SpecimenType.Plasma, ContainerType.Citrate, "14979-9",
            Numeric("seconds", 1, [All(25.0m, 35.0m, null, 100.0m)], "APTT"));

        // Blood grouping: answers are not "normal" or "abnormal", so they always need manual verification.
        yield return new("ABO", "ABO group", Section, Blood, Edta, "883-9", Choice(["A", "B", "AB", "O"], null));
        yield return new("RH", "Rh (D) type", Section, Blood, Edta, "10331-7", Choice(["Positive", "Negative"], null));
    }

    private static IEnumerable<OrderableSeed> HaematologyTests()
    {
        const LabSection Section = LabSection.Hematology;
        const string Dlc = "Differential leucocyte count";

        yield return new OrderableSeed(
            "LAB-CBC", "Complete blood count (CBC)", "CBC", 350m, Section, SpecimenType.WholeBlood, ContainerType.Edta, 4,
            "Automated 5-part haematology analyser (impedance and flow cytometry)", "58410-2",
            Interpretation: "Peripheral smear is reviewed by a pathologist when the analyser flags abnormal cells.")
        {
            Components =
            [
                ("HB", null), ("TLC", null), ("NEUT", Dlc), ("LYMPH", Dlc), ("MONO", Dlc), ("EOS", Dlc), ("BASO", Dlc),
                ("RBC", null), ("PCV", null), ("MCV", null), ("MCH", null), ("MCHC", null), ("PLT", null),
            ],
        };

        yield return new OrderableSeed(
            "LAB-ESR", "Erythrocyte sedimentation rate (ESR)", "ESR", 120m, Section, SpecimenType.WholeBlood, ContainerType.Edta, 2,
            "Modified Westergren (automated)", "4537-7")
        {
            Result = Numeric("mm/hr", 0,
            [
                Ages(0, Years(18), 0, 10),
                Male(Years(18), Years(50), 0, 15),
                Male(Years(50), null, 0, 20),
                Female(Years(18), Years(50), 0, 20),
                Female(Years(50), null, 0, 30),
            ], "ESR"),
        };

        yield return new OrderableSeed(
            "LAB-COAG", "Coagulation profile (PT/INR, APTT)", "COAG", 700m, Section, SpecimenType.Plasma, ContainerType.Citrate, 3,
            "Clot detection on an automated coagulometer", null,
            Preparation: "Fill the citrate tube exactly to the mark (9 parts blood to 1 part citrate); mention anticoagulant therapy.")
        {
            Components = [("PT", null), ("INR", null), ("APTT", null)],
        };

        yield return new OrderableSeed(
            "LAB-BLOOD-GROUP", "Blood group and Rh typing", "BLOOD GRP", 150m, Section, SpecimenType.WholeBlood, ContainerType.Edta, 1,
            "Column agglutination (forward and reverse grouping)", null)
        {
            Components = [("ABO", null), ("RH", null)],
        };

        yield return new OrderableSeed(
            "LAB-MALARIA", "Malaria parasite antigen (Pf/Pv) rapid test", "MP Ag", 350m, Section, SpecimenType.WholeBlood, ContainerType.Edta, 1,
            "Immunochromatography (HRP-2 / pLDH)", null,
            Interpretation: "A negative rapid test does not exclude malaria at low parasitaemia; repeat, or request a peripheral smear, if clinically suspected.")
        {
            Result = Choice(
                ["Negative", "Positive for P. vivax", "Positive for P. falciparum", "Positive for P. falciparum and P. vivax (mixed)"],
                ["Negative"],
                ["Positive for P. falciparum", "Positive for P. falciparum and P. vivax (mixed)"]),
        };
    }
}
