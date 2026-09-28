using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Modules.HR.Domain;

public sealed record BankTransferRow(string EmployeeCode, string BeneficiaryName, string AccountNumber, string Ifsc, decimal Amount, string Narration);

/// <summary>
/// Salary bulk-transfer file (CSV) for upload to the hospital's corporate banking portal. The file contains decrypted
/// account numbers, so it is generated on demand, never stored, and every download is audited. Cells that a
/// spreadsheet would treat as formulas are neutralised (CSV injection).
/// </summary>
public static class BankTransferFile
{
    public const string Header = "EmployeeCode,BeneficiaryName,AccountNumber,IFSC,Amount,Narration";

    public static string BuildCsv(IEnumerable<BankTransferRow> rows)
    {
        var csv = new StringBuilder();
        csv.Append(Header).Append("\r\n");
        foreach (var row in rows)
        {
            csv.Append(Escape(row.EmployeeCode)).Append(',')
                .Append(Escape(row.BeneficiaryName)).Append(',')
                .Append(Escape(row.AccountNumber)).Append(',')
                .Append(Escape(row.Ifsc)).Append(',')
                .Append(row.Amount.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                .Append(Escape(row.Narration)).Append("\r\n");
        }

        return csv.ToString();
    }

    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    public static string Narration(int year, int month) =>
        "SALARY " + new DateOnly(year, month, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
}
