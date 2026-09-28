using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Tests;

public sealed class BankTransferFileTests
{
    [Fact]
    public void Writes_a_header_and_one_line_per_payee_with_invariant_amounts()
    {
        var csv = BankTransferFile.BuildCsv(
        [
            new BankTransferRow("NHEMP00001", "Deepa Raman", "001234567890", "SBIN0001234", 27_100m, "SALARY SEP 2026"),
            new BankTransferRow("NHEMP00002", "Suresh Babu", "98765432101", "HDFC0000123", 18_450.5m, "SALARY SEP 2026"),
        ]);

        csv.ShouldBe(
            "EmployeeCode,BeneficiaryName,AccountNumber,IFSC,Amount,Narration\r\n"
            + "NHEMP00001,Deepa Raman,001234567890,SBIN0001234,27100.00,SALARY SEP 2026\r\n"
            + "NHEMP00002,Suresh Babu,98765432101,HDFC0000123,18450.50,SALARY SEP 2026\r\n");
    }

    [Fact]
    public void Quotes_fields_with_commas_or_quotes()
    {
        BankTransferFile.Escape("Rao, K \"Jr\"").ShouldBe("\"Rao, K \"\"Jr\"\"\"");
        BankTransferFile.Escape("Plain").ShouldBe("Plain");
        BankTransferFile.Escape(null).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")")]
    [InlineData("+91")]
    [InlineData("-5")]
    [InlineData("@SUM(A1)")]
    public void Neutralises_spreadsheet_formulas(string value) =>
        BankTransferFile.Escape(value).TrimStart('"').ShouldStartWith("'");

    [Fact]
    public void Narration_names_the_salary_month()
    {
        BankTransferFile.Narration(2026, 9).ShouldBe("SALARY SEP 2026");
        BankTransferFile.Narration(2027, 1).ShouldBe("SALARY JAN 2027");
    }
}
