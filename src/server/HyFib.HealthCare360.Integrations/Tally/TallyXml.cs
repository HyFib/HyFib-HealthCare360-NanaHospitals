using System.Globalization;
using System.Xml.Linq;

namespace HyFib.HealthCare360.Integrations.Tally;

/// <summary>Company, cost-category and ledger mapping applied while building an import envelope.</summary>
internal sealed record TallyImportOptions(string? CompanyName, string CostCategory, IReadOnlyDictionary<string, string>? LedgerMap)
{
    public static TallyImportOptions From(TallySettings settings) =>
        new(settings.CompanyName, string.IsNullOrWhiteSpace(settings.CostCategory) ? "Primary Cost Category" : settings.CostCategory, settings.LedgerMap);

    public string MapLedger(string ledger) =>
        LedgerMap is not null && LedgerMap.TryGetValue(ledger, out var mapped) && !string.IsNullOrWhiteSpace(mapped) ? mapped.Trim() : ledger.Trim();
}

/// <summary>
/// Tally Prime XML: voucher import envelopes (<c>ENVELOPE/HEADER/TALLYREQUEST=Import Data</c>, <c>IMPORTDATA/REQUESTDESC</c>
/// with <c>SVCURRENTCOMPANY</c>, one <c>TALLYMESSAGE/VOUCHER</c> per voucher, with the GST party and the supplier's
/// invoice when the voucher has them, and each ledger line's bank and bill allocations), the company-list request used as
/// a health check and the day's voucher collection read back for reconciliation. Built with LINQ to XML so every value is
/// escaped.
/// </summary>
internal static class TallyXml
{
    public const string AccountingVoucherView = "Accounting Voucher View";
    public const string CompanyCollection = "HC360Companies";
    public const string DayVoucherCollection = "HC360DayVouchers";

    /// <summary>What the read-back asks Tally for each voucher: enough to match it by remote id and compare its ledgers.</summary>
    public const string DayVoucherFields =
        "Date, VoucherTypeName, VoucherNumber, GUID, RemoteID, IsCancelled, IsOptional, IsDeleted, "
        + "AllLedgerEntries.LedgerName, AllLedgerEntries.IsDeemedPositive, AllLedgerEntries.Amount";

    public static string BuildImport(IReadOnlyList<AccountingVoucher> vouchers, TallyImportOptions options)
    {
        var envelope = new XElement("ENVELOPE",
            new XElement("HEADER", new XElement("TALLYREQUEST", "Import Data")),
            new XElement("BODY",
                new XElement("IMPORTDATA",
                    new XElement("REQUESTDESC",
                        new XElement("REPORTNAME", "Vouchers"),
                        new XElement("STATICVARIABLES",
                            string.IsNullOrWhiteSpace(options.CompanyName) ? null : new XElement("SVCURRENTCOMPANY", Clean(options.CompanyName)))),
                    new XElement("REQUESTDATA", vouchers.Select(v => Message(v, options))))));
        return envelope.ToString();
    }

    /// <summary>Lists the companies open in Tally (proves the XML port is reachable and the company is loaded).</summary>
    public static string BuildCompanyListRequest() =>
        new XElement("ENVELOPE",
            new XElement("HEADER",
                new XElement("VERSION", "1"),
                new XElement("TALLYREQUEST", "Export"),
                new XElement("TYPE", "Collection"),
                new XElement("ID", CompanyCollection)),
            new XElement("BODY",
                new XElement("DESC",
                    new XElement("STATICVARIABLES", new XElement("SVEXPORTFORMAT", "$$SysName:XML")),
                    new XElement("TDL",
                        new XElement("TDLMESSAGE",
                            new XElement("COLLECTION", new XAttribute("NAME", CompanyCollection), new XAttribute("ISMODIFY", "No"),
                                new XElement("TYPE", "Company"),
                                new XElement("FETCH", "NAME"))))))).ToString();

    /// <summary>
    /// Exports the company's vouchers of one day: a TDL collection of type Voucher, whose period Tally takes from
    /// <c>SVFROMDATE</c> and <c>SVTODATE</c>. The answer is <c>ENVELOPE/BODY/DATA/COLLECTION/VOUCHER</c>, read by
    /// <see cref="TallyVoucherReader"/>.
    /// </summary>
    public static string BuildDayVouchersRequest(DateOnly date, string? companyName)
    {
        var day = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new XElement("ENVELOPE",
            new XElement("HEADER",
                new XElement("VERSION", "1"),
                new XElement("TALLYREQUEST", "Export"),
                new XElement("TYPE", "Collection"),
                new XElement("ID", DayVoucherCollection)),
            new XElement("BODY",
                new XElement("DESC",
                    new XElement("STATICVARIABLES",
                        new XElement("SVEXPORTFORMAT", "$$SysName:XML"),
                        string.IsNullOrWhiteSpace(companyName) ? null : new XElement("SVCURRENTCOMPANY", Clean(companyName)),
                        new XElement("SVFROMDATE", new XAttribute("TYPE", "Date"), day),
                        new XElement("SVTODATE", new XAttribute("TYPE", "Date"), day)),
                    new XElement("TDL",
                        new XElement("TDLMESSAGE",
                            new XElement("COLLECTION", new XAttribute("NAME", DayVoucherCollection), new XAttribute("ISMODIFY", "No"),
                                new XElement("TYPE", "Voucher"),
                                new XElement("FETCH", DayVoucherFields))))))).ToString();
    }

    /// <summary>
    /// Tally sign convention: debit lines are "deemed positive" and carry a negative amount; credit lines are not
    /// deemed positive and carry a positive amount. Amounts use two decimals and the invariant culture.
    /// </summary>
    public static string Amount(AccountingEntry entry) => Signed(entry.Amount, entry.IsDebit);

    /// <summary><paramref name="amount"/> with the sign of a line on the debit or the credit side (<see cref="Amount"/>).</summary>
    public static string Signed(decimal amount, bool isDebit)
    {
        var rounded = Money.Round(amount);
        return (isDebit ? -rounded : rounded).ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Tally's name of how money moved through the bank (<c>TRANSACTIONTYPE</c>).</summary>
    public static string TransactionType(AccountingBankTransaction type) => type switch
    {
        AccountingBankTransaction.Cheque => "Cheque",
        AccountingBankTransaction.EFundTransfer => "e-Fund Transfer",
        _ => "Others",
    };

    /// <summary>Tally's name of a bill-wise reference type (<c>BILLTYPE</c>).</summary>
    public static string BillType(AccountingBillType type) => type switch
    {
        AccountingBillType.AgainstRef => "Agst Ref",
        AccountingBillType.Advance => "Advance",
        AccountingBillType.OnAccount => "On Account",
        _ => "New Ref",
    };

    /// <summary>
    /// REMOTEID makes the import idempotent: Tally alters the voucher it already holds with this id instead of
    /// creating a duplicate when the outbox retries a batch (ADR-008).
    /// </summary>
    public static string RemoteId(string reference) => AccountingVoucher.RemoteIdPrefix + reference.Trim();

    /// <summary>Why Tally would reject the voucher, or null. Checked before sending so one bad voucher is reported clearly.</summary>
    public static string? Validate(AccountingVoucher voucher)
    {
        if (string.IsNullOrWhiteSpace(voucher.VoucherType) || string.IsNullOrWhiteSpace(voucher.Number))
        {
            return "voucher type and number are required";
        }

        if (voucher.Entries.Count < 2)
        {
            return "a voucher needs at least two ledger entries";
        }

        if (voucher.Entries.Any(e => string.IsNullOrWhiteSpace(e.LedgerName)))
        {
            return "every entry needs a ledger";
        }

        if (voucher.Party is { } party && string.IsNullOrWhiteSpace(party.LedgerName))
        {
            return "the party needs its ledger";
        }

        if (voucher.Entries.Any(e => e.Amount <= 0))
        {
            return "amounts must be positive (IsDebit decides the side)";
        }

        var debits = voucher.Entries.Where(e => e.IsDebit).Sum(e => Money.Round(e.Amount));
        var credits = voucher.Entries.Where(e => !e.IsDebit).Sum(e => Money.Round(e.Amount));
        if (debits != credits)
        {
            return string.Create(CultureInfo.InvariantCulture, $"debits ({debits:0.00}) and credits ({credits:0.00}) do not balance");
        }

        return voucher.Entries.Select(e => BillProblem(e) ?? BankProblem(e)).FirstOrDefault(problem => problem is not null);
    }

    /// <summary>Why a line's bank allocations would not stand in Tally: a part not positive, or parts that do not add up to the line.</summary>
    private static string? BankProblem(AccountingEntry entry)
    {
        var parts = entry.BankAllocations;
        if (parts.Count == 0)
        {
            return null;
        }

        var ledger = entry.LedgerName.Trim();
        if (parts.Any(b => b.Amount <= 0))
        {
            return $"the bank allocations of {ledger} must be positive (the line decides the side)";
        }

        var allocated = parts.Sum(b => Money.Round(b.Amount));
        return allocated == Money.Round(entry.Amount)
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"the bank allocations of {ledger} ({allocated:0.00}) do not add up to its amount ({Money.Round(entry.Amount):0.00})");
    }

    /// <summary>Why a line's bill allocations would not stand in Tally: a part not positive, a bill without a name, or parts that do not add up to the line.</summary>
    private static string? BillProblem(AccountingEntry entry)
    {
        var bills = entry.BillAllocations;
        if (bills.Count == 0)
        {
            return null;
        }

        var ledger = entry.LedgerName.Trim();
        if (bills.Any(b => b.Amount <= 0))
        {
            return $"the bill allocations of {ledger} must be positive (the line decides the side)";
        }

        if (bills.Any(b => b.Type != AccountingBillType.OnAccount && string.IsNullOrWhiteSpace(b.Name)))
        {
            return $"every bill allocation of {ledger} except On Account needs the bill's name";
        }

        var allocated = bills.Sum(b => Money.Round(b.Amount));
        return allocated == Money.Round(entry.Amount)
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"the bill allocations of {ledger} ({allocated:0.00}) do not add up to its amount ({Money.Round(entry.Amount):0.00})");
    }

    public static IReadOnlyList<string> ParseCompanyNames(XDocument document) =>
        document.Descendants("COMPANY")
            .Select(company => (string?)company.Attribute("NAME") ?? company.Element("NAME")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static XElement Message(AccountingVoucher voucher, TallyImportOptions options)
    {
        XNamespace udf = "TallyUDF";
        return new XElement("TALLYMESSAGE", new XAttribute(XNamespace.Xmlns + "UDF", udf.NamespaceName), Voucher(voucher, options));
    }

    /// <summary>One voucher as it is imported, ledgers mapped (the simulator keeps this as its copy of the voucher).</summary>
    public static XElement Voucher(AccountingVoucher voucher, TallyImportOptions options)
    {
        var date = voucher.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var type = Clean(voucher.VoucherType);
        return new XElement("VOUCHER",
            voucher.RemoteId is { } remoteId ? new XAttribute("REMOTEID", Clean(remoteId)) : null,
            new XAttribute("VCHTYPE", type),
            new XAttribute("ACTION", "Create"),
            new XAttribute("OBJVIEW", AccountingVoucherView),
            new XElement("DATE", date),
            new XElement("EFFECTIVEDATE", date),
            new XElement("VOUCHERTYPENAME", type),
            new XElement("VOUCHERNUMBER", Clean(voucher.Number)),
            string.IsNullOrWhiteSpace(voucher.SupplierInvoiceNumber) ? null : new XElement("REFERENCE", Clean(voucher.SupplierInvoiceNumber)),
            voucher.SupplierInvoiceDate is { } invoiceDate ? new XElement("REFERENCEDATE", Date(invoiceDate)) : null,
            Party(voucher.Party, options),
            new XElement("PERSISTEDVIEW", AccountingVoucherView),
            string.IsNullOrWhiteSpace(voucher.Narration) ? null : new XElement("NARRATION", Clean(voucher.Narration)),
            voucher.Entries.Select(entry => LedgerEntry(entry, options)));
    }

    /// <summary>The GST party of a B2B sale or a purchase: its ledger (renamed by the ledger map), GSTIN, state and place of supply.</summary>
    private static IEnumerable<XElement> Party(AccountingParty? party, TallyImportOptions options)
    {
        if (party is null)
        {
            yield break;
        }

        yield return new XElement("PARTYLEDGERNAME", Clean(options.MapLedger(party.LedgerName)));
        if (!string.IsNullOrWhiteSpace(party.Gstin))
        {
            yield return new XElement("PARTYGSTIN", Clean(party.Gstin));
        }

        if (!string.IsNullOrWhiteSpace(party.StateName))
        {
            yield return new XElement("STATENAME", Clean(party.StateName));
        }

        if (!string.IsNullOrWhiteSpace(party.PlaceOfSupply))
        {
            yield return new XElement("PLACEOFSUPPLY", Clean(party.PlaceOfSupply));
        }
    }

    private static XElement LedgerEntry(AccountingEntry entry, TallyImportOptions options)
    {
        var amount = Amount(entry);
        return new XElement("ALLLEDGERENTRIES.LIST",
            new XElement("LEDGERNAME", Clean(options.MapLedger(entry.LedgerName))),
            new XElement("ISDEEMEDPOSITIVE", entry.IsDebit ? "Yes" : "No"),
            new XElement("AMOUNT", amount),
            entry.BankAllocations.Select(bank => new XElement("BANKALLOCATIONS.LIST",
                new XElement("DATE", Date(bank.Date)),
                new XElement("INSTRUMENTDATE", Date(bank.InstrumentDate ?? bank.Date)),
                new XElement("TRANSACTIONTYPE", TransactionType(bank.TransactionType)),
                string.IsNullOrWhiteSpace(bank.InstrumentNumber) ? null : new XElement("INSTRUMENTNUMBER", Clean(bank.InstrumentNumber)),
                string.IsNullOrWhiteSpace(bank.BankName) ? null : new XElement("BANKNAME", Clean(bank.BankName)),
                new XElement("AMOUNT", Signed(bank.Amount, entry.IsDebit)))),
            entry.BillAllocations.Select(bill => new XElement("BILLALLOCATIONS.LIST",
                string.IsNullOrWhiteSpace(bill.Name) ? null : new XElement("NAME", Clean(bill.Name)),
                new XElement("BILLTYPE", BillType(bill.Type)),
                new XElement("AMOUNT", Signed(bill.Amount, entry.IsDebit)))),
            string.IsNullOrWhiteSpace(entry.CostCentre)
                ? null
                : new XElement("CATEGORYALLOCATIONS.LIST",
                    new XElement("CATEGORY", Clean(options.CostCategory)),
                    new XElement("COSTCENTREALLOCATIONS.LIST",
                        new XElement("NAME", Clean(entry.CostCentre)),
                        new XElement("AMOUNT", amount))));
    }

    private static string Date(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Removes control characters XML cannot carry (they would throw while serialising).</summary>
    private static string Clean(string value) =>
        new string(value.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray()).Trim();
}
