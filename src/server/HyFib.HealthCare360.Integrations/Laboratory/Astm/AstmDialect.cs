using System.Text;
using HyFib.HealthCare360.Platform.Contracts;

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

    /// <summary>Stored name of <see cref="YumizenH500"/>.</summary>
    public const string YumizenH500Name = "YumizenH500";

    /// <summary>
    /// The Yumizen H500's DIF parameters (manual §3.4.1.1): a sample with any of them pending is ordered as DIF, which
    /// also measures the CBC.
    /// </summary>
    private static readonly HashSet<string> H500DifCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "DIF", "LYM#", "LYM%", "MON#", "MON%", "NEU#", "NEU%", "EOS#", "EOS%", "BAS#", "BAS%", "IMG#", "IMG%",
        "IMM#", "IMM%", "IML#", "IML%", "ALY#", "ALY%", "LIC#", "LIC%",
    };

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

    /// <summary>
    /// HORIBA Yumizen H500 / H500E (OT and CT; haematology), from HORIBA's "Output Format for Host Connection"
    /// (RAA085BEN, software 4.0.x), ASTM over RS-232 (38400 8N1 by default) or the network:
    /// <list type="bullet">
    /// <item>It is ordered by panel, not by parameter: O.5 is <c>^^^CBC</c>, <c>^^^DIF</c> (which includes the CBC) or
    /// <c>^^^ESR</c>, and an order naming anything else is refused. HC360 maps the channels it has pending (WBC, HGB,
    /// LYM#, ESR…) to those panels; results still come one parameter per R record.</item>
    /// <item>It takes orders only in answer to its own query: an order sent unasked is ignored, so none are sent. A sample
    /// HC360 has nothing for is answered with an empty order (<c>O|1|id||^^^|…|Y</c>) rather than left pending.</item>
    /// <item>The answer's H.5 is a copy of the query's H.10; H.13 is <c>LIS2-A2</c>; O.6 is R or S only; O.16 is
    /// <c>BLOOD</c>; the name component is at most 20 characters.</item>
    /// <item>QC and technician runs (H.12 <c>Q</c> or <c>D</c>, O.16 <c>CONTROL</c>) are not patient results and are
    /// not stored as such; <c>---</c> (no result) and <c>+++</c> (beyond the range) in R.4 are results not obtained.</item>
    /// <item>Text is UTF-8, with control characters escaped as <c>&amp;Xhhhh&amp;</c>.</item>
    /// </list>
    /// </summary>
    public static AstmDialect YumizenH500 { get; } = new()
    {
        Name = YumizenH500Name,
        MaxPatientNameLength = 20,
        SpecimenRequired = true,
        SpecimenOverride = "BLOOD",
        TerminatorAlwaysNormal = true,
        HeaderVersion = "LIS2-A2",
        HeaderSenderEchoesQuery = true,
        OrdersByPanel = true,
        UrgentAsStat = true,
        AnswersUnknownSamples = true,
        SendsOrdersUnasked = false,
        PatientResultsOnly = true,
        DashValuesNotObtained = true,
        DefaultEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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

    /// <summary>A query answer's H.5 (sender) is a copy of the query's H.10 (receiver), and HC360 names itself nowhere else.</summary>
    public bool HeaderSenderEchoesQuery { get; private init; }

    /// <summary>O.16 whatever the tube's specimen (the H500 knows only BLOOD for a patient sample).</summary>
    public string? SpecimenOverride { get; private init; }

    /// <summary>O.5 names the analyser's panels (CBC, DIF, ESR), not the channels (<see cref="OrderedTests"/>).</summary>
    public bool OrdersByPanel { get; private init; }

    /// <summary>O.6 knows only R and S: urgent samples go as S.</summary>
    public bool UrgentAsStat { get; private init; }

    /// <summary>A queried sample with nothing to run is answered with an empty order of report type Y.</summary>
    public bool AnswersUnknownSamples { get; private init; }

    /// <summary>Orders may be sent without the analyser asking (order download).</summary>
    public bool SendsOrdersUnasked { get; private init; } = true;

    /// <summary>Only H.12 <c>P</c> (or empty) messages carry patient results; QC (<c>Q</c>), technician (<c>D</c>) and control samples are not stored.</summary>
    public bool PatientResultsOnly { get; private init; }

    /// <summary>R.4 <c>---</c> and <c>+++</c> are results not obtained, not values.</summary>
    public bool DashValuesNotObtained { get; private init; }

    /// <summary>The encoding when the connection names none (otherwise Latin-1).</summary>
    public Encoding? DefaultEncoding { get; private init; }

    public static IReadOnlyList<string> Names { get; } = [YumizenC150Name, YumizenH500Name];

    /// <summary>A dialect name a connection may store: none (E1394) or a known one.</summary>
    public static bool IsKnown(string? name) => string.IsNullOrEmpty(name) || Names.Contains(name, StringComparer.Ordinal);

    /// <summary>The analyser's dialect; E1394 for none, or for a name this version does not know.</summary>
    public static AstmDialect For(string? name) => name switch
    {
        YumizenC150Name => YumizenC150,
        YumizenH500Name => YumizenH500,
        _ => Standard,
    };

    /// <summary>
    /// The tests O.5 names: the channels as they are, or, for an analyser ordered by panel, ESR when ESR is pending,
    /// and DIF when a differential parameter is pending, else CBC for any other channel.
    /// </summary>
    public IReadOnlyList<string> OrderedTests(IReadOnlyList<string> channelCodes)
    {
        if (!OrdersByPanel)
        {
            return channelCodes;
        }

        var codes = channelCodes.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
        var panels = new List<string>();
        var esr = codes.Any(c => string.Equals(c, "ESR", StringComparison.OrdinalIgnoreCase));
        var blood = codes.Where(c => !string.Equals(c, "ESR", StringComparison.OrdinalIgnoreCase)).ToList();
        if (blood.Any(H500DifCodes.Contains))
        {
            panels.Add("DIF");
        }
        else if (blood.Count > 0)
        {
            panels.Add("CBC");
        }

        if (esr)
        {
            panels.Add("ESR");
        }

        return panels;
    }

    /// <summary>O.6: S (stat), A (as soon as possible, or S where the analyser knows no A) or R (routine).</summary>
    public string Priority(OrderPriority priority) => priority switch
    {
        OrderPriority.Stat => "S",
        OrderPriority.Urgent => UrgentAsStat ? "S" : "A",
        _ => "R",
    };

    /// <summary>Whether a message with this H.12 processing id carries patient results to store.</summary>
    public bool CarriesPatientResults(string? processingId) =>
        !PatientResultsOnly || string.IsNullOrWhiteSpace(processingId) || processingId.Trim().Equals("P", StringComparison.OrdinalIgnoreCase);

    /// <summary>The delimiters of a message the analyser sent, its header's with this dialect's repeat delimiter.</summary>
    public AstmDelimiters Incoming(AstmDelimiters fromHeader) =>
        RepeatDelimiter is { } repeat ? fromHeader with { Repeat = repeat } : fromHeader;

    /// <summary>The delimiters HC360 writes to this analyser.</summary>
    public AstmDelimiters Outgoing => RepeatDelimiter is { } repeat ? AstmDelimiters.Default with { Repeat = repeat } : AstmDelimiters.Default;

    /// <summary>O.16 for the analyser: the C150 names Blood, Urine, Serum, Plasma and CSF.</summary>
    public string? Specimen(string? specimenType)
    {
        if (SpecimenOverride is not null)
        {
            return SpecimenOverride;
        }

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
