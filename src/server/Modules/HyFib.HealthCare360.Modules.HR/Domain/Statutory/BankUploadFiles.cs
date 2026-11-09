using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

public enum BankFileFormat
{
    Csv = 0,
    Sbi,
    Hdfc,
    Icici,
}

/// <summary>
/// Salary bulk-upload files in the layouts of SBI, HDFC Bank and ICICI Bank (<see cref="StatutoryLayouts"/>), besides the
/// generic CSV (<see cref="BankTransferFile"/>). A payee at the same bank as the hospital's salary account is paid by
/// internal transfer, everyone else by NEFT. Text fields are made safe for spreadsheets and stripped of the separator.
/// </summary>
public static class BankUploadFiles
{
    public static string Build(BankFileFormat format, IReadOnlyList<BankTransferRow> rows, string? debitAccount, DateOnly valueDate)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return format switch
        {
            BankFileFormat.Sbi => Sbi(rows, Clean(debitAccount), valueDate),
            BankFileFormat.Hdfc => Hdfc(rows, valueDate),
            BankFileFormat.Icici => Icici(rows, Clean(debitAccount), valueDate),
            _ => BankTransferFile.BuildCsv(rows),
        };
    }

    /// <summary>The bank prefix of an IFSC: SBIN, HDFC, ICIC.</summary>
    public static bool SameBank(string ifsc, string bankCode) => HrIdentifiers.Normalize(ifsc).StartsWith(bankCode, StringComparison.Ordinal);

    private static string Sbi(IReadOnlyList<BankTransferRow> rows, string debit, DateOnly valueDate)
    {
        var layout = StatutoryLayouts.SbiCmp;
        var text = new StringBuilder(string.Join(layout.Separator, layout.Fields)).Append("\r\n");
        foreach (var r in rows)
        {
            text.Append(layout.Line(new Dictionary<string, string>
            {
                ["Beneficiary Name"] = Text(r.BeneficiaryName, '|', 35),
                ["Beneficiary Account No"] = HrIdentifiers.Normalize(r.AccountNumber),
                ["IFSC"] = HrIdentifiers.Normalize(r.Ifsc),
                ["Transaction Type"] = SameBank(r.Ifsc, "SBIN") ? "DCR" : "NEFT",
                ["Amount"] = Amount(r.Amount),
                ["Debit Account No"] = debit,
                ["Narration"] = Text(r.Narration, '|', 30),
                ["Value Date"] = valueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            })).Append("\r\n");
        }

        return text.ToString();
    }

    private static string Hdfc(IReadOnlyList<BankTransferRow> rows, DateOnly valueDate)
    {
        var layout = StatutoryLayouts.HdfcEnet;
        var text = new StringBuilder();
        foreach (var r in rows)
        {
            text.Append(layout.Line(new Dictionary<string, string>
            {
                ["Transaction Type"] = SameBank(r.Ifsc, "HDFC") ? "I" : "N",
                ["Beneficiary Code"] = Text(r.EmployeeCode, ',', 13),
                ["Beneficiary Account Number"] = HrIdentifiers.Normalize(r.AccountNumber),
                ["Instrument Amount"] = Amount(r.Amount),
                ["Beneficiary Name"] = Text(r.BeneficiaryName, ',', 40),
                ["Customer Reference Number"] = Text(r.EmployeeCode, ',', 20),
                ["Payment Details 1"] = Text(r.Narration, ',', 30),
                ["Chq/Trn Date"] = valueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                ["IFSC Code"] = HrIdentifiers.Normalize(r.Ifsc),
            })).Append("\r\n");
        }

        return text.ToString();
    }

    private static string Icici(IReadOnlyList<BankTransferRow> rows, string debit, DateOnly valueDate)
    {
        var layout = StatutoryLayouts.IciciCib;
        var text = new StringBuilder(string.Join(layout.Separator, layout.Fields)).Append("\r\n");
        foreach (var r in rows)
        {
            text.Append(layout.Line(new Dictionary<string, string>
            {
                ["Debit Ac No"] = debit,
                ["Beneficiary Ac No"] = HrIdentifiers.Normalize(r.AccountNumber),
                ["Beneficiary Name"] = Text(r.BeneficiaryName, ',', 32),
                ["Amt"] = Amount(r.Amount),
                ["Pay Mod"] = SameBank(r.Ifsc, "ICIC") ? "I" : "N",
                ["Date"] = valueDate.ToString("dd-MMM-yyyy", CultureInfo.InvariantCulture),
                ["IFSC"] = HrIdentifiers.Normalize(r.Ifsc),
                ["Remarks"] = Text(r.Narration, ',', 30),
            })).Append("\r\n");
        }

        return text.ToString();
    }

    private static string Amount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Clean(string? account) => string.IsNullOrWhiteSpace(account) ? string.Empty : HrIdentifiers.Normalize(account);

    /// <summary>Letters, digits, spaces and a few marks; never the separator, a quote or a leading formula character.</summary>
    public static string Text(string? value, char separator, int max)
    {
        var kept = new string([.. (value ?? string.Empty).Select(c => c == separator || c is '"' or '\r' or '\n' or '\t' ? ' ' : c)]).Trim();
        kept = kept.TrimStart('=', '+', '-', '@').Trim();
        return kept.Length > max ? kept[..max] : kept;
    }
}
