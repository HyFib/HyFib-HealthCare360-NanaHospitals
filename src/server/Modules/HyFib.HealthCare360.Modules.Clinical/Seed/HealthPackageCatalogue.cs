using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Seed;

/// <summary>
/// Starter master health checkup packages (MHC-01). Items reference the service codes seeded by Diagnostics (LAB-*, RAD-*,
/// CARD-*) and the platform consultation service (CONS-NEW); prices are indicative INR list prices for the hospital to
/// revise. Item rules keep tests appropriate: mammography from 40 years, PSA for men from 50.
/// </summary>
internal static class HealthPackageCatalogue
{
    internal sealed record ItemSeed(string ServiceCode, PackageGender Gender = PackageGender.Any, int? MinAgeYears = null, string? ConsultationDepartment = null)
    {
        public bool IsConsultation => ConsultationDepartment is not null;
    }

    internal sealed record PackageSeed(string Code, string Name, string Description, PackageGender Gender, int? MinAgeYears, int? MaxAgeYears, decimal Price,
        ItemSeed[] Items);

    private static ItemSeed Consultation(string department) => new("CONS-NEW", ConsultationDepartment: department);

    public static IReadOnlyList<PackageSeed> Packages { get; } =
    [
        new("HC-BASIC", "Basic Health Checkup", "Essential screening: blood counts, sugar, lipids, kidney function, urine, chest X-ray, ECG and a physician consultation.",
            PackageGender.Any, 18, null, 1499m,
            [new("LAB-CBC"), new("LAB-FBS"), new("LAB-LIPID"), new("LAB-KFT"), new("LAB-URINE-RE"), new("RAD-XR-CHEST"), new("CARD-ECG"), Consultation("GM")]),
        new("HC-EXEC", "Executive Health Checkup", "Comprehensive annual screening for working adults, including liver, thyroid, abdominal ultrasound and treadmill test.",
            PackageGender.Any, 18, null, 4999m,
            [new("LAB-CBC"), new("LAB-ESR"), new("LAB-FBS"), new("LAB-PPBS"), new("LAB-HBA1C"), new("LAB-LIPID"), new("LAB-LFT"), new("LAB-KFT"), new("LAB-TFT"),
             new("LAB-URINE-RE"), new("RAD-XR-CHEST"), new("RAD-USG-ABD"), new("CARD-ECG"), new("CARD-TMT"), Consultation("GM")]),
        new("HC-WOMEN", "Women's Wellness Checkup", "Screening for women: anaemia, thyroid, vitamin D and B12, diabetes, pelvic ultrasound, mammography from 40, gynaecology consultation.",
            PackageGender.Female, 18, null, 3999m,
            [new("LAB-CBC"), new("LAB-FBS"), new("LAB-TFT"), new("LAB-VITD"), new("LAB-VITB12"), new("LAB-LIPID"), new("LAB-URINE-RE"), new("RAD-USG-PELVIS"),
             new("RAD-MAMMO", PackageGender.Female, 40), Consultation("OBG"), Consultation("GM")]),
        new("HC-SENIOR", "Senior Citizen Health Checkup", "For adults aged 60 and above: metabolic, kidney, liver, thyroid, electrolytes, vitamins, cardiac and abdominal evaluation; PSA for men.",
            PackageGender.Any, 60, null, 5999m,
            [new("LAB-CBC"), new("LAB-ESR"), new("LAB-FBS"), new("LAB-PPBS"), new("LAB-HBA1C"), new("LAB-LIPID"), new("LAB-LFT"), new("LAB-KFT"),
             new("LAB-ELECTROLYTES"), new("LAB-TFT"), new("LAB-VITD"), new("LAB-VITB12"), new("LAB-URINE-RE"), new("LAB-PSA", PackageGender.Male, 50),
             new("RAD-XR-CHEST"), new("RAD-USG-ABD"), new("CARD-ECG"), new("CARD-ECHO"), Consultation("GM")]),
        new("HC-DIABETES", "Diabetes Care Checkup", "Diabetes control and complication screen: glucose, HbA1c, lipids, kidney function, electrolytes, urine, ECG.",
            PackageGender.Any, 18, null, 2499m,
            [new("LAB-FBS"), new("LAB-PPBS"), new("LAB-HBA1C"), new("LAB-LIPID"), new("LAB-KFT"), new("LAB-ELECTROLYTES"), new("LAB-URINE-RE"), new("CARD-ECG"),
             Consultation("GM")]),
        new("HC-CARDIAC", "Cardiac Risk Assessment", "Heart risk evaluation: lipids, glucose, HbA1c, CRP, kidney function, ECG, echocardiography, treadmill test and cardiology consultation.",
            PackageGender.Any, 25, null, 6999m,
            [new("LAB-CBC"), new("LAB-FBS"), new("LAB-HBA1C"), new("LAB-LIPID"), new("LAB-CRP"), new("LAB-KFT"), new("LAB-ELECTROLYTES"), new("RAD-XR-CHEST"),
             new("CARD-ECG"), new("CARD-ECHO"), new("CARD-TMT"), Consultation("CARD")]),
    ];
}
