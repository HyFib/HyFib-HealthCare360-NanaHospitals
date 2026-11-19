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

    /// <summary>
    /// Tube numbers: exactly 10 digits with no leading zero, unique across facilities, because analysers look samples up
    /// by this number only. A lab without barcodes writes it on the tube and types it on the analyser as its sample id (the
    /// Yumizen H500 takes 16 characters), so the last digit is a check digit ({CHK}): a number typed with one digit wrong,
    /// or two neighbouring digits swapped, is no tube's number and finds no tube (<see cref="Services.TubeNumbers"/>). The
    /// running number has 9 digits, from 100000000: the first tube is 1000000005.
    /// </summary>
    public static NumberSeriesDefinition LabSample { get; } =
        new("lab.sample", "Sample tube number", "Laboratory", "{SEQ:9}{CHK}", NumberResetPolicy.Never, MaxLength: 20)
        {
            PerFacility = false,
            FirstNumber = 100_000_000,
            LockedReason = "Tube numbers are written on the tubes, typed on the analysers and scanned as plain 10-digit numbers ending in a check digit, which the analysers send back with each result, so the format cannot change.",
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

    /// <summary>PCPNDT Form F, numbered in the order the forms are made, as the register the Appropriate Authority inspects.</summary>
    public static NumberSeriesDefinition FormF { get; } =
        new("radiology.form-f", "PCPNDT Form F", "Radiology", "{FAC}FF{YY}{SEQ:6}", NumberResetPolicy.Yearly, MaxLength: 24);

    internal static IEnumerable<NumberSeriesDefinition> All { get; } = [LabAccession, LabSample, RadiologyAccession, CardiologyAccession, FormF];
}
