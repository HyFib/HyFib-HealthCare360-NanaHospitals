using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>
/// Haematology: CBC, ESR, coagulation, blood grouping and malaria antigen. The CBC's analytes carry the HORIBA Yumizen
/// H500's parameter codes as their channels (WBC, HGB, NEU%, NEU#…), so its results go on them as they come; the codes
/// another analyser uses for the differential (NEUT%, LYMPH%…) are kept beside them.
/// </summary>
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

        // The differential in per cent: the Yumizen H500 sends NEU%, LYM%, MON%, EOS% and BAS%; other analysers NEUT%,
        // LYMPH%, MONO%, EO% and BASO%. The H500's code comes first, as its host queries ask for it (a NEU% pending makes it
        // run the differential, DIF).
        yield return new("NEUT", "Neutrophils", Section, Blood, Edta, "770-8",
            Numeric("%", 0, [Ages(0, 30, 40, 80), Ages(30, Years(6), 25, 60), Ages(Years(6), null, 40, 80)]) with { Channels = ["NEU%", "NEUT%"] });
        yield return new("LYMPH", "Lymphocytes", Section, Blood, Edta, "736-9",
            Numeric("%", 0, [Ages(0, 30, 20, 50), Ages(30, Years(6), 35, 65), Ages(Years(6), null, 20, 40)]) with { Channels = ["LYM%", "LYMPH%"] });
        yield return new("MONO", "Monocytes", Section, Blood, Edta, "5905-5", Numeric("%", 0, [All(2, 10)]) with { Channels = ["MON%", "MONO%"] });
        yield return new("EOS", "Eosinophils", Section, Blood, Edta, "713-8", Numeric("%", 0, [All(1, 6)]) with { Channels = ["EOS%", "EO%"] });
        yield return new("BASO", "Basophils", Section, Blood, Edta, "706-2", Numeric("%", 0, [All(0, 2)]) with { Channels = ["BAS%", "BASO%"] });

        // The rest of what the Yumizen H500 sends for a CBC or DIF (its parameter codes are the channels), reported on the
        // CBC, as Nana Hospitals asked: "Analyser sent all parameter to test code", "PCT, PLT: all parameters are shown on
        // patient reports". Adult intervals only, as defaults the laboratory reviews and replaces with its own (by sex
        // where it has them); a child's result prints without one rather than against an adult's:
        // * RDW and the absolute counts: Dacie and Lewis Practical Haematology, 12th ed. (Bain BJ, Bates I, Laffan MA, eds.;
        //   Elsevier, 2017), chapter 2, adult reference ranges (the CBC intervals above follow the same table).
        // * MPV, PDW, plateletcrit and P-LCR: Sachdev R et al., "Establishing biological reference intervals for novel
        //   platelet parameters", Indian J Pathol Microbiol 2014;57(2):231-5, doi:10.4103/0377-4929.134676 (945 healthy
        //   Indian adults, on a Sysmex XE-2100). The H500 counts platelets by impedance and may report PDW in %: compare with
        //   HORIBA's intervals for the H500 before go-live.
        // * Immature granulocytes: Monteiro WO et al., "Definition of reference range for the immature granulocytes parameter
        //   provided by a hematology analyzer", Clin Lab 2021;67(1), doi:10.7754/Clin.Lab.2020.200439.
        // The H500's research-use parameters (ALY, LIC, MIC, MAC…) and its large platelet count (P-LCC, for which no adult
        // interval was found to cite) are not reported: Lab setup lists them among the channels no test maps.
        const string Count = "10³/µL";
        yield return new("NEUT-ABS", "Absolute neutrophil count", Section, Blood, Edta, "751-8", Numeric(Count, 2, [Ages(adult, null, 2.00m, 7.00m)], "NEU#"));
        yield return new("LYMPH-ABS", "Absolute lymphocyte count", Section, Blood, Edta, "731-0", Numeric(Count, 2, [Ages(adult, null, 1.00m, 3.00m)], "LYM#"));
        yield return new("MONO-ABS", "Absolute monocyte count", Section, Blood, Edta, "742-7", Numeric(Count, 2, [Ages(adult, null, 0.20m, 1.00m)], "MON#"));
        yield return new("EOS-ABS", "Absolute eosinophil count", Section, Blood, Edta, "711-2", Numeric(Count, 2, [Ages(adult, null, 0.02m, 0.50m)], "EOS#"));
        yield return new("BASO-ABS", "Absolute basophil count", Section, Blood, Edta, "704-7", Numeric(Count, 2, [Ages(adult, null, 0.02m, 0.10m)], "BAS#"));
        yield return new("IG", "Immature granulocytes", Section, Blood, Edta, null, Numeric("%", 1, [Ages(adult, null, 0.0m, 0.6m)], "IMG%"));
        yield return new("IG-ABS", "Immature granulocytes, absolute count", Section, Blood, Edta, null,
            Numeric(Count, 2, [Ages(adult, null, 0.00m, 0.06m)], "IMG#"));

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
        yield return new("RDW-CV", "Red cell distribution width (RDW-CV)", Section, Blood, Edta, "788-0",
            Numeric("%", 1, [Ages(adult, null, 11.6m, 14.0m)], "RDW-CV"));
        yield return new("RDW-SD", "Red cell distribution width (RDW-SD)", Section, Blood, Edta, "21000-5",
            Numeric("fL", 1, [Ages(adult, null, 39.0m, 46.0m)], "RDW-SD"));
        yield return new("PLT", "Platelet count", Section, Blood, Edta, "777-3",
            Numeric("10³/µL", 0, [All(150, 410, 20, 1000)], "PLT", delta: 50m));
        yield return new("MPV", "Mean platelet volume (MPV)", Section, Blood, Edta, "32623-1",
            Numeric("fL", 1, [Ages(adult, null, 8.6m, 15.5m)], "MPV"));
        yield return new("PCT", "Plateletcrit (PCT)", Section, Blood, Edta, "51637-7",
            Numeric("%", 3, [Ages(adult, null, 0.150m, 0.620m)], "PCT"));
        yield return new("PDW", "Platelet distribution width (PDW)", Section, Blood, Edta, "32207-3",
            Numeric("fL", 1, [Ages(adult, null, 8.3m, 25.0m)], "PDW"));
        yield return new("P-LCR", "Platelet large cell ratio (P-LCR)", Section, Blood, Edta, "48386-7",
            Numeric("%", 1, [Ages(adult, null, 11.9m, 66.9m)], "P-LCR"));

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
        const string Absolute = "Absolute leucocyte counts";

        // Every parameter the Yumizen H500 reports for a CBC or DIF (see the analytes above).
        yield return new OrderableSeed(
            "LAB-CBC", "Complete blood count (CBC)", "CBC", 350m, Section, SpecimenType.WholeBlood, ContainerType.Edta, 4,
            "Automated 5-part haematology analyser (impedance and flow cytometry)", "58410-2",
            Interpretation: "Peripheral smear is reviewed by a pathologist when the analyser flags abnormal cells.")
        {
            Components =
            [
                ("HB", null), ("TLC", null), ("NEUT", Dlc), ("LYMPH", Dlc), ("MONO", Dlc), ("EOS", Dlc), ("BASO", Dlc), ("IG", Dlc),
                ("NEUT-ABS", Absolute), ("LYMPH-ABS", Absolute), ("MONO-ABS", Absolute), ("EOS-ABS", Absolute), ("BASO-ABS", Absolute),
                ("IG-ABS", Absolute),
                ("RBC", null), ("PCV", null), ("MCV", null), ("MCH", null), ("MCHC", null), ("RDW-CV", null), ("RDW-SD", null),
                ("PLT", null), ("MPV", null), ("PCT", null), ("PDW", null), ("P-LCR", null),
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
