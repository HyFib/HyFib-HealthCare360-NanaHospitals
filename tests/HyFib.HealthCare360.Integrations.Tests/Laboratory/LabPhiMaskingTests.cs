using HyFib.HealthCare360.Integrations.Laboratory;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class LabPhiMaskingTests
{
    [Fact]
    public void Removes_names_birth_dates_addresses_and_phones_from_hl7_pid()
    {
        var masked = LabPhiMasking.Mask(LabSamples.HematologyOru, LabProtocol.Hl7v2);

        var pid = masked.Split('\r').Single(s => s.StartsWith("PID", StringComparison.Ordinal));
        pid.ShouldBe("PID|1||********2345||***||***|M|||***||***");
        masked.ShouldNotContain("SHARMA");
        masked.ShouldNotContain("19800115");
        masked.ShouldNotContain("MG Road");
        masked.ShouldNotContain("9876543210");
    }

    [Fact]
    public void Keeps_sample_ids_and_results_for_troubleshooting()
    {
        var masked = LabPhiMasking.MaskHl7(LabSamples.HematologyOru);

        masked.ShouldContain("OBR|1|ORD-77|SMP-26090042|CBC^Complete blood count|||20260925102500");
        masked.ShouldContain("OBX|2|NM|HGB^Hemoglobin||10.2|g/dL|12.0-16.0|L|||F");
        masked.ShouldStartWith(@"MSH|^~\&|BC-5380");
    }

    [Fact]
    public void Masks_next_of_kin_and_guarantor_segments()
    {
        var masked = LabPhiMasking.MaskHl7(@"MSH|^~\&|A|B|C|D|20260925||ORU^R01|1|P|2.5.1" + "\rNK1|1|SHARMA^PRIYA|SPO|12 MG Road|9876500000\rGT1|1||SHARMA^RAVI||12 MG Road|9876543210");

        masked.ShouldNotContain("PRIYA");
        masked.ShouldNotContain("9876500000");
        masked.ShouldNotContain("RAVI");
        masked.ShouldContain("NK1|1|***|SPO|***|***");
    }

    [Fact]
    public void Honours_custom_hl7_field_separators() =>
        LabPhiMasking.MaskHl7("MSH#^~\\&#A\rPID#1##UHID00012345##DOE^JOHN").ShouldEndWith("PID#1##********2345##***");

    [Fact]
    public void Removes_identity_from_astm_patient_records()
    {
        var masked = LabPhiMasking.Mask(string.Join("\r", LabSamples.AstmResultRecords), LabProtocol.Astm);

        var patient = masked.Split('\r').Single(r => r.StartsWith('P'));
        patient.ShouldBe("P|1||********2345||***||***|M||***||***");
        masked.ShouldContain("R|1|^^^GLU|98|mg/dL||N||F||||20260925103500");
        masked.ShouldContain("O|1|SMP-26090042^01^0012");
    }

    [Fact]
    public void Uses_the_astm_delimiters_declared_by_the_header() =>
        LabPhiMasking.MaskAstm("H!@#$\rP!1!!UHID00012345!!DOE#JOHN!!19700101!F").ShouldEndWith("P!1!!********2345!!***!!***!F");

    [Fact]
    public void Short_identifiers_are_fully_hidden() =>
        LabPhiMasking.MaskAstm("H|\\^&\rP|1||1234").ShouldEndWith("P|1||****");
}
