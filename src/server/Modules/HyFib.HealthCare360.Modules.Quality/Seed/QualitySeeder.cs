using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Seed;

/// <summary>
/// Seeds the core NABH (5th edition) quality indicators so the quality team starts with a standard catalogue.
/// Idempotent by code; targets are common starting points and are expected to be tuned by the quality committee.
/// </summary>
internal sealed class QualitySeeder : ISeeder
{
    public int Order => 120;

    private static readonly (string Code, string Name, string Category, string Numerator, string Denominator, IndicatorUnit Unit, decimal? Target, IndicatorDirection Direction, string Nabh)[] Indicators =
    [
        ("OPD-IA-TIME", "Time taken for initial assessment of OPD patients", "Patient care",
            "Sum of minutes from registration to the doctor's initial assessment", "Number of OPD patients assessed", IndicatorUnit.Minutes, 20, IndicatorDirection.LowerIsBetter, "AAC"),
        ("IPD-IA-TIME", "Time taken for initial assessment of in-patients", "Patient care",
            "Sum of minutes from admission to completed initial assessment", "Number of admissions assessed", IndicatorUnit.Minutes, 60, IndicatorDirection.LowerIsBetter, "AAC"),
        ("OPD-WAIT", "OPD waiting time", "Patient care",
            "Sum of minutes from registration to consultation", "Number of OPD consultations", IndicatorUnit.Minutes, 30, IndicatorDirection.LowerIsBetter, "AAC"),
        ("LAB-TAT", "Laboratory reports within turnaround time", "Patient care",
            "Reports released within the defined turnaround time", "Reports released", IndicatorUnit.Percent, 95, IndicatorDirection.HigherIsBetter, "AAC"),
        ("MED-ERR", "Medication error rate", "Medication safety",
            "Number of medication errors", "Number of medication charts reviewed", IndicatorUnit.Percent, 1, IndicatorDirection.LowerIsBetter, "MOM"),
        ("ADR-RATE", "Adverse drug reaction rate", "Medication safety",
            "In-patients who developed an adverse drug reaction", "Number of in-patients", IndicatorUnit.Percent, 0.5m, IndicatorDirection.LowerIsBetter, "MOM"),
        ("HAND-HYG", "Hand hygiene compliance rate", "Infection control",
            "Hand-hygiene opportunities complied with", "Hand-hygiene opportunities observed", IndicatorUnit.Percent, 90, IndicatorDirection.HigherIsBetter, "HIC"),
        ("NSI-RATE", "Needle-stick injury rate", "Infection control",
            "Needle-stick injuries reported", "Healthcare workers at risk", IndicatorUnit.Percent, 0, IndicatorDirection.LowerIsBetter, "HIC"),
        ("SSI-RATE", "Surgical site infection rate", "Infection control",
            "Surgical site infections", "Surgeries performed", IndicatorUnit.Percent, 1, IndicatorDirection.LowerIsBetter, "HIC"),
        ("CLABSI", "Central line-associated bloodstream infection rate", "Infection control",
            "Central line-associated bloodstream infections", "Central-line days", IndicatorUnit.PerThousand, 2, IndicatorDirection.LowerIsBetter, "HIC"),
        ("VAP", "Ventilator-associated pneumonia rate", "Infection control",
            "Ventilator-associated pneumonias", "Ventilator days", IndicatorUnit.PerThousand, 5, IndicatorDirection.LowerIsBetter, "HIC"),
        ("CAUTI", "Catheter-associated urinary tract infection rate", "Infection control",
            "Catheter-associated urinary tract infections", "Urinary-catheter days", IndicatorUnit.PerThousand, 3, IndicatorDirection.LowerIsBetter, "HIC"),
        ("FALL-RATE", "Patient fall rate", "Patient safety",
            "Patient falls", "In-patient days", IndicatorUnit.PerThousand, 1, IndicatorDirection.LowerIsBetter, "PSQ"),
        ("PRESSURE-ULCER", "Pressure ulcer incidence", "Patient safety",
            "New pressure ulcers developed after admission", "In-patient days", IndicatorUnit.PerThousand, 1, IndicatorDirection.LowerIsBetter, "COP"),
        ("SENTINEL-RCA", "Sentinel events analysed within 30 days", "Patient safety",
            "Sentinel events with RCA completed within 30 days", "Sentinel events", IndicatorUnit.Percent, 100, IndicatorDirection.HigherIsBetter, "PSQ"),
        ("RTN-OT-48", "Unplanned return to OT within 48 hours", "Surgical care",
            "Unplanned returns to the OT within 48 hours", "Surgeries performed", IndicatorUnit.Percent, 1, IndicatorDirection.LowerIsBetter, "COP"),
        ("REINTUB-48", "Re-intubation rate within 48 hours", "Surgical care",
            "Patients re-intubated within 48 hours of extubation", "Extubations", IndicatorUnit.Percent, 5, IndicatorDirection.LowerIsBetter, "COP"),
        ("TRANSF-RXN", "Transfusion reaction rate", "Surgical care",
            "Transfusion reactions", "Units transfused", IndicatorUnit.Percent, 0.5m, IndicatorDirection.LowerIsBetter, "COP"),
        ("MORTALITY", "Hospital mortality rate", "Outcomes",
            "Deaths", "Discharges including deaths", IndicatorUnit.Percent, 2, IndicatorDirection.LowerIsBetter, "COP"),
        ("DISCH-TAT", "Discharge turnaround time", "Operations",
            "Sum of minutes from discharge advice to the patient leaving", "Discharges", IndicatorUnit.Minutes, 180, IndicatorDirection.LowerIsBetter, "AAC"),
        ("BED-OCC", "Bed occupancy rate", "Operations",
            "In-patient days", "Available bed-days", IndicatorUnit.Percent, 80, IndicatorDirection.HigherIsBetter, "ROM"),
        ("PT-SAT", "Patient satisfaction index", "Patient experience",
            "Patients rating overall experience 4 or 5 out of 5", "Patients surveyed", IndicatorUnit.Percent, 90, IndicatorDirection.HigherIsBetter, "PRE"),
    ];

    public async Task SeedAsync(HospitalDbContext db, CancellationToken cancellationToken)
    {
        var existing = await db.Set<QualityIndicator>().Select(i => i.Code).ToListAsync(cancellationToken);
        var order = 0;
        foreach (var i in Indicators)
        {
            order += 10;
            if (existing.Contains(i.Code))
            {
                continue;
            }

            db.Add(new QualityIndicator
            {
                Code = i.Code,
                Name = i.Name,
                Category = i.Category,
                NumeratorDefinition = i.Numerator,
                DenominatorDefinition = i.Denominator,
                Unit = i.Unit,
                Multiplier = IndicatorMath.DefaultMultiplier(i.Unit),
                Target = i.Target,
                Direction = i.Direction,
                Frequency = IndicatorFrequency.Monthly,
                NabhReference = i.Nabh,
                SortOrder = order,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
