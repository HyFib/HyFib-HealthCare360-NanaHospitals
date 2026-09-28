using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class PayrollInputCsvParserTests
{
    [Fact]
    public void Parses_a_canteen_statement_with_header_and_quoted_descriptions()
    {
        var result = PayrollInputCsvParser.Parse(
            "employeeCode,componentCode,amount,description\r\n"
            + "nhemp00001,canteen,850.5,\"Canteen, September\"\r\n"
            + "NHEMP00002,CANTEEN,1200,\n");

        result.Errors.ShouldBeEmpty();
        result.Lines.Count.ShouldBe(2);
        result.Lines[0].ShouldBe(new PayrollInputCsvLine(2, "NHEMP00001", "CANTEEN", 850.5m, "Canteen, September"));
        result.Lines[1].Description.ShouldBeNull();
    }

    [Fact]
    public void The_header_row_is_optional()
    {
        var result = PayrollInputCsvParser.Parse("NHEMP00003,ADVANCE,5000");

        result.Errors.ShouldBeEmpty();
        result.Lines.ShouldHaveSingleItem().LineNumber.ShouldBe(1);
    }

    [Fact]
    public void Reports_each_bad_line_with_its_number_and_keeps_the_good_ones()
    {
        var result = PayrollInputCsvParser.Parse(
            "NHEMP00001,CANTEEN,100\n"
            + "NHEMP00002,CANTEEN\n"
            + "NHEMP00003,CANTEEN,abc\n"
            + "NHEMP00004,CANT EEN,50\n"
            + "NHEMP00005,CANTEEN,-20\n");

        result.Lines.ShouldHaveSingleItem().EmployeeCode.ShouldBe("NHEMP00001");
        result.Errors.Count.ShouldBe(4);
        result.Errors.ShouldContain(e => e.StartsWith("Line 2:", StringComparison.Ordinal));
        result.Errors.ShouldContain(e => e.StartsWith("Line 3:", StringComparison.Ordinal));
        result.Errors.ShouldContain(e => e.StartsWith("Line 4:", StringComparison.Ordinal));
        result.Errors.ShouldContain(e => e.StartsWith("Line 5:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("employeeCode,componentCode,amount\n")]
    public void Empty_files_are_an_error(string? csv) => PayrollInputCsvParser.Parse(csv).Errors.ShouldNotBeEmpty();

    [Fact]
    public void Refuses_oversized_imports()
    {
        var csv = string.Join('\n', Enumerable.Repeat("NHEMP00001,CANTEEN,1", PayrollInputCsvParser.MaxLines + 5));

        PayrollInputCsvParser.Parse(csv).Errors.ShouldHaveSingleItem().ShouldContain("at most");
    }

    [Fact]
    public void Splits_rfc4180_fields_with_doubled_quotes()
    {
        PayrollInputCsvParser.SplitCsvLine("a,\"b, \"\"c\"\"\",d").ShouldBe(["a", "b, \"c\"", "d"]);
    }
}
