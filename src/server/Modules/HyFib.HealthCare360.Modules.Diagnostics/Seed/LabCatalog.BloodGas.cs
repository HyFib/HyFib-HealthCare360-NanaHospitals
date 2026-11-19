using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory.Rules;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

/// <summary>
/// The arterial blood gas (Nana Hospitals, N8): pH, pCO2, pO2, bicarbonate, base excess and oxygen saturation, read on a
/// blood gas analyser that is not interfaced, so entered by hand (each analyte has the channel a blood gas analyser
/// names it by, should one ever be interfaced); the FiO2 the patient was breathing, entered by a person (the result sheet
/// offers 21 %, room air, to confirm, and HC360 never assumes it); and the P/F ratio and the alveolar-arterial gradient,
/// which HC360 calculates from them (<see cref="ResultFormulas"/>).
/// </summary>
internal static partial class LabCatalog
{
    private static IEnumerable<AnalyteSeed> BloodGasAnalytes()
    {
        const LabSection Section = LabSection.Biochemistry;
        const SpecimenType Arterial = SpecimenType.ArterialBlood;
        const ContainerType Syringe = ContainerType.Heparin;
        var adult = Years(18);

        // Adult arterial intervals: pH 7.35 - 7.45, pO2 80 - 100 mmHg, pCO2 35 - 45 mmHg, HCO3 22 - 26 mmol/L and base excess
        // -2 to +2 mmol/L (Habib T, Nair A, Murphy S, Saeed H, Ishaya N, "Mastering blood gas interpretation: a practical
        // guide for primary care providers", S Afr Fam Pract 2025;67(1):e1-e7, doi:10.4102/safp.v67i1.6058, table 1).
        // Critical limits: pH below 7.21 or above 7.59, pCO2 below 19 or above 67 mmHg, pO2 below 43 mmHg (Kost GJ, "Critical
        // limits for urgent clinician notification at US medical centers", JAMA 1990;263(5):704-707). The oxygen saturation's
        // 95 - 100 % is the interval commonly printed: the sources above give none. Defaults the laboratory reviews; a
        // child's results are read against no interval.
        yield return new("ABG-PH", "pH", Section, Arterial, Syringe, "2744-1",
            Numeric(null, 2, [Ages(adult, null, 7.35m, 7.45m, 7.21m, 7.59m)], "pH"));
        yield return new("ABG-PCO2", "pCO2", Section, Arterial, Syringe, "2019-8",
            Numeric("mmHg", 1, [Ages(adult, null, 35m, 45m, 19m, 67m, "35 - 45")], "pCO2"));
        yield return new("ABG-HCO3", "Bicarbonate (HCO3, actual)", Section, Arterial, Syringe, "1960-4",
            Numeric("mmol/L", 1, [Ages(adult, null, 22m, 26m, text: "22 - 26")], "HCO3"));
        yield return new("ABG-BE", "Base excess (BE)", Section, Arterial, Syringe, "1925-7",
            Numeric("mmol/L", 1, [Ages(adult, null, -2m, 2m, text: "-2 to +2")], "BE"));
        yield return new("ABG-PO2", "pO2", Section, Arterial, Syringe, "2703-7",
            Numeric("mmHg", 1, [Ages(adult, null, 80m, 100m, 43m, text: "80 - 100 (breathing room air)")], "pO2"));
        yield return new("ABG-SO2", "Oxygen saturation (sO2)", Section, Arterial, Syringe, "2708-6",
            Numeric("%", 1, [Ages(adult, null, 95m, 100m, text: "95 - 100")], "sO2"));

        // The FiO2 is what the patient breathed, not a measurement: no interval, and a P/F ratio or gradient only from one a
        // person entered (21 % on room air, up to 100 %).
        yield return new("ABG-FIO2", "FiO2 (inspired oxygen)", Section, Arterial, Syringe, "3150-0", Numeric("%", 0, [], "FiO2"));

        // P/F ratio: 300 or less is the Berlin definition's acute respiratory distress syndrome (mild 201 - 300, moderate
        // 101 - 200, severe 100 or less, with PEEP or CPAP of 5 cmH2O or more: The ARDS Definition Task Force, "Acute
        // respiratory distress syndrome: the Berlin Definition", JAMA 2012;307(23):2526-2533, doi:10.1001/jama.2012.5669).
        // Calculated and rounded to a whole number, so 300 flags low.
        yield return new("ABG-PF", "P/F ratio (pO2/FiO2)", Section, Arterial, Syringe, null,
            Numeric("mmHg", 0, [Ages(adult, null, 301m, null, text: "> 300 (Berlin definition of ARDS: 201 - 300 mild, 101 - 200 moderate, 100 or less severe)")]));

        // Alveolar-arterial gradient: 10 - 15 mmHg is normal (Sood P, Paul G, Puri S, "Interpretation of arterial blood gas",
        // Indian J Crit Care Med 2010;14(2):57-64, doi:10.4103/0972-5229.68215) on room air; it is wider with age and on oxygen.
        yield return new("ABG-AAGRAD", "A-a gradient (alveolar-arterial O2)", Section, Arterial, Syringe, null,
            Numeric("mmHg", 0, [Ages(adult, null, null, 15m, text: "Up to 15 on room air (wider with age, and on oxygen)")]));
    }

    private static IEnumerable<OrderableSeed> BloodGasTests()
    {
        // Drawn on its own: each blood gas is a syringe of its own moment, and of the FiO2 the patient breathed then. The
        // handling follows Sood et al. (above). The tariff is a default the hospital reviews.
        yield return new OrderableSeed(
            "LAB-ABG", "Arterial blood gas (ABG)", "ABG", 1200m, LabSection.Biochemistry, SpecimenType.ArterialBlood, ContainerType.Heparin, 1,
            "Blood gas analyser; P/F ratio and A-a gradient calculated by HC360 from the FiO2 entered", "24336-0",
            Preparation: "Draw arterial blood without air into a heparinised blood-gas syringe; expel any bubble at once, cap it, and mix by inverting and rolling. Do not cool it; analyse within 30 minutes. Write the FiO2 the patient was breathing on the request (room air: 21 %).",
            Interpretation: "Oxygenation is read against the FiO2 entered for this sample: HC360 calculates the P/F ratio and the A-a gradient from it, never from an assumed room air, and the gradient with the laboratory's barometric pressure.")
        {
            Components =
            [
                ("ABG-PH", "Acid-base"), ("ABG-PCO2", "Acid-base"), ("ABG-HCO3", "Acid-base"), ("ABG-BE", "Acid-base"),
                ("ABG-PO2", "Oxygenation"), ("ABG-SO2", "Oxygenation"), ("ABG-FIO2", "Oxygenation"), ("ABG-PF", "Oxygenation"),
                ("ABG-AAGRAD", "Oxygenation"),
            ],
            DrawSeparately = true,
        };
    }
}
