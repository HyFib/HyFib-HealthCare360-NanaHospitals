using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>Biochemistry: glucose, HbA1c, lipid profile, LFT, KFT, electrolytes and CRP.</summary>
internal static partial class LabCatalog
{
    private static IEnumerable<AnalyteSeed> BiochemistryAnalytes()
    {
        const LabSection Section = LabSection.Biochemistry;
        const SpecimenType Serum = SpecimenType.Serum;
        const ContainerType Plain = ContainerType.Plain;
        var adult = Years(18);

        // Lipid profile (NCEP ATP III / Lipid Association of India cut-offs printed as guidance text).
        yield return new("CHOL", "Total cholesterol", Section, Serum, Plain, "2093-3",
            Numeric("mg/dL", 0, [All(null, 200, text: "Desirable: < 200; Borderline high: 200 - 239; High: >= 240")], "CHOL"));
        yield return new("TG", "Triglycerides", Section, Serum, Plain, "2571-8",
            Numeric("mg/dL", 0, [All(null, 150, text: "Normal: < 150; Borderline high: 150 - 199; High: 200 - 499; Very high: >= 500")], "TG"));
        yield return new("HDL", "HDL cholesterol", Section, Serum, Plain, "2085-9",
            Numeric("mg/dL", 0, [Male(0, null, 40, null, text: "> 40"), Female(0, null, 50, null, text: "> 50"), All(40, null)], "HDL"));
        yield return new("LDL", "LDL cholesterol (calculated)", Section, Serum, Plain, "13457-7",
            Numeric("mg/dL", 0, [All(null, 100, text: "Optimal: < 100; Near optimal: 100 - 129; Borderline high: 130 - 159; High: 160 - 189; Very high: >= 190")], "LDL"));
        yield return new("VLDL", "VLDL cholesterol (calculated)", Section, Serum, Plain, "13458-5",
            Numeric("mg/dL", 0, [All(5, 40)], "VLDL"));

        // Liver function.
        yield return new("TBIL", "Bilirubin, total", Section, Serum, Plain, "1975-2", Numeric("mg/dL", 2,
        [
            Ages(0, 30, null, 12.00m, null, 15.00m, "Up to 12.0 (neonates: interpret on the hour-specific nomogram)"),
            Ages(30, null, 0.30m, 1.20m),
        ], "TBIL"));
        yield return new("DBIL", "Bilirubin, direct (conjugated)", Section, Serum, Plain, "1968-7", Numeric("mg/dL", 2, [All(0.00m, 0.30m)], "DBIL"));
        yield return new("IBIL", "Bilirubin, indirect (unconjugated)", Section, Serum, Plain, "1971-1", Numeric("mg/dL", 2, [Ages(30, null, 0.20m, 0.90m)]));
        yield return new("AST", "SGOT (AST)", Section, Serum, Plain, "1920-8",
            Numeric("U/L", 0, [Male(0, null, null, 40), Female(0, null, null, 32), All(null, 40)], "AST"));
        yield return new("ALT", "SGPT (ALT)", Section, Serum, Plain, "1742-6",
            Numeric("U/L", 0, [Male(0, null, null, 41), Female(0, null, null, 33), All(null, 41)], "ALT"));
        yield return new("ALP", "Alkaline phosphatase (ALP)", Section, Serum, Plain, "6768-6",
            Numeric("U/L", 0, [Ages(0, Years(1), 80, 470), Ages(Years(1), adult, 100, 390), Ages(adult, null, 30, 120)], "ALP"));
        yield return new("GGT", "Gamma-glutamyl transferase (GGT)", Section, Serum, Plain, "2324-2",
            Numeric("U/L", 0, [Male(0, null, 8, 61), Female(0, null, 5, 36), All(5, 61)], "GGT"));
        yield return new("TP", "Total protein", Section, Serum, Plain, "2885-2",
            Numeric("g/dL", 1, [Ages(0, Years(1), 4.4m, 7.6m), Ages(Years(1), null, 6.4m, 8.3m)], "TP"));
        yield return new("ALB", "Albumin", Section, Serum, Plain, "1751-7", Numeric("g/dL", 1, [All(3.5m, 5.2m)], "ALB"));
        yield return new("GLOB", "Globulin (calculated)", Section, Serum, Plain, "10834-0", Numeric("g/dL", 1, [All(2.0m, 3.5m)]));
        yield return new("AGR", "Albumin/globulin ratio", Section, Serum, Plain, "1759-0", Numeric(null, 2, [All(1.10m, 2.20m)]));

        // Kidney function and electrolytes (critical potassium and sodium per common Indian NABL lists).
        yield return new("UREA", "Blood urea", Section, Serum, Plain, "3091-6",
            Numeric("mg/dL", 0, [Ages(0, adult, 10, 40), Ages(adult, null, 17, 43)], "UREA"));
        yield return new("CREA", "Creatinine", Section, Serum, Plain, "2160-0", Numeric("mg/dL", 2,
        [
            Ages(0, Years(1), 0.20m, 0.40m),
            Ages(Years(1), Years(12), 0.30m, 0.70m),
            Ages(Years(12), adult, 0.50m, 1.00m),
            Male(adult, null, 0.70m, 1.30m, null, 7.40m),
            Female(adult, null, 0.60m, 1.10m, null, 7.40m),
        ], "CREA", delta: 30m));
        yield return new("UA", "Uric acid", Section, Serum, Plain, "3084-1",
            Numeric("mg/dL", 1, [Ages(0, adult, 2.0m, 5.5m), Male(adult, null, 3.5m, 7.2m), Female(adult, null, 2.6m, 6.0m)], "UA"));
        yield return new("NA", "Sodium", Section, Serum, Plain, "2951-2", Numeric("mmol/L", 0, [All(136, 145, 120, 160)], "NA", delta: 8m));
        yield return new("K", "Potassium", Section, Serum, Plain, "2823-3",
            Numeric("mmol/L", 1, [Ages(0, 30, 3.7m, 5.9m, 2.5m, 7.0m), Ages(30, null, 3.5m, 5.1m, 2.5m, 6.5m)], "K", delta: 20m));
        yield return new("CL", "Chloride", Section, Serum, Plain, "2075-0", Numeric("mmol/L", 0, [All(98, 107, 80, 120)], "CL"));
    }

    private static IEnumerable<OrderableSeed> BiochemistryTests()
    {
        const LabSection Section = LabSection.Biochemistry;
        const string Glucose = "Hexokinase";

        // Fasting and post-prandial glucose share the glucose assay and therefore its QC (QC group GLU).
        yield return new OrderableSeed(
            "LAB-FBS", "Glucose, fasting (FBS)", "FBS", 80m, Section, SpecimenType.Plasma, ContainerType.Fluoride, 2, Glucose, "1558-6",
            Preparation: "Overnight fast of 8–12 hours; water is allowed.")
        {
            Result = Numeric("mg/dL", 0,
            [
                Ages(0, 30, 45, 90, 40, 300),
                Ages(30, null, 70, 100, 50, 450, "70 - 100 (normal); 100 - 125 (prediabetes); >= 126 (diabetes)"),
            ], "GLU", qcGroup: "GLU"),
        };

        yield return new OrderableSeed(
            "LAB-PPBS", "Glucose, post-prandial (2 hours)", "PPBS", 80m, Section, SpecimenType.Plasma, ContainerType.Fluoride, 2, Glucose, "1521-4",
            Preparation: "Collect exactly 2 hours after the start of a meal.")
        {
            Result = Numeric("mg/dL", 0,
                [Ages(30, null, 70, 140, 50, 450, "< 140 (normal); 140 - 199 (prediabetes); >= 200 (diabetes)")], "GLU", qcGroup: "GLU"),
            DrawSeparately = true,
        };

        yield return new OrderableSeed(
            "LAB-HBA1C", "Glycated haemoglobin (HbA1c)", "HbA1c", 550m, Section, SpecimenType.WholeBlood, ContainerType.Edta, 6,
            "HPLC (NGSP certified)", "4548-4",
            Interpretation: "Reflects average glycaemia over the previous 2–3 months; unreliable with haemoglobin variants, recent transfusion or haemolysis.")
        {
            Result = Numeric("%", 1, [All(4.0m, 5.6m, text: "Non-diabetic: < 5.7; Prediabetes: 5.7 - 6.4; Diabetes: >= 6.5")], "HBA1C"),
        };

        yield return new OrderableSeed(
            "LAB-LIPID", "Lipid profile", "LIPID", 600m, Section, SpecimenType.Serum, ContainerType.Plain, 6,
            "Enzymatic colorimetric (CHOD-PAP, GPO-PAP); direct HDL", null,
            Preparation: "10–12 hours fasting; avoid alcohol for 24 hours.",
            Interpretation: "LDL is calculated by the Friedewald formula when triglycerides are below 400 mg/dL.")
        {
            Components = [("CHOL", null), ("TG", null), ("HDL", null), ("LDL", null), ("VLDL", null)],
        };

        yield return new OrderableSeed(
            "LAB-LFT", "Liver function test (LFT)", "LFT", 650m, Section, SpecimenType.Serum, ContainerType.Plain, 6,
            "Automated clinical chemistry (IFCC methods)", null)
        {
            Components =
            [
                ("TBIL", "Bilirubin"), ("DBIL", "Bilirubin"), ("IBIL", "Bilirubin"), ("AST", "Enzymes"), ("ALT", "Enzymes"), ("ALP", "Enzymes"),
                ("GGT", "Enzymes"), ("TP", "Proteins"), ("ALB", "Proteins"), ("GLOB", "Proteins"), ("AGR", "Proteins"),
            ],
        };

        yield return new OrderableSeed(
            "LAB-KFT", "Kidney function test (KFT / RFT)", "KFT", 700m, Section, SpecimenType.Serum, ContainerType.Plain, 6,
            "Automated clinical chemistry; electrolytes by indirect ISE", null)
        {
            Components = [("UREA", null), ("CREA", null), ("UA", null), ("NA", "Electrolytes"), ("K", "Electrolytes"), ("CL", "Electrolytes")],
        };

        yield return new OrderableSeed(
            "LAB-ELECTROLYTES", "Serum electrolytes (Na, K, Cl)", "ELECTROLYTES", 450m, Section, SpecimenType.Serum, ContainerType.Plain, 2,
            "Indirect ion-selective electrode", null,
            Preparation: "Avoid haemolysis and prolonged tourniquet; separate serum within an hour (potassium rises in stored blood).")
        {
            Components = [("NA", null), ("K", null), ("CL", null)],
        };

        yield return new OrderableSeed(
            "LAB-CRP", "C-reactive protein (CRP), quantitative", "CRP", 500m, Section, SpecimenType.Serum, ContainerType.Plain, 4,
            "Immunoturbidimetry", "1988-5")
        {
            Result = Numeric("mg/L", 1, [All(null, 5.0m)], "CRP"),
        };
    }
}
