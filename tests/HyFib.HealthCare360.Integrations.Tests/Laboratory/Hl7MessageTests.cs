using HyFib.HealthCare360.Integrations.Laboratory.Hl7;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class Hl7MessageTests
{
    [Fact]
    public void Reads_the_header_fields()
    {
        Hl7Message.TryParse(LabSamples.HematologyOru, out var message, out var error).ShouldBeTrue(error);

        message.MessageType.ShouldBe("ORU");
        message.TriggerEvent.ShouldBe("R01");
        message.ControlId.ShouldBe("MSG00042");
        message.Version.ShouldBe("2.5.1");
        message.Header.Field(1).ShouldBe("|");
        message.Header.Field(2).ShouldBe(@"^~\&");
        message.Header.Field(3).ShouldBe("BC-5380");
        message.Segments.Select(s => s.Id).ShouldBe(["MSH", "PID", "PV1", "OBR", "OBX", "OBX", "OBX", "OBX", "OBX"]);
    }

    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Accepts_any_segment_terminator(string terminator)
    {
        var text = LabSamples.HematologyOru.TrimEnd('\r').Replace("\r", terminator, StringComparison.Ordinal);

        Hl7Message.TryParse(text, out var message, out _).ShouldBeTrue();
        message.Segments.Count.ShouldBe(9);
    }

    [Fact]
    public void Uses_the_delimiters_declared_in_msh()
    {
        const string text = "MSH#^~\\&#LAB#HOSP#HC360#NANA#20260925##ORU^R01#C1#P#2.3.1\rOBX#1#NM#GLU^Glucose##98#mg/dL";

        Hl7Message.TryParse(text, out var message, out _).ShouldBeTrue();

        message.Encoding.Field.ShouldBe('#');
        message.ControlId.ShouldBe("C1");
        message.Segments[1].Component(3, 2).ShouldBe("Glucose");
        message.Segments[1].Value(5).ShouldBe("98");
    }

    [Theory]
    [InlineData("", "The message does not start with an MSH segment.")]
    [InlineData("PID|1||123", "The message does not start with an MSH segment.")]
    [InlineData("MSH|^|LAB", "MSH-2 does not define the HL7 encoding characters.")]
    public void Rejects_messages_without_a_valid_header(string text, string expected)
    {
        Hl7Message.TryParse(text, out var message, out var error).ShouldBeFalse();

        message.ShouldBeNull();
        error.ShouldBe(expected);
    }

    [Fact]
    public void Reads_components_repetitions_and_subcomponents()
    {
        var segment = new Hl7Segment(@"PID|1||UHID1^^^HC360&1.2.3&ISO^MR~AADHAAR9^^^UIDAI||SHARMA^RAVI", Hl7Encoding.Default);

        segment.Id.ShouldBe("PID");
        segment.Repetitions(3).Count.ShouldBe(2);
        segment.Component(3, 1).ShouldBe("UHID1");
        segment.Component(3, 4).ShouldBe("HC360");
        segment.Component(3, 1, repetition: 1).ShouldBe("AADHAAR9");
        segment.Component(5, 2).ShouldBe("RAVI");
        segment.Component(5, 9).ShouldBeEmpty();
        segment.Field(40).ShouldBeEmpty();
        segment.Repetitions(2).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"a\F\b", "a|b")]
    [InlineData(@"a\S\b\T\c\R\d\E\e", @"a^b&c~d\e")]
    [InlineData(@"line1\.br\line2", "line1\nline2")]
    [InlineData(@"\X41424344\", "ABCD")]
    [InlineData(@"\H\bold\N\", "bold")]
    [InlineData(@"trailing \F", @"trailing \F")]
    public void Decodes_escape_sequences(string raw, string expected) =>
        Hl7Encoding.Default.Decode(raw).ShouldBe(expected);

    [Fact]
    public void Encodes_delimiters_and_line_breaks_in_free_text() =>
        Hl7Encoding.Default.Encode("a|b^c&d~e\\f\r\ng").ShouldBe(@"a\F\b\S\c\T\d\R\e\E\f  g");
}
