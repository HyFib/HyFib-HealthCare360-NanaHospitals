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
/// with <c>SVCURRENTCOMPANY</c>, one <c>TALLYMESSAGE/VOUCHER</c> per voucher) and the company-list request used as a
/// health check. Built with LINQ to XML so every value is escaped.
/// </summary>
internal static class TallyXml
{
    public const string AccountingVoucherView = "Accounting Voucher View";
    public const string CompanyCollection = "HC360Companies";

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
    /// Tally sign convention: debit lines are "deemed positive" and carry a negative amount; credit lines are not
    /// deemed positive and carry a positive amount. Amounts use two decimals and the invariant culture.
    /// </summary>
    public static string Amount(AccountingEntry entry)
    {
        var amount = Money.Round(entry.Amount);
        return (entry.IsDebit ? -amount : amount).ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// REMOTEID makes the import idempotent: Tally alters the voucher it already holds with this id instead of
    /// creating a duplicate when the outbox retries a batch (ADR-008).
    /// </summary>
    public static string RemoteId(string reference) => "HC360-" + reference.Trim();

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

        if (voucher.Entries.Any(e => e.Amount <= 0))
        {
            return "amounts must be positive (IsDebit decides the side)";
        }

        var debits = voucher.Entries.Where(e => e.IsDebit).Sum(e => Money.Round(e.Amount));
        var credits = voucher.Entries.Where(e => !e.IsDebit).Sum(e => Money.Round(e.Amount));
        return debits == credits
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"debits ({debits:0.00}) and credits ({credits:0.00}) do not balance");
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

    private static XElement Voucher(AccountingVoucher voucher, TallyImportOptions options)
    {
        var date = voucher.Date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var type = Clean(voucher.VoucherType);
        return new XElement("VOUCHER",
            string.IsNullOrWhiteSpace(voucher.Reference) ? null : new XAttribute("REMOTEID", Clean(RemoteId(voucher.Reference))),
            new XAttribute("VCHTYPE", type),
            new XAttribute("ACTION", "Create"),
            new XAttribute("OBJVIEW", AccountingVoucherView),
            new XElement("DATE", date),
            new XElement("EFFECTIVEDATE", date),
            new XElement("VOUCHERTYPENAME", type),
            new XElement("VOUCHERNUMBER", Clean(voucher.Number)),
            new XElement("PERSISTEDVIEW", AccountingVoucherView),
            string.IsNullOrWhiteSpace(voucher.Narration) ? null : new XElement("NARRATION", Clean(voucher.Narration)),
            voucher.Entries.Select(entry => LedgerEntry(entry, options)));
    }

    private static XElement LedgerEntry(AccountingEntry entry, TallyImportOptions options)
    {
        var amount = Amount(entry);
        return new XElement("ALLLEDGERENTRIES.LIST",
            new XElement("LEDGERNAME", Clean(options.MapLedger(entry.LedgerName))),
            new XElement("ISDEEMEDPOSITIVE", entry.IsDebit ? "Yes" : "No"),
            new XElement("AMOUNT", amount),
            string.IsNullOrWhiteSpace(entry.CostCentre)
                ? null
                : new XElement("CATEGORYALLOCATIONS.LIST",
                    new XElement("CATEGORY", Clean(options.CostCategory)),
                    new XElement("COSTCENTREALLOCATIONS.LIST",
                        new XElement("NAME", Clean(entry.CostCentre)),
                        new XElement("AMOUNT", amount))));
    }

    /// <summary>Removes control characters XML cannot carry (they would throw while serialising).</summary>
    private static string Clean(string value) =>
        new string(value.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray()).Trim();
}
