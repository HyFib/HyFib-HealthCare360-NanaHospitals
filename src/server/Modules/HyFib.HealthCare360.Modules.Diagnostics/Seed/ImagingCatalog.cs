using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

namespace HyFib.HealthCare360.Modules.Diagnostics.Seed;

internal sealed record ImagingProcedureSeed(
    string Code, string Name, string ShortName, decimal Price, Modality Modality, string BodyPart, int DurationMinutes, bool RequiresContrast, string? Preparation);

internal sealed record CardiologyServiceSeed(string Code, string Name, string ShortName, decimal Price);

/// <summary>
/// Seeded radiology procedures and cardiology services with 2026 INR tariffs of a tier-2 city private hospital. The
/// <c>RAD-*</c> and <c>CARD-*</c> codes are relied on by other modules (order sets, health-check packages).
/// </summary>
internal static class ImagingCatalog
{
    private const string MriSafety =
        "Remove all metal objects. Tell the staff about a pacemaker, cochlear implant, aneurysm clips, metal fragments, pregnancy or claustrophobia.";

    public static IReadOnlyList<ImagingProcedureSeed> Procedures { get; } =
    [
        new("RAD-XR-CHEST", "X-ray chest PA view", "XR CHEST PA", 400m, Modality.DX, "Chest", 10, false,
            "Remove jewellery and metal objects from the chest; tell the staff if you may be pregnant."),
        new("RAD-XR-SPINE-LS", "X-ray lumbosacral spine AP and lateral", "XR LS SPINE", 700m, Modality.DX, "Lumbosacral spine", 15, false,
            "Remove metal objects around the waist; tell the staff if you may be pregnant."),
        new("RAD-USG-ABD", "Ultrasound whole abdomen", "USG ABDOMEN", 1200m, Modality.US, "Abdomen", 20, false,
            "Fast for 6 hours. Drink 4–5 glasses of water an hour before the scan and do not pass urine (full bladder)."),
        new("RAD-USG-PELVIS", "Ultrasound pelvis", "USG PELVIS", 1000m, Modality.US, "Pelvis", 20, false,
            "Full bladder: drink 4–5 glasses of water an hour before the scan and do not pass urine."),
        new("RAD-CT-HEAD", "CT brain (plain)", "CT BRAIN", 3000m, Modality.CT, "Head", 15, false,
            "Remove hair pins, earrings and other metal objects."),
        new("RAD-CT-ABD", "CT abdomen and pelvis with contrast (CECT)", "CECT ABD", 6500m, Modality.CT, "Abdomen", 30, true,
            "Fast for 4–6 hours. A recent serum creatinine is required. Tell the staff about allergies, asthma, diabetes medicines (metformin) or pregnancy."),
        new("RAD-MRI-BRAIN", "MRI brain (plain)", "MRI BRAIN", 7500m, Modality.MR, "Brain", 45, false, MriSafety),
        new("RAD-MRI-SPINE", "MRI lumbosacral spine (plain)", "MRI LS SPINE", 8000m, Modality.MR, "Lumbosacral spine", 45, false, MriSafety),
        new("RAD-MAMMO", "Mammography (bilateral)", "MAMMO", 2000m, Modality.MG, "Breast", 20, false,
            "Do not apply deodorant, powder or lotion on the day of the test; preferably one week after the menstrual period."),
    ];

    public static IReadOnlyList<CardiologyServiceSeed> Cardiology { get; } =
    [
        new("CARD-ECG", "Electrocardiogram (12-lead ECG)", "ECG", 250m),
        new("CARD-ECHO", "2D echocardiography with colour Doppler", "2D ECHO", 2000m),
        new("CARD-TMT", "Treadmill test (TMT / exercise stress test)", "TMT", 2000m),
        new("CARD-HOLTER", "Holter monitoring (24 hours)", "HOLTER", 3000m),
        new("CARD-ABPM", "Ambulatory blood pressure monitoring (24 hours)", "ABPM", 2500m),
    ];
}
