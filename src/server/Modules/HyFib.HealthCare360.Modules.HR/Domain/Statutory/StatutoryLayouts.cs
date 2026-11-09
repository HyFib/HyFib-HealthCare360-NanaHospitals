namespace HyFib.HealthCare360.Modules.HR.Domain.Statutory;

/// <summary>A file layout: its fields in order, the separator, and where it comes from and when it was last checked.</summary>
public sealed record FileLayout(string Name, string Separator, bool HeaderRow, IReadOnlyList<string> Fields, string Source, DateOnly CheckedOn)
{
    public int IndexOf(string field)
    {
        for (var i = 0; i < Fields.Count; i++)
        {
            if (string.Equals(Fields[i], field, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new ArgumentException($"{Name} has no field '{field}'.", nameof(field));
    }

    /// <summary>One line with the named values in their places and every other field empty.</summary>
    public string Line(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var cells = new string[Fields.Count];
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = string.Empty;
        }

        foreach (var (field, value) in values)
        {
            cells[IndexOf(field)] = value;
        }

        return string.Join(Separator, cells);
    }
}

/// <summary>
/// Every statutory and bank file layout HC360 writes, in one place. Formats change: when a portal or bank changes a
/// layout, change it here, with the source and the date, and in docs/modules/statutory-returns.md. The builders refer to
/// fields by name, so a field moving or a new one being added is a change to this file only.
/// </summary>
public static class StatutoryLayouts
{
    /// <summary>EPFO Electronic Challan cum Return, version 2.0 (UAN based), uploaded on the EPFO employer portal.</summary>
    public static FileLayout Ecr { get; } = new(
        "EPFO ECR 2.0", "#~#", false,
        ["UAN", "Member Name", "Gross Wages", "EPF Wages", "EPS Wages", "EDLI Wages", "EPF Contribution remitted",
         "EPS Contribution remitted", "EPF EPS Difference remitted", "NCP Days", "Refund of Advances"],
        "EPFO Unified Portal, 'ECR file format (Version 2.0)' help text and sample file; text file, one member a line, whole rupees.",
        new DateOnly(2026, 11, 8));

    /// <summary>ESIC monthly contribution upload (the portal's MC template; save as Excel 97-2003 before uploading).</summary>
    public static FileLayout EsicMonthly { get; } = new(
        "ESIC monthly contribution", ",", true,
        ["IP Number", "IP Name", "No of Days for which wages paid/payable during the month", "Total Monthly Wages",
         "Reason Code for Zero workings days", "Last Working Day"],
        "ESIC employer portal, Monthly Contribution > Upload Excel, template MC_Template.xls and its reason-code list.",
        new DateOnly(2026, 11, 8));

    /// <summary>Protean (NSDL) e-TDS/TCS regular statement, file header (FH).</summary>
    public static FileLayout TdsFileHeader { get; } = new(
        "e-TDS FH", "^", false,
        ["Line Number", "Record Type", "File Type", "Upload Type", "File Creation Date", "File Sequence No", "Uploader Type",
         "TAN of Deductor", "Total No of Batches", "Return Preparation Utility", "Record Hash", "FVU Version", "File Hash",
         "Sam Version", "SAM Hash", "SCM Version", "SCM Hash", "Consolidated File Hash"],
        TdsSource, new DateOnly(2026, 11, 8));

    /// <summary>Batch header (BH): the deductor and the person responsible.</summary>
    public static FileLayout TdsBatchHeader { get; } = new(
        "e-TDS BH", "^", false,
        ["Line Number", "Record Type", "Batch Number", "Count of Challan Records", "Form Number", "Transaction Type",
         "Batch Updation Indicator", "Original RRR No", "Previous RRR No", "RRR No", "RRR Date", "Last TAN of Deductor",
         "TAN of Deductor", "Receipt Number", "PAN of Deductor", "Assessment Year", "Financial Year", "Period", "Name of Deductor",
         "Deductor Branch", "Deductor Address 1", "Deductor Address 2", "Deductor Address 3", "Deductor Address 4", "Deductor Address 5",
         "Deductor State", "Deductor PIN", "Deductor Email", "Deductor STD Code", "Deductor Phone", "Deductor Address Changed",
         "Deductor Type", "Responsible Person Name", "Responsible Person Designation", "Responsible Address 1", "Responsible Address 2",
         "Responsible Address 3", "Responsible Address 4", "Responsible Address 5", "Responsible State", "Responsible PIN",
         "Responsible Email", "Responsible Mobile", "Responsible STD Code", "Responsible Phone", "Responsible Address Changed",
         "Batch Total of Deposit Amount", "Unmatched Challan Count", "Count of Salary Detail Records", "Batch Total of Gross Total Income",
         "AO Approval", "AO Approval Number", "Last Deductor Type", "State Name", "PAO Code", "DDO Code", "Ministry Name",
         "Ministry Name Other", "PAN of Responsible Person", "PAO Registration No", "DDO Registration No", "Deductor STD Code Alternate",
         "Deductor Phone Alternate", "Deductor Email Alternate", "Responsible STD Code Alternate", "Responsible Phone Alternate",
         "Responsible Email Alternate", "AIN", "GSTN", "Record Hash"],
        TdsSource, new DateOnly(2026, 11, 8));

    /// <summary>Challan detail (CD): one challan deposited for the quarter.</summary>
    public static FileLayout TdsChallanDetail { get; } = new(
        "e-TDS CD", "^", false,
        ["Line Number", "Record Type", "Batch Number", "Challan Record No", "Count of Deductee Records", "NIL Challan Indicator",
         "Challan Updation Indicator", "Filler 3", "Filler 4", "Filler 5", "Last Bank Challan No", "Bank Challan No",
         "Last Transfer Voucher No", "DDO Serial No", "Last Bank Branch Code", "Bank Branch Code", "Last Challan Date", "Challan Date",
         "Filler 6", "Filler 7", "Section", "Oltas Income Tax", "Oltas Surcharge", "Oltas Cess", "Oltas Interest", "Oltas Others",
         "Total Deposit Amount", "Last Total Deposit Amount", "Total Tax Deposit as per Deductees", "TDS Income Tax", "TDS Surcharge",
         "TDS Cess", "Sum of TDS", "TDS Interest", "TDS Others", "Cheque DD No", "Book Entry Cash", "Remarks", "Late Payment Fee",
         "Minor Head", "Record Hash"],
        TdsSource, new DateOnly(2026, 11, 8));

    /// <summary>Deductee detail (DD): one deduction from one employee or payee, under its challan.</summary>
    public static FileLayout TdsDeducteeDetail { get; } = new(
        "e-TDS DD", "^", false,
        ["Line Number", "Record Type", "Batch Number", "Challan Record No", "Deductee Record No", "Mode", "Employee Serial No",
         "Deductee Code", "Last PAN", "PAN", "Last PAN Ref No", "PAN Ref No", "Name", "TDS Income Tax", "TDS Surcharge", "TDS Cess",
         "Total TDS", "Last Total TDS", "Total Tax Deposited", "Last Total Tax Deposited", "Total Value of Purchase", "Amount Paid",
         "Date Paid", "Date Deducted", "Date of Deposit", "Rate", "Grossing Up Indicator", "Book Entry Cash", "Date of Certificate",
         "Remarks", "Remarks 2", "Remarks 3", "Section Code", "Certificate No 197", "Record Hash"],
        TdsSource, new DateOnly(2026, 11, 8));

    /// <summary>HDFC Bank ENet bulk upload (no header row).</summary>
    public static FileLayout HdfcEnet { get; } = new(
        "HDFC ENet", ",", false,
        ["Transaction Type", "Beneficiary Code", "Beneficiary Account Number", "Instrument Amount", "Beneficiary Name", "Drawee Location",
         "Print Location", "Bene Address 1", "Bene Address 2", "Bene Address 3", "Bene Address 4", "Bene Address 5",
         "Instruction Reference Number", "Customer Reference Number", "Payment Details 1", "Payment Details 2", "Payment Details 3",
         "Payment Details 4", "Payment Details 5", "Payment Details 6", "Payment Details 7", "Cheque Number", "Chq/Trn Date",
         "MICR Number", "IFSC Code", "Bene Bank Name", "Bene Bank Branch Name", "Beneficiary Email Id"],
        "HDFC Bank ENet corporate banking, bulk upload standard format (N = NEFT, R = RTGS, I = within HDFC). Confirm against the "
        + "sample file the bank gives at onboarding: client codes and optional columns differ by mandate.",
        new DateOnly(2026, 11, 8));

    /// <summary>ICICI Bank Corporate Internet Banking bulk payment upload (header row).</summary>
    public static FileLayout IciciCib { get; } = new(
        "ICICI CIB bulk", ",", true,
        ["Debit Ac No", "Beneficiary Ac No", "Beneficiary Name", "Amt", "Pay Mod", "Date", "IFSC", "Payable Location",
         "Print Location", "Bene Mobile No", "Bene Email ID", "Bene add1", "Bene add2", "Bene add3", "Bene add4", "Add Details 1",
         "Add Details 2", "Add Details 3", "Add Details 4", "Add Details 5", "Remarks"],
        "ICICI Bank CIB, Bulk Payments upload template (Pay Mod I = within ICICI, N = NEFT, R = RTGS). Confirm against the "
        + "template downloaded from the hospital's CIB login.",
        new DateOnly(2026, 11, 8));

    /// <summary>State Bank of India CINB/CMP bulk upload (pipe separated, header row).</summary>
    public static FileLayout SbiCmp { get; } = new(
        "SBI CMP bulk", "|", true,
        ["Beneficiary Name", "Beneficiary Account No", "IFSC", "Transaction Type", "Amount", "Debit Account No", "Narration", "Value Date"],
        "SBI Corporate Internet Banking / CMP bulk upload as set up for salary (DCR = within SBI, NEFT otherwise). SBI configures "
        + "the layout per corporate at onboarding: confirm the column order with the relationship manager's sample file.",
        new DateOnly(2026, 11, 8));

    private const string TdsSource =
        "Protean eGov (NSDL), 'File Format for e-TDS/TCS regular statements' for Forms 24Q and 26Q, record types FH, BH, CD and DD; "
        + "caret separated, validated by the File Validation Utility (FVU) before upload. Run every file through the current FVU.";
}
