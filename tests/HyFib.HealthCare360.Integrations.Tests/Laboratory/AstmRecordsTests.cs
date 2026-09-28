using HyFib.HealthCare360.Integrations.Laboratory.Astm;

namespace HyFib.HealthCare360.Integrations.Tests.Laboratory;

public sealed class AstmRecordsTests
{
    [Fact]
    public void Parses_records_with_the_header_delimiters()
    {
        var records = AstmRecords.Parse(string.Join("\r", LabSamples.AstmResultRecords));

        records.Select(r => r.Type).ShouldBe(['H', 'P', 'O', 'R', 'R', 'R', 'C', 'L']);
        records[0].Delimiters.ShouldBe(AstmDelimiters.Default);
        records[0].Field(14).ShouldBe("20260925103000");
        records[3].Field(4).ShouldBe("98");
    }

    [Fact]
    public void Reads_components_of_the_universal_test_id()
    {
        var result = AstmRecords.Parse("R|1|^^^GLU^1|98|mg/dL||N||F")[0];

        result.Components(3).ShouldBe(["", "", "", "GLU", "1"]);
        result.Component(3, 4).ShouldBe("GLU");
        result.Component(3, 9).ShouldBeEmpty();
        result.Field(30).ShouldBeEmpty();
    }

    [Fact]
    public void Splits_repeats_but_never_the_header_delimiter_definition()
    {
        var records = AstmRecords.Parse("H|\\^&\rR|1|^^^WBC|7.8|10*3/uL||H\\LL||F");

        records[0].Repeats(2).ShouldBe(["\\^&"]);
        records[1].Repeats(7).ShouldBe(["H", "LL"]);
    }

    [Fact]
    public void Honours_non_default_delimiters_declared_by_the_header()
    {
        var records = AstmRecords.Parse("H!@#$\rR!1!###GLU!98!mg/dL");

        records[0].Delimiters.ShouldBe(new AstmDelimiters('!', '@', '#', '$'));
        records[1].Component(3, 4).ShouldBe("GLU");
        records[1].Value(4).ShouldBe("98");
    }

    [Fact]
    public void Ignores_blank_lines_and_line_noise()
    {
        var records = AstmRecords.Parse("\r\n  \rH|\\^&\r\u0002garbage\r\nL|1|N\r");

        records.Select(r => r.Type).ShouldBe(['H', 'L']);
    }

    [Fact]
    public void Unescapes_delimiters_in_values()
    {
        var record = AstmRecords.Parse("H|\\^&\rC|1|I|A&F&B&S&C&R&D&E&E")[1];

        record.Value(4).ShouldBe("A|B^C\\D&E");
    }

    [Fact]
    public void Escapes_free_text_and_drops_line_breaks() =>
        AstmDelimiters.Default.EscapeText("A|B^C\\D&E\r\nF").ShouldBe("A&F&B&S&C&R&D&E&EF");

    [Fact]
    public void Omits_trailing_empty_fields_when_building_records() =>
        AstmRecords.Build(AstmDelimiters.Default, "L", "1", null, null).ShouldBe("L|1");

    [Theory]
    [InlineData(null)]
    [InlineData("P|1")]
    [InlineData("H|")]
    public void Falls_back_to_the_standard_delimiters(string? header) =>
        AstmDelimiters.FromHeader(header).ShouldBe(AstmDelimiters.Default);
}
