namespace HyFib.HealthCare360.Integrations.Labels;

// What each kind of label shows. The staff app sends the same data it puts on its own printed (HTML) label, so a label
// looks the same whichever way it is printed. Text is shown as given; codes and numbers are not looked up here.

/// <summary>A sample tube label (LIS-02/03): who, which sample, which tube and tests, and when it was drawn.</summary>
/// <param name="Barcode">The sample barcode the lab scans at receipt.</param>
/// <param name="Tube">Short tube name, for example "EDTA".</param>
/// <param name="Bench">Short bench code, for example "HAEM".</param>
public sealed record LabTubeLabel(
    string Barcode,
    string PatientName,
    string Uhid,
    string? AgeSex,
    string? AccessionNumber,
    bool Stat,
    string? Tube,
    string? Bench,
    IReadOnlyList<string>? Tests,
    DateTimeOffset? CollectedAt);

/// <summary>An in-patient's wristband (FO-03): who, the UHID to scan, the admission and bed, and an allergy mark.</summary>
/// <param name="Allergies">What the chart says about allergies, which decides the band's allergy mark.</param>
public sealed record WristbandLabel(
    string Uhid,
    string PatientName,
    string? AgeSex,
    string? AdmissionNumber,
    string? Ward,
    string? Bed,
    WristbandAllergies Allergies);

/// <summary>
/// What the chart says about allergies, as the wristband marks it. The names match the chart's allergy recording status,
/// so the staff app passes it through as it reads it.
/// </summary>
public enum WristbandAllergies
{
    /// <summary>Active allergies are on the chart: a black "ALLERGY" mark.</summary>
    Recorded = 0,

    /// <summary>A clinician recorded "no known allergies": no mark.</summary>
    NoKnownAllergies,

    /// <summary>Nobody has recorded allergies or their absence: an outlined "ALLERGIES NOT RECORDED" mark.</summary>
    NotRecorded,
}

/// <summary>How the UHID is coded on a wristband: Code 128 for line scanners, QR for 2D scanners.</summary>
public enum WristbandCode
{
    Code128 = 0,
    QrCode,
}

/// <summary>A medicine label from the pharmacy (PH-01): who, what and how much, how to take it, batch and expiry.</summary>
/// <param name="Quantity">As the pharmacist reads it, for example "10 tab".</param>
public sealed record DispensingLabel(
    string PatientName,
    string Medicine,
    string Quantity,
    string? Directions,
    string? Batch,
    DateOnly? Expiry,
    DateOnly? DispensedOn,
    string? Hospital);

/// <summary>A blood unit label (BB-03): group, component and volume, the unit number to scan, expiry and screening.</summary>
/// <param name="BloodGroup">As the blood bank writes it, for example "O+" or "AB−".</param>
/// <param name="Component">Short component name, for example "PRBC".</param>
/// <param name="Cleared">Screening is complete and non-reactive. Otherwise the label says the unit is not for issue.</param>
public sealed record BloodUnitLabel(
    string UnitNumber,
    string BloodGroup,
    string Component,
    int? VolumeMl,
    bool Cleared,
    DateTimeOffset? ExpiresAt,
    string? Storage);

/// <summary>A sterile pack label (CSSD-04): the set, the pack number to scan, the method and the use-by date.</summary>
/// <param name="SterileUntil">Null until the load is released: the label then says the pack is not released.</param>
public sealed record CssdPackLabel(
    string PackNumber,
    string SetName,
    string? Method,
    DateOnly? SterileUntil);

/// <summary>An asset tag (AST-01): the hospital, the asset and its tag number to scan.</summary>
public sealed record AssetTagLabel(
    string AssetTag,
    string AssetName,
    string? Hospital);

/// <summary>
/// A pharmacy tag (INT-BAR) for a strip, bottle, box or shelf: the medicine, its batch, expiry and MRP, and the barcode
/// the counter scans. The barcode is the item code and batch number (<c>PCM500#AB1234</c>), or the item code alone.
/// </summary>
/// <param name="Unit">What the MRP is for, as stock is counted (<c>tab</c>): "MRP Rs 2.50/tab".</param>
public sealed record PharmacyTagLabel(
    string Barcode,
    string Medicine,
    string? Batch,
    DateOnly? Expiry,
    decimal? Mrp,
    string? Unit = null);
