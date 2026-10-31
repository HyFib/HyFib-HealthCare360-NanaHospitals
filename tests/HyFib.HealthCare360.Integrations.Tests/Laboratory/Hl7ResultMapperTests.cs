using HyFib.HealthCare360.Integrations.Laboratory.Hl7;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class Hl7ResultMapperTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static Hl7Message Parse(string text)
    {
        Hl7Message.TryParse(text, out var message, out var error).ShouldBeTrue(error);
        return message;
    }

    private static Hl7MappingResult Map(string text, IReadOnlyList<string>? sampleIdFields = null) =>
        Hl7ResultMapper.Map(Parse(text), "BC5380", sampleIdFields, text, LabSamples.ReceivedAt, LabSamples.India);

    [Fact]
    public void Maps_an_oru_message_to_one_result_message_per_sample()
    {
        var mapped = Map(LabSamples.HematologyOru);

        mapped.Error.ShouldBeNull();
        var message = mapped.Messages.ShouldHaveSingleItem();
        message.InstrumentCode.ShouldBe("BC5380");
        message.SampleBarcode.ShouldBe("SMP-26090042");
        message.RawMessage.ShouldBe(LabSamples.HematologyOru);
        message.ReceivedAt.ShouldBe(LabSamples.ReceivedAt);
        message.Results.Select(r => r.ChannelCode).ShouldBe(["WBC", "HGB", "RBC-MORPH"]);
    }

    [Fact]
    public void Reads_value_units_flags_status_and_observation_time()
    {
        var results = Map(LabSamples.HematologyOru).Messages[0].Results;

        results[0].ShouldBe(new InstrumentResult("WBC", "7.8", "10*3/uL", "N", new DateTimeOffset(2026, 9, 25, 10, 29, 0, Ist)) { Status = "F" });
        // No OBX-14: the order's observation time (OBR-7) applies.
        results[1].ShouldBe(new InstrumentResult("HGB", "10.2", "g/dL", "L", new DateTimeOffset(2026, 9, 25, 10, 25, 0, Ist)) { Status = "F" });
        results[2].Value.ShouldBe("Normocytic & normochromic");
        results[2].Units.ShouldBeNull();
        results[2].Flags.ShouldBeNull();
    }

    [Fact]
    public void Skips_encapsulated_data_and_results_that_could_not_be_obtained()
    {
        var codes = Map(LabSamples.HematologyOru).Messages[0].Results.Select(r => r.ChannelCode);

        codes.ShouldNotContain("HIST");
        codes.ShouldNotContain("PLT");
    }

    [Fact]
    public void Oul_messages_inherit_the_specimen_declared_before_the_orders()
    {
        var message = Map(LabSamples.ChemistryOul).Messages.ShouldHaveSingleItem();

        message.SampleBarcode.ShouldBe("SMP-OUL-1");
        message.Results.Select(r => (r.ChannelCode, r.Value)).ShouldBe([("GLU", "98"), ("CRP", "<5")]);
        // No OBX-14 or OBR-7: the message time (MSH-7) applies.
        message.Results[0].ResultedAt.ShouldBe(new DateTimeOffset(2026, 9, 25, 11, 0, 0, Ist));
    }

    [Fact]
    public void Oru_specimen_segments_after_an_order_belong_to_that_order()
    {
        var text = string.Join('\r',
            @"MSH|^~\&|XN|Sysmex|HC360|Nana|20260925||ORU^R01|M1|P|2.5.1",
            "OBR|1||ORDER-SIDE-ID",
            "SPM|1|SPM-BARCODE-9||BLD",
            "OBX|1|NM|WBC||6.1|10*3/uL|||||F");

        Map(text).Messages.ShouldHaveSingleItem().SampleBarcode.ShouldBe("SPM-BARCODE-9");
    }

    [Fact]
    public void Splits_results_by_sample_and_merges_repeated_samples()
    {
        var text = string.Join('\r',
            @"MSH|^~\&|AU480|Beckman|HC360|Nana|20260925||ORU^R01|M2|P|2.3.1",
            "OBR|1||S-1",
            "OBX|1|NM|GLU||98|mg/dL|||||F",
            "OBR|2||S-2",
            "OBX|1|NM|GLU||120|mg/dL|||||F",
            "OBR|3||S-1",
            "OBX|1|NM|CREA||1.1|mg/dL|||||F");

        var messages = Map(text).Messages;

        messages.Select(m => m.SampleBarcode).ShouldBe(["S-1", "S-2"]);
        messages[0].Results.Select(r => r.ChannelCode).ShouldBe(["GLU", "CREA"]);
    }

    [Fact]
    public void Configured_sample_id_fields_take_precedence() =>
        Map(LabSamples.HematologyOru, ["OBR-2"]).Messages.ShouldHaveSingleItem().SampleBarcode.ShouldBe("ORD-77");

    [Fact]
    public void Coded_and_structured_numeric_values_are_rendered_as_text()
    {
        var text = string.Join('\r',
            @"MSH|^~\&|LAB|X|HC360|Nana|20260925||ORU^R01|M3|P|2.5.1",
            "OBR|1||S-9",
            "OBX|1|CE|BLDGRP^Blood group||B+^B Positive^L||||||F",
            "OBX|2|SN|WIDAL^Widal titre||^1^:^160||||||F",
            "OBX|3|SN|HCV^HCV viral load||>^1000000|IU/mL|||||F",
            "OBX|4|ST|COMMENT||Line one~Line two||||||F");

        Map(text).Messages[0].Results.Select(r => r.Value).ShouldBe(["B Positive", "1:160", ">1000000", "Line one; Line two"]);
    }

    [Fact]
    public void Messages_without_results_are_errors() =>
        Map(string.Join('\r', @"MSH|^~\&|LAB|X|HC360|Nana|20260925||ORU^R01|M4|P|2.5.1", "OBR|1||S-1")).Error
            .ShouldBe("The message contains no OBX results.");

    [Fact]
    public void Results_without_a_sample_id_are_kept_under_no_sample_id()
    {
        var text = string.Join('\r',
            @"MSH|^~\&|LAB|X|HC360|Nana|20260925||ORU^R01|M5|P|2.5.1",
            "OBR|1",
            "OBX|1|NM|GLU||98|mg/dL|||||F",
            "OBR|2||S-1",
            "OBX|1|NM|CREA||1.1|mg/dL|||||F");

        var mapped = Map(text);

        mapped.Error.ShouldBeNull();
        mapped.Messages.Select(m => (m.SampleBarcode, m.Results.ShouldHaveSingleItem().ChannelCode)).ShouldBe([("", "GLU"), ("S-1", "CREA")]);
    }

    [Fact]
    public void A_preliminary_result_keeps_its_status_and_the_notes_after_it_are_its_comment()
    {
        var text = string.Join('\r',
            @"MSH|^~\&|LAB|X|HC360|Nana|20260925||ORU^R01|M8|P|2.5.1",
            "OBR|1||S-1",
            "NTE|1|L|Order note from the ward",
            "OBX|1|NM|GLU||98|mg/dL||N|||P",
            "NTE|1|I|Lipaemic sample~index 2",
            "NTE|2|I|Rerun advised",
            "OBX|2|NM|CREA||1.1|mg/dL|||||F",
            "SPM|1|S-1",
            "NTE|1|I|Not a result note");

        var results = Map(text).Messages.ShouldHaveSingleItem().Results;

        results.Select(r => (r.ChannelCode, r.Status, r.Comment)).ShouldBe(
            [("GLU", "P", "Lipaemic sample index 2 Rerun advised"), ("CREA", "F", null)]);
    }

    [Fact]
    public void Deleted_or_corrected_away_results_leave_nothing_to_store() =>
        Map(string.Join('\r', @"MSH|^~\&|LAB|X|HC360|Nana|20260925||ORU^R01|M6|P|2.5.1", "OBR|1||S-1", "OBX|1|NM|GLU||98|mg/dL|||||D")).Error
            .ShouldBe("The message contains no usable results.");

    [Theory]
    [InlineData("ORU^R01", true)]
    [InlineData("ORU^R30", true)]
    [InlineData("OUL^R22", true)]
    [InlineData("ADT^A01", false)]
    [InlineData("QRY^Q02", false)]
    [InlineData("ORU^R02", false)]
    public void Recognises_result_messages(string type, bool expected) =>
        Hl7ResultMapper.IsResultMessage(Parse($@"MSH|^~\&|LAB|X|HC360|Nana|20260925||{type}|M7|P|2.5.1")).ShouldBe(expected);
}
