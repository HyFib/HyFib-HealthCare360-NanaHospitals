using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

/// <summary>The deductor and the person responsible, as the batch header needs them.</summary>
public sealed record TdsDeductor(
    string Tan, string Pan, string Name, string? Branch, IReadOnlyList<string> Address, string StateCode, string Pin, string? Email, string? Phone,
    string DeductorType, string ResponsibleName, string ResponsibleDesignation, string? ResponsiblePan, string? ResponsibleMobile, string? ResponsibleEmail);

/// <summary>One deduction from one employee or payee.</summary>
public sealed record TdsDeduction(
    string Name, string? Pan, decimal AmountPaid, DateOnly PaidOn, DateOnly DeductedOn, decimal Tds, decimal? RatePercent, string SectionCode, string? Remark,
    string DeducteeCode = "02");

/// <summary>One challan (ITNS 281) deposited, with the deductions it pays.</summary>
public sealed record TdsChallanDeposit(string BsrCode, DateOnly DepositedOn, string ChallanSerial, decimal Tax, decimal Surcharge, decimal Cess, decimal Interest,
    decimal Others, decimal LateFee, string MinorHead, IReadOnlyList<TdsDeduction> Deductions)
{
    public decimal Total => Tax + Surcharge + Cess + Interest + Others + LateFee;
}

public sealed record TdsReturnResult(string Text, int Challans, int Deductees, decimal TotalDeposited, decimal TotalDeducted, IReadOnlyList<string> Problems);

/// <summary>
/// The quarterly TDS statement as the input file of Protean's File Validation Utility (FVU), for Form 24Q (salary, s.192)
/// and Form 26Q (other payments: s.194J professional fees). Records: a file header, one batch, then each challan followed
/// by its deductees. PAN missing is written "PANNOTAVBL". The FVU checks the file and makes the .fvu that is uploaded; HC360
/// checks the deposits match the deductions and lists every problem, and the file is only for the FVU when there are none.
/// Annexure II of a fourth-quarter 24Q (salary details, SD records) is not written: it is prepared from Form 16 Part B.
/// </summary>
public static class TdsReturnFile
{
    public const string PanNotAvailable = "PANNOTAVBL";

    public static string Quarter(int quarter) => $"Q{Math.Clamp(quarter, 1, 4)}";

    /// <summary>The months of a quarter of the financial year starting in <paramref name="fyStartYear"/>: Q1 April–June … Q4 January–March.</summary>
    public static (DateOnly From, DateOnly To) QuarterDates(int fyStartYear, int quarter)
    {
        var from = new DateOnly(fyStartYear, 4, 1).AddMonths(3 * (Math.Clamp(quarter, 1, 4) - 1));
        return (from, from.AddMonths(3).AddDays(-1));
    }

    public static TdsReturnResult Build(string form, TdsDeductor deductor, int fyStartYear, int quarter, IReadOnlyList<TdsChallanDeposit> challans, DateOnly createdOn,
        IReadOnlyList<string>? earlierProblems = null)
    {
        ArgumentNullException.ThrowIfNull(deductor);
        ArgumentNullException.ThrowIfNull(challans);
        var problems = new List<string>(earlierProblems ?? []);
        if (!HrIdentifiers.IsValidTan(deductor.Tan))
        {
            problems.Add("The deductor's TAN is missing or not in the form ABCD12345E.");
        }

        if (!HrIdentifiers.IsValidPan(deductor.Pan))
        {
            problems.Add("The deductor's PAN is missing or not valid.");
        }

        foreach (var challan in challans)
        {
            var deducted = challan.Deductions.Sum(d => d.Tds);
            if (challan.Tax + challan.Surcharge + challan.Cess < deducted)
            {
                problems.Add($"Challan {challan.ChallanSerial} of {challan.DepositedOn:dd/MM/yyyy} pays ₹{Money.FormatInr(challan.Tax + challan.Surcharge + challan.Cess)} "
                             + $"of tax but ₹{Money.FormatInr(deducted)} is allocated to it.");
            }
        }

        var line = 1;
        var text = new StringBuilder();
        void Add(FileLayout layout, Dictionary<string, string> values)
        {
            values["Line Number"] = line.ToString(CultureInfo.InvariantCulture);
            text.Append(layout.Line(values)).Append('\n');
            line++;
        }

        var tan = HrIdentifiers.Normalize(deductor.Tan);
        Add(StatutoryLayouts.TdsFileHeader, new()
        {
            ["Record Type"] = "FH",
            ["File Type"] = "NS1",
            ["Upload Type"] = "R",
            ["File Creation Date"] = Date(createdOn),
            ["File Sequence No"] = "1",
            ["Uploader Type"] = "D",
            ["TAN of Deductor"] = tan,
            ["Total No of Batches"] = "1",
            ["Return Preparation Utility"] = "HC360",
        });

        var address = deductor.Address.Concat(Enumerable.Repeat(string.Empty, 5)).Take(5).Select(a => Text(a, 25)).ToList();
        var deposited = challans.Sum(c => c.Total);
        Add(StatutoryLayouts.TdsBatchHeader, new()
        {
            ["Record Type"] = "BH",
            ["Batch Number"] = "1",
            ["Count of Challan Records"] = challans.Count.ToString(CultureInfo.InvariantCulture),
            ["Form Number"] = form,
            ["TAN of Deductor"] = tan,
            ["PAN of Deductor"] = HrIdentifiers.Normalize(deductor.Pan),
            ["Assessment Year"] = $"{fyStartYear + 1}{(fyStartYear + 2) % 100:00}",
            ["Financial Year"] = $"{fyStartYear}{(fyStartYear + 1) % 100:00}",
            ["Period"] = Quarter(quarter),
            ["Name of Deductor"] = Text(deductor.Name, 75),
            ["Deductor Branch"] = Text(deductor.Branch, 75),
            ["Deductor Address 1"] = address[0],
            ["Deductor Address 2"] = address[1],
            ["Deductor Address 3"] = address[2],
            ["Deductor Address 4"] = address[3],
            ["Deductor Address 5"] = address[4],
            ["Deductor State"] = deductor.StateCode,
            ["Deductor PIN"] = deductor.Pin,
            ["Deductor Email"] = deductor.Email ?? string.Empty,
            ["Deductor Phone"] = Digits(deductor.Phone),
            ["Deductor Address Changed"] = "N",
            ["Deductor Type"] = deductor.DeductorType,
            ["Responsible Person Name"] = Text(deductor.ResponsibleName, 75),
            ["Responsible Person Designation"] = Text(deductor.ResponsibleDesignation, 20),
            ["Responsible Address 1"] = address[0],
            ["Responsible Address 2"] = address[1],
            ["Responsible Address 3"] = address[2],
            ["Responsible Address 4"] = address[3],
            ["Responsible Address 5"] = address[4],
            ["Responsible State"] = deductor.StateCode,
            ["Responsible PIN"] = deductor.Pin,
            ["Responsible Email"] = deductor.ResponsibleEmail ?? string.Empty,
            ["Responsible Mobile"] = Digits(deductor.ResponsibleMobile),
            ["Responsible Address Changed"] = "N",
            ["Batch Total of Deposit Amount"] = Amount(deposited),
            ["PAN of Responsible Person"] = deductor.ResponsiblePan is { } rp ? HrIdentifiers.Normalize(rp) : string.Empty,
        });

        var challanNo = 0;
        var deductees = 0;
        foreach (var challan in challans)
        {
            challanNo++;
            var deducted = challan.Deductions.Sum(d => d.Tds);
            Add(StatutoryLayouts.TdsChallanDetail, new()
            {
                ["Record Type"] = "CD",
                ["Batch Number"] = "1",
                ["Challan Record No"] = challanNo.ToString(CultureInfo.InvariantCulture),
                ["Count of Deductee Records"] = challan.Deductions.Count.ToString(CultureInfo.InvariantCulture),
                ["NIL Challan Indicator"] = challan.Total == 0 ? "Y" : "N",
                ["Bank Challan No"] = challan.ChallanSerial,
                ["Bank Branch Code"] = challan.BsrCode,
                ["Challan Date"] = Date(challan.DepositedOn),
                ["Oltas Income Tax"] = Amount(challan.Tax),
                ["Oltas Surcharge"] = Amount(challan.Surcharge),
                ["Oltas Cess"] = Amount(challan.Cess),
                ["Oltas Interest"] = Amount(challan.Interest),
                ["Oltas Others"] = Amount(challan.Others),
                ["Total Deposit Amount"] = Amount(challan.Total),
                ["Total Tax Deposit as per Deductees"] = Amount(deducted),
                ["TDS Income Tax"] = Amount(deducted),
                ["TDS Surcharge"] = Amount(0),
                ["TDS Cess"] = Amount(0),
                ["Sum of TDS"] = Amount(deducted),
                ["TDS Interest"] = Amount(challan.Interest),
                ["TDS Others"] = Amount(challan.Others),
                ["Book Entry Cash"] = "N",
                ["Late Payment Fee"] = Amount(challan.LateFee),
                ["Minor Head"] = challan.MinorHead,
            });

            var deducteeNo = 0;
            foreach (var d in challan.Deductions)
            {
                deducteeNo++;
                deductees++;
                Add(StatutoryLayouts.TdsDeducteeDetail, new()
                {
                    ["Record Type"] = "DD",
                    ["Batch Number"] = "1",
                    ["Challan Record No"] = challanNo.ToString(CultureInfo.InvariantCulture),
                    ["Deductee Record No"] = deducteeNo.ToString(CultureInfo.InvariantCulture),
                    ["Mode"] = "O",
                    ["Deductee Code"] = d.DeducteeCode,
                    ["PAN"] = HrIdentifiers.IsValidPan(d.Pan) ? HrIdentifiers.Normalize(d.Pan!) : PanNotAvailable,
                    ["Name"] = Text(d.Name, 75),
                    ["TDS Income Tax"] = Amount(d.Tds),
                    ["TDS Surcharge"] = Amount(0),
                    ["TDS Cess"] = Amount(0),
                    ["Total TDS"] = Amount(d.Tds),
                    ["Total Tax Deposited"] = Amount(d.Tds),
                    ["Amount Paid"] = Amount(d.AmountPaid),
                    ["Date Paid"] = Date(d.PaidOn),
                    ["Date Deducted"] = d.Tds > 0 ? Date(d.DeductedOn) : string.Empty,
                    ["Date of Deposit"] = Date(challan.DepositedOn),
                    ["Rate"] = d.RatePercent is { } rate ? rate.ToString("0.0000", CultureInfo.InvariantCulture) : string.Empty,
                    ["Book Entry Cash"] = "N",
                    ["Remarks"] = d.Remark ?? string.Empty,
                    ["Section Code"] = d.SectionCode,
                });
            }
        }

        return new TdsReturnResult(text.ToString(), challans.Count, deductees, deposited, challans.Sum(c => c.Deductions.Sum(d => d.Tds)), problems);
    }

    private static string Date(DateOnly date) => date.ToString("ddMMyyyy", CultureInfo.InvariantCulture);

    private static string Amount(decimal amount) => Money.Round(amount).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Digits(string? value) => new([.. (value ?? string.Empty).Where(char.IsAsciiDigit)]);

    /// <summary>Upper case, without the caret separator, at most <paramref name="max"/> characters.</summary>
    public static string Text(string? value, int max)
    {
        var text = new string([.. (value ?? string.Empty).ToUpperInvariant().Select(c => c is '^' or '\r' or '\n' or '\t' ? ' ' : c)]).Trim();
        return text.Length > max ? text[..max] : text;
    }
}
