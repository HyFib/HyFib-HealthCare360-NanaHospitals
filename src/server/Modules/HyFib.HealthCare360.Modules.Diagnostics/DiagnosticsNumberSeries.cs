using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Diagnostics;

/// <summary>
/// The numbers the Diagnostics module issues (administrators set their formats under Hospital setup). Scanners and
/// analysers read these numbers, so their formats are fixed; their next numbers can still be raised.
/// </summary>
public static class DiagnosticsNumberSeries
{
    public static NumberSeriesDefinition LabAccession { get; } =
        new("lab.accession", "Lab accession number", "Laboratory", "{FAC}LB{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24)
        {
            LockedReason = "Barcode scanners on the laboratory screens recognise accession numbers by their shape, so the format cannot change.",
        };

    /// <summary>Tube barcodes: numeric and unique across facilities, because analysers look samples up by barcode only.</summary>
    public static NumberSeriesDefinition LabSample { get; } =
        new("lab.sample", "Sample tube barcode", "Laboratory", "{SEQ:9}", NumberResetPolicy.Never, MaxLength: 20)
        {
            PerFacility = false,
            LockedReason = "Laboratory analysers and barcode scanners read tube barcodes as plain 9-digit numbers, so the format cannot change.",
        };

    public static NumberSeriesDefinition RadiologyAccession { get; } =
        new("radiology.accession", "Radiology accession number", "Radiology", "{FAC}RD{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24)
        {
            LockedReason = "Barcode scanners on the diagnostics screens recognise radiology accession numbers by their shape, so the format cannot change.",
        };

    public static NumberSeriesDefinition CardiologyAccession { get; } =
        new("cardiology.accession", "Cardiology accession number", "Cardiology", "{FAC}CD{YY}{SEQ:7}", NumberResetPolicy.Yearly, MaxLength: 24)
        {
            LockedReason = "Barcode scanners on the diagnostics screens recognise cardiology accession numbers by their shape, so the format cannot change.",
        };

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [LabAccession, LabSample, RadiologyAccession, CardiologyAccession];
}
