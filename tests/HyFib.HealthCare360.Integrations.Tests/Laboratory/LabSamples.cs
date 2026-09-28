using System.Text;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

/// <summary>Analyser messages as real instruments send them (segments/records separated by CR), and shared fixtures.</summary>
internal static class LabSamples
{
    /// <summary>Fixed +05:30 zone so the tests do not depend on the machine's time-zone database.</summary>
    public static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("TEST-IST", TimeSpan.FromHours(5.5), "IST", "IST");

    public static readonly DateTimeOffset ReceivedAt = new(2026, 9, 25, 5, 1, 0, TimeSpan.Zero);

    /// <summary>Haematology ORU^R01 (Mindray BC-5380 style): the sample barcode is in OBR-3.</summary>
    public static readonly string HematologyOru = string.Join('\r',
        @"MSH|^~\&|BC-5380|Mindray|HC360|NanaHospitals|20260925103015||ORU^R01^ORU_R01|MSG00042|P|2.5.1||||||UNICODE UTF-8",
        "PID|1||UHID00012345^^^HC360^MR||SHARMA^RAVI||19800115|M|||12 MG Road^^Chennai||9876543210",
        "PV1|1|O",
        "OBR|1|ORD-77|SMP-26090042|CBC^Complete blood count|||20260925102500",
        "OBX|1|NM|WBC^White blood cells||7.8|10*3/uL|4.0-10.0|N|||F|||20260925102900",
        "OBX|2|NM|HGB^Hemoglobin||10.2|g/dL|12.0-16.0|L|||F",
        @"OBX|3|ST|RBC-MORPH^RBC morphology||Normocytic \T\ normochromic||||||F",
        "OBX|4|ED|HIST^WBC histogram||^image^png^Base64^iVBORw0KGgo=||||||F",
        "OBX|5|NM|PLT^Platelets||250|10*3/uL|||||X") + "\r";

    /// <summary>Biochemistry OUL^R22 (Lab Automation profile): SPM/SAC precede the OBRs they apply to.</summary>
    public static readonly string ChemistryOul = string.Join('\r',
        @"MSH|^~\&|cobas|Roche|HC360|NanaHospitals|20260925110000||OUL^R22^OUL_R22|MSG00043|P|2.5.1",
        "PID|1||UHID00012345",
        "SPM|1|SMP-OUL-1||SER",
        "SAC|||CONT-1",
        "OBR|1|||GLU^Glucose",
        "OBX|1|NM|GLU^Glucose||98|mg/dL|70-110|N|||F",
        "OBR|2|||CRP^C-reactive protein",
        "OBX|1|SN|CRP^C-reactive protein||<^5|mg/L|||||F");

    /// <summary>E1394 result message (cobas c 311 style): H, P, O (sample in O.3), R records, a comment and the terminator.</summary>
    public static readonly string[] AstmResultRecords =
    [
        @"H|\^&|||cobas c311^1.0|||||||P|1|20260925103000",
        "P|1||UHID00012345||SHARMA^RAVI||19800115|M||12 MG Road||9876543210",
        @"O|1|SMP-26090042^01^0012||^^^GLU\^^^CREA|R||||||N||||||||||||||F",
        "R|1|^^^GLU|98|mg/dL||N||F||||20260925103500",
        "R|2|^^^CREA|1.4|mg/dL||H||F",
        "R|3|^^^UREA|0|mg/dL||||X",
        "C|1|I|Sample slightly haemolysed|G",
        "L|1|N",
    ];

    /// <summary>E1394 host query for two samples (Q.3 = patient id ^ specimen id, repeated).</summary>
    public static readonly string[] AstmQueryRecords =
    [
        @"H|\^&|||cobas c311^1.0|||||||P|1|20260925103000",
        @"Q|1|^SMP-1\^SMP-2||^^^ALL||||||||O",
        "L|1|N",
    ];

    public static LabInstrumentSettings Hl7Instrument { get; } = new() { Code = "BC5380", Name = "Haematology", Protocol = LabProtocol.Hl7v2, Port = 5100 };

    public static LabInstrumentSettings AstmInstrument { get; } = new() { Code = "C311", Name = "Biochemistry", Protocol = LabProtocol.Astm, Port = 5200 };

    /// <summary>Short ASTM timers so protocol tests run in milliseconds; waits still fail fast if a reply never comes.</summary>
    public static LabRuntimeOptions Options(int maxMessageBytes = 64 * 1024) => new(
        Encoding.Latin1,
        maxMessageBytes,
        TimeSpan.FromSeconds(10),
        India,
        new AstmTimings(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)));
}
