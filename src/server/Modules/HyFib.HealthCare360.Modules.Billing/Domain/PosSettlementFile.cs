using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>One transaction read from an acquirer's settlement file, in HC360's terms.</summary>
/// <param name="RowNumber">The row in its sheet (1 is the first), for messages.</param>
/// <param name="Amount">The transaction's amount, always positive.</param>
/// <param name="Net">What the transaction added to the payout: <c>Gross - Charges - Tax</c>.</param>
public sealed record PosSettlementRow(
    int RowNumber,
    PosSettlementLineType Type,
    PaymentMode Mode,
    string? TerminalId,
    DateTimeOffset? TransactionAt,
    DateOnly? TransactionOn,
    decimal Amount,
    decimal Charges,
    decimal Tax,
    decimal Net,
    string? Rrn,
    string? ApprovalCode,
    string? CardLast4,
    DateOnly SettledOn,
    string? Utr)
{
    /// <summary>
    /// For a chargeback with neither an RRN nor an approval code: which of the report's chargebacks that look the same
    /// (terminal, amount, days and UTR) this one is, counted in the report's order. It keeps them apart, and a later
    /// report listing the same ones in the same order gives them the same keys.
    /// </summary>
    public int Occurrence { get; init; } = 1;

    /// <summary>
    /// The same transaction gets the same key from every file that lists it: its type and RRN, or without an RRN its
    /// terminal, approval code, amount and day (a chargeback without either: also its settlement day, UTR and
    /// <see cref="Occurrence"/>).
    /// </summary>
    public string Key => Rrn is not null
        ? $"{Type}|{Rrn}"
        : ApprovalCode is not null
            ? $"{Type}|{TerminalId}|{ApprovalCode}|{Amount.ToString("0.00", CultureInfo.InvariantCulture)}|{Day(TransactionOn)}"
            : $"{Type}|{TerminalId}|{Amount.ToString("0.00", CultureInfo.InvariantCulture)}|{Day(TransactionOn)}|{Day(SettledOn)}|{Utr}"
              + (Occurrence > 1 ? $"#{Occurrence.ToString(CultureInfo.InvariantCulture)}" : string.Empty);

    /// <summary>A chargeback without an RRN or approval code: nothing in the report tells two alike apart.</summary>
    public bool IsUnreferenced => Rrn is null && ApprovalCode is null;

    private static string Day(DateOnly? date) => date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? "-";
}

/// <param name="Columns">The headings HC360 recognised, as <c>Amount from "Txn Amount"</c>, for the import's message.</param>
public sealed record PosSettlementFileContent(IReadOnlyList<PosSettlementRow> Rows, int Ignored, IReadOnlyList<string> Problems, IReadOnlyList<string> Columns);

/// <summary>
/// Reads an acquirer's settlement report (INT-POS reconciliation): CSV (comma, semicolon, tab or pipe separated) or an
/// Excel workbook (.xlsx). Banks and POS companies lay their reports out differently, so the columns are found by their
/// headings, which may come after a few title rows; the hospital can add its acquirer's headings in the
/// <c>billing.pos.settlements</c> setting. Dates are read day first, as Indian reports write them, in Indian time. Only
/// the card's last four digits are kept from the card number.
/// </summary>
public static class PosSettlementFile
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxRows = 20_000;
    private const int MaxProblems = 20;
    private const int HeaderSearchRows = 30;
    private const long MaxUnzippedBytes = 150L * 1024 * 1024;
    private static readonly TimeSpan India = TimeSpan.FromHours(5.5);

    public const string AcceptedFiles = "Import the settlement report as the acquirer gives it, in CSV or Excel (.xlsx), at most 10 MB.";

    /// <summary>The fields HC360 reads, each with the headings (letters and digits only, lower case) that name it.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>
    {
        ["rrn"] = ["rrn", "rrnno", "rrnnumber", "retrievalreferencenumber", "retrievalrefno", "bankrrn", "upirrn", "custrefno", "customerreferencenumber", "upirefno", "upireferenceno", "upireferencenumber", "upitransactionid", "upitxnid", "npcitxnid"],
        ["approvalCode"] = ["approvalcode", "apprcode", "approvalno", "authcode", "authorizationcode", "authorisationcode", "authid", "authno", "authorizationid", "appcode", "approvalid"],
        ["amount"] = ["txnamount", "transactionamount", "tranamount", "trxnamount", "txnamt", "tranamt", "trxnamt", "transactionamt", "grossamount", "grossamt", "saleamount", "originalamount", "amount", "amt"],
        ["charges"] = ["mdramount", "mdramt", "mdr", "commissionamount", "commissionamt", "commission", "msfamount", "msf", "chargeamount", "charges", "fees", "fee", "merchantdiscount"],
        ["tax"] = ["gst", "gstamount", "gstamt", "gstonmdr", "gstoncommission", "totalgst", "taxamount", "taxamt", "tax", "servicetax", "igst", "cgst", "sgst", "utgst"],
        ["net"] = ["netamount", "netamt", "net", "netsettlementamount", "netsettledamount", "settlementamount", "settledamount", "settlementamt", "netpayable", "payableamount", "amountpaid", "netcredited", "creditamount", "netpayout", "payoutamount", "amountsettled"],
        ["terminalId"] = ["tid", "tidno", "terminalid", "terminalno", "terminalnumber", "terminal", "posid"],
        ["transactionDate"] = ["txndate", "transactiondate", "trandate", "trxndate", "txndatetime", "transactiondatetime", "trandatetime", "trxndatetime", "txntimestamp", "transactiontimestamp", "saledate", "datetime", "date"],
        ["transactionTime"] = ["txntime", "transactiontime", "trantime", "trxntime", "time"],
        ["card"] = ["cardno", "cardnumber", "maskedcardno", "maskedcardnumber", "maskedpan", "pan", "cardnum", "card"],
        ["type"] = ["txntype", "transactiontype", "trantype", "trxntype", "type"],
        ["mode"] = ["paymentmode", "paymode", "paymenttype", "paymentmethod", "txnmode", "channel", "paymentchannel", "instrument", "instrumenttype", "cardtype", "cardcategory", "cardnetwork", "network", "cardscheme", "scheme"],
        ["settlementDate"] = ["settlementdate", "settledate", "settleddate", "settlementdt", "payoutdate", "paymentdate", "creditdate", "valuedate", "fundingdate", "remittancedate", "paiddate"],
        ["utr"] = ["utr", "utrno", "utrnumber", "neftutr", "neftrefno", "bankutr", "settlementutr", "payoututr", "settlementreference", "paymentreference", "bankreference", "bankrefno", "remittanceref"],
    };

    /// <summary>The fields in words, for telling the accountant which heading was read as what.</summary>
    private static readonly IReadOnlyDictionary<string, string> FieldNames = new Dictionary<string, string>
    {
        ["rrn"] = "RRN",
        ["approvalCode"] = "Approval code",
        ["amount"] = "Amount",
        ["charges"] = "Charges",
        ["tax"] = "GST",
        ["net"] = "Net amount",
        ["terminalId"] = "TID",
        ["transactionDate"] = "Transaction date",
        ["transactionTime"] = "Transaction time",
        ["card"] = "Card",
        ["type"] = "Type",
        ["mode"] = "Payment mode",
        ["settlementDate"] = "Settlement date",
        ["utr"] = "UTR",
    };

    /// <summary>GST split into its parts: added up when the report has no total GST column.</summary>
    private static readonly string[] TaxParts = ["igst", "cgst", "sgst", "utgst"];

    private static readonly string[] AmountFields = ["amount", "charges", "tax", "net"];

    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yy", "dd-MM-yy",
        "dd-MMM-yyyy", "d-MMM-yyyy", "dd-MMM-yy", "d-MMM-yy", "dd MMM yyyy", "d MMM yyyy", "dd/MMM/yyyy", "dd MMM yy",
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd",
    ];

    private static readonly string[] TimeFormats =
    [
        "", " HH:mm:ss", " HH:mm", " H:mm:ss", " H:mm", " HH:mm:ss.fff", " hh:mm:ss tt", " hh:mm tt", " h:mm:ss tt", " h:mm tt", "THH:mm:ss", "THH:mm:ss.fff", "'T'HH:mm:ss",
    ];

    private static readonly string[] DateTimeFormats = DateFormats.SelectMany(d => TimeFormats.Select(t => d + t)).ToArray();

    private static readonly string[] TimeOnlyFormats = ["HH:mm:ss", "H:mm:ss", "HH:mm", "H:mm", "hh:mm:ss tt", "h:mm:ss tt", "hh:mm tt", "h:mm tt", "HHmmss"];

    /// <param name="settledOn">The day the bank was paid, for a report without a settlement date column.</param>
    public static Result<PosSettlementFileContent> Read(byte[] content, PosSettlementSettings settings, DateOnly? settledOn)
    {
        if (content.Length == 0)
        {
            return Error.Field("file", "The file is empty. " + AcceptedFiles);
        }

        if (content.Length > MaxBytes)
        {
            return Error.Field("file", "The file is larger than 10 MB. Import the report in parts, for example a week at a time.");
        }

        var sheets = ReadSheets(content);
        if (sheets.IsFailure)
        {
            return sheets.Error!;
        }

        // Every sheet with the headings holds transactions (some acquirers put cards and UPI on sheets of their own);
        // a sheet without them, such as a summary, is passed over.
        var synonyms = Synonyms(settings);
        var found = sheets.Value
            .Select(sheet => (Sheet: sheet, Header: FindHeader(sheet.Rows, synonyms)))
            .Where(s => s.Header is not null)
            .Select(s => (s.Sheet, Header: s.Header!))
            .ToList();
        if (found.Count == 0)
        {
            return Error.Field("file",
                "HC360 could not find the column headings: it needs the transaction amount and the RRN or approval code, with headings such as "
                + "\"Txn Amount\", \"RRN\" and \"Approval Code\". If your acquirer names them differently, an administrator can add its headings "
                + $"in the setting {PosSettlementSettings.Key}.");
        }

        var report = new ReportReader(settledOn);
        foreach (var (sheet, header) in found)
        {
            var read = report.Read(sheet, header, nameSheets: found.Count > 1);
            if (read is not null)
            {
                return read;
            }
        }

        return report.Content();
    }

    /// <summary>A heading as HC360 compares it: letters and digits only, lower case.</summary>
    public static string Normalize(string? heading) =>
        new string((heading ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static Dictionary<string, string[]> Synonyms(PosSettlementSettings settings) =>
        Fields.ToDictionary(
            f => f.Key,
            f => (settings.Columns.FirstOrDefault(c => string.Equals(c.Key, f.Key, StringComparison.OrdinalIgnoreCase)).Value ?? [])
                .Select(Normalize).Where(h => h.Length > 0)
                .Concat(f.Value)
                .Distinct()
                .ToArray());

    private sealed record Header(int Row, IReadOnlyDictionary<string, int> Columns, IReadOnlyList<int> TaxColumns, IReadOnlyList<string> Found);

    /// <summary>
    /// The first row, within the first rows of the sheet, that names the amount and the RRN or approval code; each field
    /// takes the first column whose heading names it, in the order of its headings.
    /// </summary>
    private static Header? FindHeader(IReadOnlyList<string[]> sheet, Dictionary<string, string[]> synonyms)
    {
        for (var r = 0; r < Math.Min(sheet.Count, HeaderSearchRows); r++)
        {
            var headings = sheet[r];
            var normalized = headings.Select(Normalize).ToArray();
            var claimed = new HashSet<int>();
            var columns = new Dictionary<string, int>();
            foreach (var (field, names) in synonyms)
            {
                foreach (var name in names)
                {
                    var index = Array.FindIndex(normalized, h => h == name);
                    while (index >= 0 && (claimed.Contains(index) || (AmountFields.Contains(field) && IsRate(headings[index]))))
                    {
                        index = Array.FindIndex(normalized, index + 1, h => h == name);
                    }

                    if (index >= 0)
                    {
                        columns[field] = index;
                        claimed.Add(index);
                        break;
                    }
                }
            }

            if (!columns.ContainsKey("amount") || !(columns.ContainsKey("rrn") || columns.ContainsKey("approvalCode")))
            {
                continue;
            }

            // A report with a total GST column uses it; one that splits GST into its parts has them added up.
            var taxColumns = new List<int>();
            if (columns.TryGetValue("tax", out var tax) && TaxParts.Contains(normalized[tax]))
            {
                taxColumns.AddRange(Enumerable.Range(0, normalized.Length)
                    .Where(i => TaxParts.Contains(normalized[i]) && (i == tax || !claimed.Contains(i)) && !IsRate(headings[i])));
            }
            else if (columns.TryGetValue("tax", out tax))
            {
                taxColumns.Add(tax);
            }

            var found = columns.OrderBy(c => c.Value).Select(c => $"{FieldNames[c.Key]} from \"{headings[c.Value].Trim()}\"").ToList();
            return new Header(r, columns, taxColumns, found);
        }

        return null;
    }

    private static bool IsRate(string heading) =>
        heading.Contains('%', StringComparison.Ordinal)
        || heading.Contains("rate", StringComparison.OrdinalIgnoreCase)
        || heading.Contains("percent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the transactions of a report's sheets into one list: a transaction repeated, also on another sheet, is
    /// left out, and the problems name the sheet when there is more than one.
    /// </summary>
    private sealed class ReportReader(DateOnly? settledOn)
    {
        private readonly List<PosSettlementRow> _rows = [];
        private readonly List<string> _problems = [];
        private readonly List<string> _columns = [];
        private readonly Dictionary<string, string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _unreferenced = new(StringComparer.Ordinal);
        private int _ignored;

        /// <summary>Reads one sheet; returns an error that stops the import, or null.</summary>
        public Result<PosSettlementFileContent>? Read(ReportSheet sheet, Header header, bool nameSheets)
        {
            var sheetName = nameSheets && !string.IsNullOrWhiteSpace(sheet.Name) ? $"Sheet \"{sheet.Name.Trim()}\"" : null;
            if (!header.Columns.ContainsKey("settlementDate") && settledOn is null)
            {
                return Error.Field("settledOn",
                    $"{(sheetName is null ? "The report" : sheetName)} has no settlement date column. Choose the day the acquirer paid the bank.");
            }

            if (sheet.Rows.Count - header.Row - 1 > MaxRows + 1000)
            {
                return Error.Field("file", $"The report has more than {MaxRows:N0} rows. Import it in parts, for example a week at a time.");
            }

            _columns.AddRange(header.Found.Where(c => !_columns.Contains(c)));
            var hints = new DateHints();
            for (var r = header.Row + 1; r < sheet.Rows.Count; r++)
            {
                var cells = sheet.Rows[r];
                var place = sheetName is null ? $"Row {r + 1}" : $"{sheetName}, row {r + 1}";
                if (cells.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                string? Cell(string field) => header.Columns.TryGetValue(field, out var index) && index < cells.Length && !string.IsNullOrWhiteSpace(cells[index])
                    ? cells[index].Trim()
                    : null;

                // The report's totals are not transactions.
                if (cells.Take(3).Any(c => c.TrimStart().StartsWith("total", StringComparison.OrdinalIgnoreCase)
                                           || c.TrimStart().StartsWith("grand total", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var parsed = ReadRow(r + 1, place, Cell, cells, header, hints, settledOn);
                if (parsed.IsFailure)
                {
                    Problem(parsed.Error!.Message);
                    continue;
                }

                var row = parsed.Value;
                if (row.IsUnreferenced)
                {
                    // Nothing tells two such chargebacks apart, so both are kept, counted in the report's order.
                    var occurrence = _unreferenced.GetValueOrDefault(row.Key) + 1;
                    _unreferenced[row.Key] = occurrence;
                    row = row with { Occurrence = occurrence };
                }
                else if (_seen.TryGetValue(row.Key, out var first))
                {
                    Problem($"{place} repeats {Lower(first)}, so it was left out.");
                    continue;
                }

                _seen[row.Key] = place;
                _rows.Add(row);
                if (_rows.Count > MaxRows)
                {
                    return Error.Field("file", $"The report has more than {MaxRows:N0} transactions. Import it in parts, for example a week at a time.");
                }
            }

            return null;
        }

        public Result<PosSettlementFileContent> Content()
        {
            if (_rows.Count == 0)
            {
                return Error.Field("file", _problems.Count > 0
                    ? "No transaction in the report could be read. " + _problems[0]
                    : "The report has headings but no transactions.");
            }

            var problems = _problems.ToList();
            if (_ignored > problems.Count && problems.Count == MaxProblems)
            {
                problems.Add($"…and {_ignored - problems.Count} more rows left out.");
            }

            return new PosSettlementFileContent(_rows, _ignored, problems, _columns);
        }

        private void Problem(string message)
        {
            _ignored++;
            if (_problems.Count < MaxProblems)
            {
                _problems.Add(message);
            }
        }

        /// <summary>A place as it reads mid-sentence: "row 2", "sheet "UPI", row 2".</summary>
        private static string Lower(string place) => char.ToLowerInvariant(place[0]) + place[1..];
    }

    /// <summary>The date formats the report's columns turned out to use.</summary>
    private sealed class DateHints
    {
        public string? Settled;
        public string? Transaction;
    }

    private static Result<PosSettlementRow> ReadRow(
        int number, string place, Func<string, string?> cell, string[] cells, Header header, DateHints hints, DateOnly? defaultSettledOn)
    {
        if (cell("amount") is not { } amountText || ParseAmount(amountText) is not { } signedAmount || signedAmount == 0)
        {
            return Error.Rule("row", $"{place} has no transaction amount, so it was left out.");
        }

        var typeText = cell("type") ?? string.Empty;
        var type = TypeOf(typeText);
        if (type == PosSettlementLineType.Sale && signedAmount < 0)
        {
            type = PosSettlementLineType.Refund;
        }

        var amount = Money.Round(Math.Abs(signedAmount));
        var rrn = Reference(cell("rrn"), 32);
        var approval = Reference(cell("approvalCode"), 16);
        if (approval is not null && approval.All(c => c == '0'))
        {
            approval = null;
        }

        if (rrn is null && approval is null && type != PosSettlementLineType.Adjustment)
        {
            return Error.Rule("row", $"{place} has neither an RRN nor an approval code, so it cannot be matched and was left out.");
        }

        DateOnly settledOn;
        if (cell("settlementDate") is { } settledText)
        {
            if (ParseDateTime(settledText, ref hints.Settled) is not { } settled)
            {
                return Error.Rule("row", $"{place}: the settlement date \"{Shorten(settledText)}\" could not be read.");
            }

            settledOn = DateOnly.FromDateTime(settled);
        }
        else if (defaultSettledOn is { } chosen)
        {
            settledOn = chosen;
        }
        else
        {
            return Error.Rule("row", $"{place} has no settlement date, so it was left out.");
        }

        DateTimeOffset? transactionAt = null;
        if (cell("transactionDate") is { } dateText)
        {
            if (ParseDateTime(dateText, ref hints.Transaction) is not { } local)
            {
                return Error.Rule("row", $"{place}: the transaction date \"{Shorten(dateText)}\" could not be read.");
            }

            if (local.TimeOfDay == TimeSpan.Zero && cell("transactionTime") is { } timeText && ParseTime(timeText) is { } time)
            {
                local = local.Date + time;
            }

            transactionAt = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), India);
        }

        var tax = header.TaxColumns.Sum(i => i < cells.Length && ParseAmount(cells[i]) is { } part ? Math.Abs(part) : 0m);
        var charges = cell("charges") is { } chargesText && ParseAmount(chargesText) is { } fee ? Math.Abs(fee) : 0m;
        decimal? netGiven = cell("net") is { } netText && ParseAmount(netText) is { } netValue ? Math.Abs(netValue) : null;
        tax = Money.Round(tax);
        charges = Money.Round(charges);

        decimal net;
        if (type == PosSettlementLineType.Sale)
        {
            if (netGiven is { } paid)
            {
                net = Money.Round(paid);
                charges = amount - tax - net;
            }
            else
            {
                net = amount - charges - tax;
            }

            if (net < 0 || charges < 0)
            {
                return Error.Rule("row", $"{place}: the net amount, charges and GST do not add up to the transaction amount, so it was left out.");
            }
        }
        else
        {
            // A refund or chargeback takes money back; any charge returned with it (or added to it) counts as charges.
            net = netGiven is { } taken ? -Money.Round(taken) : -amount;
            tax = 0m;
            charges = -amount - net;
        }

        var cardDigits = new string((cell("card") ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        var cardLast4 = cardDigits.Length >= 4 ? cardDigits[^4..] : null;
        return new PosSettlementRow(
            number,
            type,
            ModeOf(cell("mode"), typeText, cardLast4, approval),
            Reference(cell("terminalId"), 32),
            transactionAt,
            transactionAt is { } at ? DateOnly.FromDateTime(at.DateTime) : null,
            amount,
            charges,
            tax,
            net,
            rrn,
            approval,
            cardLast4,
            settledOn,
            Reference(cell("utr"), 40));
    }

    private static PosSettlementLineType TypeOf(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("chargeback", StringComparison.Ordinal) || t.Contains("cbk", StringComparison.Ordinal) || t.Contains("dispute", StringComparison.Ordinal)
            || t.Contains("adjust", StringComparison.Ordinal))
        {
            return PosSettlementLineType.Adjustment;
        }

        return t.Contains("refund", StringComparison.Ordinal) || t.Contains("void", StringComparison.Ordinal) || t.Contains("revers", StringComparison.Ordinal)
               || t.Contains("return", StringComparison.Ordinal) || t.Contains("cancel", StringComparison.Ordinal)
            ? PosSettlementLineType.Refund
            : PosSettlementLineType.Sale;
    }

    private static PaymentMode ModeOf(string? modeText, string typeText, string? cardLast4, string? approval)
    {
        var mode = (modeText ?? string.Empty).ToLowerInvariant();
        if (mode.Contains("upi", StringComparison.Ordinal) || typeText.Contains("upi", StringComparison.OrdinalIgnoreCase))
        {
            return PaymentMode.Upi;
        }

        string[] cardWords = ["card", "credit", "debit", "visa", "master", "rupay", "amex", "diners", "maestro", "prepaid"];
        if (cardWords.Any(w => mode.Contains(w, StringComparison.Ordinal)))
        {
            return PaymentMode.Card;
        }

        // Without a mode column: a UPI payment has neither a card number nor an approval code.
        return cardLast4 is null && approval is null ? PaymentMode.Upi : PaymentMode.Card;
    }

    /// <summary>
    /// An RRN, approval code, TID or UTR: letters and digits only (a spreadsheet's leading apostrophe or a number's
    /// trailing ".0" dropped), or null.
    /// </summary>
    internal static string? Reference(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().TrimStart('\'');
        if (value.EndsWith(".0", StringComparison.Ordinal) && value[..^2].All(char.IsAsciiDigit))
        {
            value = value[..^2];
        }

        var clean = new string(value.Where(char.IsAsciiLetterOrDigit).Take(max).ToArray()).ToUpperInvariant();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>An amount as reports write it: "1,234.50", "₹ 1,234.50", "INR 1234.5", "-12.00" or "(12.00)".</summary>
    internal static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim().Replace("₹", string.Empty, StringComparison.Ordinal).Replace("INR", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("Rs.", string.Empty, StringComparison.OrdinalIgnoreCase).Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        var negative = false;
        if (value.StartsWith('(') && value.EndsWith(')'))
        {
            negative = true;
            value = value[1..^1].Trim();
        }

        if (value.EndsWith("Dr", StringComparison.OrdinalIgnoreCase) || value.EndsWith("Cr", StringComparison.OrdinalIgnoreCase))
        {
            negative |= value.EndsWith("Dr", StringComparison.OrdinalIgnoreCase);
            value = value[..^2].Trim();
        }

        if (!decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var amount) || Math.Abs(amount) > 100_000_000m)
        {
            return null;
        }

        return negative ? -amount : amount;
    }

    /// <summary>A date (and time) written day first, ISO, or as an Excel serial number; in Indian time.</summary>
    internal static DateTime? ParseDateTime(string? text)
    {
        string? format = null;
        return ParseDateTime(text, ref format);
    }

    /// <param name="format">The format the column's last date had, tried first: a report writes all its dates alike.</param>
    private static DateTime? ParseDateTime(string? text, ref string? format)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (format is not null
            && DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var same))
        {
            return same;
        }

        foreach (var candidate in DateTimeFormats)
        {
            if (DateTime.TryParseExact(value, candidate, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                format = candidate;
                return parsed;
            }
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20_000 and < 80_000)
        {
            return DateTime.FromOADate(serial);
        }

        if (value.Contains('T', StringComparison.Ordinal)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var withOffset))
        {
            return withOffset.ToOffset(India).DateTime;
        }

        return null;
    }

    private static TimeSpan? ParseTime(string text)
    {
        var value = text.Trim();
        if (DateTime.TryParseExact(value, TimeOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var time))
        {
            return time.TimeOfDay;
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) && fraction is >= 0 and < 1
            ? TimeSpan.FromSeconds(Math.Round(fraction * 86_400))
            : null;
    }

    private static string Shorten(string text) => text.Length > 30 ? text[..30] + "…" : text;

    // ---------------------------------------------------------------------------------------------------- the table

    internal static Result<IReadOnlyList<ReportSheet>> ReadSheets(byte[] content)
    {
        if (content is [0xD0, 0xCF, 0x11, 0xE0, ..])
        {
            return Error.Field("file", "This is an older Excel file (.xls). Open it in Excel and save it as .xlsx or .csv, then import that.");
        }

        if (content is [(byte)'P', (byte)'K', 3, 4, ..])
        {
            return XlsxSheets.Read(content, MaxUnzippedBytes, MaxRows);
        }

        return Result<IReadOnlyList<ReportSheet>>.Ok([new ReportSheet(null, ReadDelimited(Decode(content)))]);
    }

    private static string Decode(byte[] content)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            // Reports saved by older Excel versions are in the Windows code page; Latin-1 reads their digits and letters.
            return Encoding.Latin1.GetString(content);
        }
    }

    /// <summary>Delimited text with RFC 4180 quoting (a quoted field may hold the separator, quotes and line breaks).</summary>
    internal static IReadOnlyList<string[]> ReadDelimited(string text)
    {
        var separator = Separator(text);
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
                if (rows.Count > MaxRows + 1000)
                {
                    break;
                }
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    /// <summary>The separator used most often in the first lines: comma, semicolon, tab or pipe.</summary>
    private static char Separator(string text)
    {
        var sample = string.Join('\n', text.Split('\n', 12).Take(11));
        char[] candidates = [',', ';', '\t', '|'];
        return candidates.OrderByDescending(c => sample.Count(ch => ch == c)).First();
    }
}
