namespace HyFib.HealthCare360.Integrations.Laboratory.Astm;

/// <summary>
/// How one analyser model departs from E1394 (<c>astmDialect</c> on its connection). <see cref="Standard"/> is E1394
/// as written, the default; a named dialect holds only what its manufacturer's interface manual, and the analyser's own
/// messages, show it does differently.
/// </summary>
internal sealed record AstmDialect
{
    /// <summary>Stored name of <see cref="YumizenC150"/>.</summary>
    public const string YumizenC150Name = "YumizenC150";

    /// <summary>E1394 as written: delimiters from the header, the sample in O.3, the UHID in P.3.</summary>
    public static AstmDialect Standard { get; } = new() { Name = null };

    /// <summary>
    /// HORIBA Yumizen C150 (clinical chemistry), from HORIBA's "LIS Communication Protocol" for the C150:
    /// <list type="bullet">
    /// <item>It writes <c>H|\^&amp;</c> but repeats with a backtick (<c>^^^GLU`^^^UREA</c>, <c>Q|1|^S1`S2</c>), so the
    /// backtick is the repeat delimiter whatever the header says, both ways.</item>
    /// <item>Its "Patient ID" (P.3) is the id it keys a sample by: it asks for orders by it, and it uploads results as
    /// H, P, R… L with no O record. So HC360 sends the tube's barcode in P.3 and reads it back from there.</item>
    /// <item>Its result records are one field to the right of E1394 from R.7 on (flags R.8, status R.10, completion
    /// time R.14), as the manual's captured frames show (their checksums match); a record in E1394's place is still read.</item>
    /// <item>Dates of birth have 14 digits (an 8-digit one shows "Invalid DateTime"), the name at most 30 characters,
    /// O.16 (specimen: Blood, Urine, Serum, Plasma or CSF) is required, O.26 is <c>O</c> also in a query answer, and the
    /// terminator is always <c>L|1|N</c>.</item>
    /// </list>
    /// </summary>
    public static AstmDialect YumizenC150 { get; } = new()
    {
        Name = YumizenC150Name,
        RepeatDelimiter = '`',
        SampleIdInPatientRecord = true,
        ResultFieldsShifted = true,
        DateOfBirthWithTime = true,
        MaxPatientNameLength = 30,
        SpecimenRequired = true,
        QueryAnswerReportType = "O",
        TerminatorAlwaysNormal = true,
        HeaderVersion = "E 1394-97",
    };

    /// <summary>The name stored on the connection; null for <see cref="Standard"/>.</summary>
    public string? Name { get; private init; }

    /// <summary>The repeat delimiter the analyser uses whatever its header declares; null: the header's.</summary>
    public char? RepeatDelimiter { get; private init; }

    /// <summary>P.3 is the sample id: sent as the tube's barcode, and read back when results come without an O record.</summary>
    public bool SampleIdInPatientRecord { get; private init; }

    /// <summary>Result fields from R.7 on may be one to the right (flags R.8, status R.10, time R.14).</summary>
    public bool ResultFieldsShifted { get; private init; }

    /// <summary>P.8 as <c>YYYYMMDDHHMMSS</c> rather than <c>YYYYMMDD</c>.</summary>
    public bool DateOfBirthWithTime { get; private init; }

    public int? MaxPatientNameLength { get; private init; }

    /// <summary>O.16 is always sent (Serum when HC360 does not know the specimen).</summary>
    public bool SpecimenRequired { get; private init; }

    /// <summary>O.26 in a host-query answer: E1394's <c>Q</c>, or what the analyser expects.</summary>
    public string QueryAnswerReportType { get; private init; } = "Q";

    /// <summary>L.3 is <c>N</c> in every message, also in host-query answers (E1394: <c>F</c>, or <c>I</c> for none).</summary>
    public bool TerminatorAlwaysNormal { get; private init; }

    /// <summary>H.13 in HC360's messages.</summary>
    public string HeaderVersion { get; private init; } = "1";

    public static IReadOnlyList<string> Names { get; } = [YumizenC150Name];

    /// <summary>A dialect name a connection may store: none (E1394) or a known one.</summary>
    public static bool IsKnown(string? name) => string.IsNullOrEmpty(name) || Names.Contains(name, StringComparer.Ordinal);

    /// <summary>The analyser's dialect; E1394 for none, or for a name this version does not know.</summary>
    public static AstmDialect For(string? name) => name switch
    {
        YumizenC150Name => YumizenC150,
        _ => Standard,
    };

    /// <summary>The delimiters of a message the analyser sent, its header's with this dialect's repeat delimiter.</summary>
    public AstmDelimiters Incoming(AstmDelimiters fromHeader) =>
        RepeatDelimiter is { } repeat ? fromHeader with { Repeat = repeat } : fromHeader;

    /// <summary>The delimiters HC360 writes to this analyser.</summary>
    public AstmDelimiters Outgoing => RepeatDelimiter is { } repeat ? AstmDelimiters.Default with { Repeat = repeat } : AstmDelimiters.Default;

    /// <summary>O.16 for the analyser: the C150 names Blood, Urine, Serum, Plasma and CSF.</summary>
    public string? Specimen(string? specimenType)
    {
        if (!SpecimenRequired)
        {
            return specimenType is { Length: > 0 } ? specimenType : null;
        }

        return specimenType?.Trim().ToUpperInvariant() switch
        {
            null or "" or "SERUM" => "SERUM",
            "PLASMA" => "PLASMA",
            "URINE" => "URINE",
            "CSF" => "CSF",
            "WHOLEBLOOD" or "BLOOD" => "BLOOD",
            // Not a specimen the analyser runs: sent as HC360 names it, so the analyser refuses it rather than running it as serum.
            var other => other,
        };
    }
}
