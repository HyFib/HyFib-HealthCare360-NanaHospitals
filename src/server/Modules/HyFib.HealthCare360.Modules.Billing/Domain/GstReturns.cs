using System.Globalization;
using System.Text.Json.Nodes;

namespace HyFib.HealthCare360.Modules.Billing.Domain;

/// <summary>Tax amounts of a row of a return: the taxable value, IGST, CGST, SGST and cess.</summary>
public sealed record GstReturnAmounts(decimal Taxable, decimal Igst, decimal Cgst, decimal Sgst, decimal Cess)
{
    public static readonly GstReturnAmounts Zero = new(0m, 0m, 0m, 0m, 0m);

    public decimal Tax => Igst + Cgst + Sgst + Cess;

    public static GstReturnAmounts Of(GstReturnLine line) => new(line.Taxable, line.Igst, line.Cgst, line.Sgst, line.Cess);

    public GstReturnAmounts Plus(GstReturnAmounts other) => new(Taxable + other.Taxable, Igst + other.Igst, Cgst + other.Cgst, Sgst + other.Sgst, Cess + other.Cess);

    public GstReturnAmounts Times(decimal sign) => new(Taxable * sign, Igst * sign, Cgst * sign, Sgst * sign, Cess * sign);
}

/// <summary>A document's items by rate, as GSTR-1 lists them (one row per rate).</summary>
public sealed record Gstr1Item(decimal Rate, GstReturnAmounts Amounts);

/// <summary>An invoice in B2B (table 4A) or B2CL (table 5), or a note in CDNR (9B registered) or CDNUR (9B unregistered, original B2CL).</summary>
public sealed record Gstr1Document(
    string Number,
    DateOnly Date,
    string? BuyerGstin,
    string BuyerName,
    string PlaceOfSupply,
    decimal Value,
    IReadOnlyList<Gstr1Item> Items,
    GstDocumentKind Kind = GstDocumentKind.Invoice);

/// <summary>A B2CS row (table 7): supplies to unregistered buyers, by place of supply and rate, net of their notes.</summary>
public sealed record Gstr1B2csRow(bool InterState, string PlaceOfSupply, decimal Rate, GstReturnAmounts Amounts);

/// <summary>Table 8: nil-rated, exempt and non-GST supplies, by inter- or intra-state and registered or not.</summary>
public sealed record Gstr1NilRow(string SupplyType, decimal NilRated, decimal Exempt, decimal NonGst);

/// <summary>Table 12: the HSN summary, by HSN or SAC code, unit and rate.</summary>
public sealed record Gstr1HsnRow(string Hsn, string Description, string Uqc, decimal Quantity, decimal Rate, GstReturnAmounts Amounts);

/// <summary>Table 13: documents issued, by nature (1 invoices, 4 debit notes, 5 credit notes) and series.</summary>
public sealed record Gstr1DocumentsIssued(int Nature, string Series, string From, string To, int Total, int Cancelled)
{
    public int Net => Total - Cancelled;

    public static string NatureName(int nature) => nature switch
    {
        1 => "Invoices for outward supply",
        4 => "Debit notes",
        5 => "Credit notes",
        _ => "Other",
    };
}

/// <summary>GSTR-1 for one GSTIN and month, table by table, and what would make GSTN refuse it or misstate it.</summary>
public sealed record Gstr1Return(
    string Gstin,
    int Year,
    int Month,
    IReadOnlyList<Gstr1Document> B2b,
    IReadOnlyList<Gstr1Document> B2cl,
    IReadOnlyList<Gstr1B2csRow> B2cs,
    IReadOnlyList<Gstr1Document> Cdnr,
    IReadOnlyList<Gstr1Document> Cdnur,
    IReadOnlyList<Gstr1NilRow> Nil,
    IReadOnlyList<Gstr1HsnRow> HsnB2b,
    IReadOnlyList<Gstr1HsnRow> HsnB2c,
    IReadOnlyList<Gstr1DocumentsIssued> DocumentsIssued,
    IReadOnlyList<string> Problems)
{
    /// <summary>GSTN's return period: MMYYYY.</summary>
    public string Period => string.Create(CultureInfo.InvariantCulture, $"{Month:D2}{Year:D4}");
}

/// <summary>GSTR-3B table 3.1 (outward supplies), 3.2 (inter-state to unregistered) and 4 (input tax credit).</summary>
public sealed record Gstr3bReturn(
    string Gstin,
    int Year,
    int Month,
    GstReturnAmounts OutwardTaxable,
    GstReturnAmounts ZeroRated,
    decimal NilAndExempt,
    GstReturnAmounts InwardReverseCharge,
    decimal NonGst,
    IReadOnlyList<Gstr1B2csRow> InterStateToUnregistered,
    GstReturnAmounts ItcAllOther,
    GstReturnAmounts ItcReversed,
    int PurchaseInvoices,
    int PurchasesWithoutGstin,
    decimal ExemptShareOfTurnover,
    IReadOnlyList<string> Notes)
{
    public GstReturnAmounts ItcNet => ItcAllOther.Plus(ItcReversed.Times(-1m));
}

/// <summary>
/// Builds GSTR-1 and GSTR-3B from the month's outward documents (and purchases), as GSTN classifies them:
/// <list type="bullet">
/// <item>B2B: invoices to a registered buyer, their taxable lines by rate.</item>
/// <item>B2CL: invoices to an unregistered buyer in another state above ₹1,00,000 (₹2,50,000 before 1 August 2024,
/// notification 12/2024-CT).</item>
/// <item>B2CS: the other taxable supplies to unregistered buyers, by place of supply and rate, with their credit and debit
/// notes netted in.</item>
/// <item>CDNR: notes to registered buyers; CDNUR: notes to unregistered buyers whose invoice was B2CL.</item>
/// <item>Table 8: nil-rated, exempt and non-GST lines of invoices (a hospital's healthcare services are exempt).</item>
/// <item>Table 12: every line by HSN or SAC, unit and rate, invoices and debit notes adding and credit notes taking
/// off, split into B2B and B2C as GSTN asks from May 2025.</item>
/// <item>Table 13: each series' first and last number, how many were issued and how many cancelled.</item>
/// </list>
/// Cancelled documents count only in table 13. Amounts are rounded to the paisa.
/// </summary>
public static class GstReturnBuilder
{
    /// <summary>The invoice value above which an inter-state invoice to an unregistered buyer is B2CL, on the invoice's date.</summary>
    public static decimal B2clThreshold(DateOnly date) => date >= new DateOnly(2024, 8, 1) ? 100000m : 250000m;

    public static Gstr1Return Gstr1(string gstin, int year, int month, IReadOnlyList<GstReturnDocument> documents)
    {
        var seller = Gstin.StateCode(gstin) ?? string.Empty;
        var problems = new List<string>();
        var b2b = new List<Gstr1Document>();
        var b2cl = new List<Gstr1Document>();
        var cdnr = new List<Gstr1Document>();
        var cdnur = new List<Gstr1Document>();
        var b2cs = new Dictionary<(bool, string, decimal), GstReturnAmounts>();
        var nil = new Dictionary<string, (decimal Nil, decimal Exempt, decimal NonGst)>(StringComparer.Ordinal);
        var hsnB2b = new Dictionary<(string, string, decimal), (string Description, decimal Quantity, GstReturnAmounts Amounts)>();
        var hsnB2c = new Dictionary<(string, string, decimal), (string Description, decimal Quantity, GstReturnAmounts Amounts)>();

        foreach (var doc in documents.Where(d => !d.IsCancelled).OrderBy(d => d.Date).ThenBy(d => d.Number, StringComparer.Ordinal))
        {
            var buyer = Gstin.Normalize(doc.BuyerGstin);
            var registered = buyer is not null;
            if (registered && !Gstin.IsWellFormed(buyer))
            {
                problems.Add($"{doc.Number}: {doc.BuyerGstin} is not a well-formed GSTIN; GSTN refuses it.");
            }

            var pos = Gstin.States.ContainsKey(doc.PlaceOfSupply) ? doc.PlaceOfSupply : seller;
            if (!Gstin.States.ContainsKey(doc.PlaceOfSupply))
            {
                problems.Add($"{doc.Number}: no place of supply; taken as the hospital's own state ({seller}).");
            }

            var inter = pos != seller;
            var taxable = doc.Lines.Where(l => l.Supply == GstSupplyKind.Taxable).ToList();
            var items = ByRate(taxable);
            var sign = doc.Kind == GstDocumentKind.CreditNote ? -1m : 1m;

            foreach (var line in doc.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Hsn))
                {
                    problems.Add($"{doc.Number}: {line.Description} has no {(line.IsService ? "SAC" : "HSN")} code; table 12 needs one.");
                }

                var key = (line.Hsn?.Trim() ?? string.Empty, line.Uqc, line.Supply == GstSupplyKind.Taxable ? line.Rate : 0m);
                var table = registered ? hsnB2b : hsnB2c;
                var before = table.GetValueOrDefault(key, (line.Description, 0m, GstReturnAmounts.Zero));
                table[key] = (before.Item1, before.Item2 + (sign * line.Quantity), before.Item3.Plus(GstReturnAmounts.Of(line).Times(sign)));
            }

            if (doc.Kind == GstDocumentKind.Invoice)
            {
                foreach (var line in doc.Lines.Where(l => l.Supply != GstSupplyKind.Taxable))
                {
                    var type = (inter ? "INTR" : "INTRA") + (registered ? "B2B" : "B2C");
                    var row = nil.GetValueOrDefault(type);
                    nil[type] = line.Supply switch
                    {
                        GstSupplyKind.NilRated => (row.Nil + line.Taxable, row.Exempt, row.NonGst),
                        GstSupplyKind.Exempt => (row.Nil, row.Exempt + line.Taxable, row.NonGst),
                        _ => (row.Nil, row.Exempt, row.NonGst + line.Taxable),
                    };
                }

                if (items.Count == 0)
                {
                    continue;
                }

                var document = new Gstr1Document(doc.Number, doc.Date, buyer, doc.BuyerName, pos, doc.Value, items);
                if (registered)
                {
                    b2b.Add(document);
                }
                else if (inter && doc.Value > B2clThreshold(doc.Date))
                {
                    b2cl.Add(document);
                }
                else
                {
                    AddB2cs(b2cs, inter, pos, taxable, 1m);
                }

                continue;
            }

            // A note.
            if (items.Count == 0)
            {
                continue;
            }

            var note = new Gstr1Document(doc.Number, doc.Date, buyer, doc.BuyerName, pos, doc.Value, items, doc.Kind);
            if (registered)
            {
                cdnr.Add(note);
                continue;
            }

            var originalPos = doc.OriginalPlaceOfSupply is { } op && Gstin.States.ContainsKey(op) ? op : pos;
            var originalDate = doc.OriginalDate ?? doc.Date;
            if (originalPos != seller && (doc.OriginalValue ?? 0m) > B2clThreshold(originalDate))
            {
                cdnur.Add(note);
            }
            else
            {
                AddB2cs(b2cs, inter, pos, taxable, sign);
            }
        }

        return new Gstr1Return(
            Gstin.Normalize(gstin) ?? gstin,
            year,
            month,
            b2b,
            b2cl,
            b2cs.OrderBy(r => r.Key.Item2, StringComparer.Ordinal).ThenBy(r => r.Key.Item3)
                .Select(r => new Gstr1B2csRow(r.Key.Item1, r.Key.Item2, r.Key.Item3, Round(r.Value))).ToList(),
            cdnr,
            cdnur,
            new[] { "INTRB2B", "INTRAB2B", "INTRB2C", "INTRAB2C" }.Where(nil.ContainsKey)
                .Select(t => new Gstr1NilRow(t, Money.Round(nil[t].Nil), Money.Round(nil[t].Exempt), Money.Round(nil[t].NonGst))).ToList(),
            Hsn(hsnB2b),
            Hsn(hsnB2c),
            Issued(documents),
            problems.Distinct(StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// GSTR-3B from the month's GSTR-1 and purchases. Table 4(B) reversals (rules 38, 42 and 43: credit on inputs used for
    /// exempt healthcare services) are left to the accountant: the share of exempt turnover is given to compute them.
    /// </summary>
    public static Gstr3bReturn Gstr3b(Gstr1Return gstr1, IReadOnlyList<GstPurchase> purchases)
    {
        var outward = GstReturnAmounts.Zero;
        foreach (var item in gstr1.B2b.Concat(gstr1.B2cl).SelectMany(d => d.Items))
        {
            outward = outward.Plus(item.Amounts);
        }

        foreach (var row in gstr1.B2cs)
        {
            outward = outward.Plus(row.Amounts);
        }

        foreach (var note in gstr1.Cdnr.Concat(gstr1.Cdnur))
        {
            var sign = note.Kind == GstDocumentKind.CreditNote ? -1m : 1m;
            foreach (var item in note.Items)
            {
                outward = outward.Plus(item.Amounts.Times(sign));
            }
        }

        var seller = Gstin.StateCode(gstr1.Gstin);
        var inter = new Dictionary<string, GstReturnAmounts>(StringComparer.Ordinal);
        foreach (var (pos, amounts) in gstr1.B2cl.SelectMany(d => d.Items.Select(i => (d.PlaceOfSupply, i.Amounts)))
                     .Concat(gstr1.Cdnur.SelectMany(d => d.Items.Select(i => (d.PlaceOfSupply, i.Amounts.Times(d.Kind == GstDocumentKind.CreditNote ? -1m : 1m)))))
                     .Concat(gstr1.B2cs.Where(r => r.InterState).Select(r => (r.PlaceOfSupply, r.Amounts))))
        {
            if (pos != seller)
            {
                inter[pos] = inter.GetValueOrDefault(pos, GstReturnAmounts.Zero).Plus(amounts);
            }
        }

        var nilExempt = gstr1.Nil.Sum(n => n.NilRated + n.Exempt);
        var nonGst = gstr1.Nil.Sum(n => n.NonGst);
        var itc = GstReturnAmounts.Zero;
        var withGstin = purchases.Where(p => Gstin.IsWellFormed(p.SupplierGstin)).ToList();
        foreach (var purchase in withGstin)
        {
            itc = itc.Plus(new GstReturnAmounts(purchase.Taxable, purchase.Igst, purchase.Cgst, purchase.Sgst, purchase.Cess));
        }

        var turnover = outward.Taxable + nilExempt + nonGst;
        var share = turnover <= 0m ? 0m : Math.Round(nilExempt / turnover, 4, MidpointRounding.AwayFromZero);
        var notes = new List<string>();
        if (itc.Tax > 0m && nilExempt > 0m)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Exempt and nil-rated supplies are {share:P2} of the month's turnover: input tax on goods and services used for them, and that share of common credit (CGST rule 42), is reversed in table 4(B)(1). HC360 does not know which purchases are common; the accountant enters the reversal."));
        }

        if (purchases.Count > withGstin.Count)
        {
            notes.Add($"{purchases.Count - withGstin.Count} purchase invoice(s) from suppliers without a GSTIN carry no input tax credit.");
        }

        notes.Add("Credit is shown as the hospital's purchase records hold it; claim it only as far as it appears in GSTR-2B (CGST rule 36(4)).");
        return new Gstr3bReturn(
            gstr1.Gstin, gstr1.Year, gstr1.Month, Round(outward), GstReturnAmounts.Zero, Money.Round(nilExempt), GstReturnAmounts.Zero, Money.Round(nonGst),
            inter.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new Gstr1B2csRow(true, p.Key, 0m, Round(p.Value))).ToList(),
            Round(itc), GstReturnAmounts.Zero, purchases.Count, purchases.Count - withGstin.Count, share, notes);
    }

    /// <summary>
    /// The GSTN offline tool's GSTR-1 JSON (the format its "Import files" takes and the portal's "Prepare offline" upload):
    /// dates as dd-mm-yyyy, amounts in rupees to the paisa.
    /// </summary>
    public static JsonObject Gstr1Json(Gstr1Return r)
    {
        var json = new JsonObject { ["gstin"] = r.Gstin, ["fp"] = r.Period, ["version"] = "GST3.2", ["hash"] = "hash" };
        if (r.B2b.Count > 0)
        {
            json["b2b"] = new JsonArray(r.B2b.GroupBy(d => d.BuyerGstin!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (JsonNode)new JsonObject
                {
                    ["ctin"] = g.Key,
                    ["inv"] = new JsonArray(g.Select(d => (JsonNode)new JsonObject
                    {
                        ["inum"] = d.Number,
                        ["idt"] = Date(d.Date),
                        ["val"] = Money.Round(d.Value),
                        ["pos"] = d.PlaceOfSupply,
                        ["rchrg"] = "N",
                        ["inv_typ"] = "R",
                        ["itms"] = Items(d.Items, igstOnly: false),
                    }).ToArray()),
                }).ToArray());
        }

        if (r.B2cl.Count > 0)
        {
            json["b2cl"] = new JsonArray(r.B2cl.GroupBy(d => d.PlaceOfSupply, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (JsonNode)new JsonObject
                {
                    ["pos"] = g.Key,
                    ["inv"] = new JsonArray(g.Select(d => (JsonNode)new JsonObject
                    {
                        ["inum"] = d.Number,
                        ["idt"] = Date(d.Date),
                        ["val"] = Money.Round(d.Value),
                        ["itms"] = Items(d.Items, igstOnly: true),
                    }).ToArray()),
                }).ToArray());
        }

        if (r.B2cs.Count > 0)
        {
            json["b2cs"] = new JsonArray(r.B2cs.Select(row =>
            {
                var item = new JsonObject
                {
                    ["sply_ty"] = row.InterState ? "INTER" : "INTRA",
                    ["pos"] = row.PlaceOfSupply,
                    ["typ"] = "OE",
                    ["txval"] = row.Amounts.Taxable,
                    ["rt"] = row.Rate,
                };
                Tax(item, row.Amounts, row.InterState);
                return (JsonNode)item;
            }).ToArray());
        }

        if (r.Cdnr.Count > 0)
        {
            json["cdnr"] = new JsonArray(r.Cdnr.GroupBy(d => d.BuyerGstin!, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (JsonNode)new JsonObject
                {
                    ["ctin"] = g.Key,
                    ["nt"] = new JsonArray(g.Select(d => (JsonNode)new JsonObject
                    {
                        ["ntty"] = d.Kind == GstDocumentKind.CreditNote ? "C" : "D",
                        ["nt_num"] = d.Number,
                        ["nt_dt"] = Date(d.Date),
                        ["val"] = Money.Round(d.Value),
                        ["pos"] = d.PlaceOfSupply,
                        ["rchrg"] = "N",
                        ["inv_typ"] = "R",
                        ["itms"] = Items(d.Items, igstOnly: false),
                    }).ToArray()),
                }).ToArray());
        }

        if (r.Cdnur.Count > 0)
        {
            json["cdnur"] = new JsonArray(r.Cdnur.Select(d => (JsonNode)new JsonObject
            {
                ["typ"] = "B2CL",
                ["ntty"] = d.Kind == GstDocumentKind.CreditNote ? "C" : "D",
                ["nt_num"] = d.Number,
                ["nt_dt"] = Date(d.Date),
                ["val"] = Money.Round(d.Value),
                ["pos"] = d.PlaceOfSupply,
                ["itms"] = Items(d.Items, igstOnly: true),
            }).ToArray());
        }

        if (r.Nil.Count > 0)
        {
            json["nil"] = new JsonObject
            {
                ["inv"] = new JsonArray(r.Nil.Select(n => (JsonNode)new JsonObject
                {
                    ["sply_ty"] = n.SupplyType,
                    ["nil_amt"] = n.NilRated,
                    ["expt_amt"] = n.Exempt,
                    ["ngsup_amt"] = n.NonGst,
                }).ToArray()),
            };
        }

        if (r.HsnB2b.Count + r.HsnB2c.Count > 0)
        {
            json["hsn"] = new JsonObject { ["hsn_b2b"] = HsnJson(r.HsnB2b), ["hsn_b2c"] = HsnJson(r.HsnB2c) };
        }

        if (r.DocumentsIssued.Count > 0)
        {
            json["doc_issue"] = new JsonObject
            {
                ["doc_det"] = new JsonArray(r.DocumentsIssued.GroupBy(d => d.Nature).OrderBy(g => g.Key).Select(g => (JsonNode)new JsonObject
                {
                    ["doc_num"] = g.Key,
                    ["docs"] = new JsonArray(g.Select((d, i) => (JsonNode)new JsonObject
                    {
                        ["num"] = i + 1,
                        ["from"] = d.From,
                        ["to"] = d.To,
                        ["totnum"] = d.Total,
                        ["cancel"] = d.Cancelled,
                        ["net_issue"] = d.Net,
                    }).ToArray()),
                }).ToArray()),
            };
        }

        return json;
    }

    private static List<Gstr1Item> ByRate(IEnumerable<GstReturnLine> lines) =>
        lines.GroupBy(l => l.Rate).OrderBy(g => g.Key)
            .Select(g => new Gstr1Item(g.Key, Round(g.Aggregate(GstReturnAmounts.Zero, (sum, l) => sum.Plus(GstReturnAmounts.Of(l))))))
            .Where(i => i.Amounts.Taxable != 0m || i.Amounts.Tax != 0m)
            .ToList();

    private static void AddB2cs(Dictionary<(bool, string, decimal), GstReturnAmounts> rows, bool inter, string pos, IEnumerable<GstReturnLine> lines, decimal sign)
    {
        foreach (var line in lines)
        {
            var key = (inter, pos, line.Rate);
            rows[key] = rows.GetValueOrDefault(key, GstReturnAmounts.Zero).Plus(GstReturnAmounts.Of(line).Times(sign));
        }
    }

    private static List<Gstr1HsnRow> Hsn(Dictionary<(string, string, decimal), (string Description, decimal Quantity, GstReturnAmounts Amounts)> rows) =>
        rows.OrderBy(r => r.Key.Item1, StringComparer.Ordinal).ThenBy(r => r.Key.Item3)
            .Select(r => new Gstr1HsnRow(r.Key.Item1, r.Value.Description, r.Key.Item2, Math.Round(r.Value.Quantity, 2), r.Key.Item3, Round(r.Value.Amounts)))
            .ToList();

    /// <summary>Table 13: per nature and series, the lowest and highest number (by length, then order), the count and the cancelled.</summary>
    private static List<Gstr1DocumentsIssued> Issued(IReadOnlyList<GstReturnDocument> documents) =>
        documents.GroupBy(d => (Nature: d.Kind switch { GstDocumentKind.DebitNote => 4, GstDocumentKind.CreditNote => 5, _ => 1 }, d.Series))
            .OrderBy(g => g.Key.Nature).ThenBy(g => g.Key.Series, StringComparer.Ordinal)
            .Select(g =>
            {
                var numbers = g.Select(d => d.Number).OrderBy(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList();
                return new Gstr1DocumentsIssued(g.Key.Nature, g.Key.Series, numbers[0], numbers[^1], g.Count(), g.Count(d => d.IsCancelled));
            })
            .ToList();

    private static JsonArray Items(IReadOnlyList<Gstr1Item> items, bool igstOnly) =>
        new(items.Select((item, index) =>
        {
            var detail = new JsonObject { ["txval"] = item.Amounts.Taxable, ["rt"] = item.Rate };
            Tax(detail, item.Amounts, igstOnly);
            return (JsonNode)new JsonObject { ["num"] = index + 1, ["itm_det"] = detail };
        }).ToArray());

    private static void Tax(JsonObject target, GstReturnAmounts amounts, bool igstOnly)
    {
        if (igstOnly || amounts.Igst != 0m)
        {
            target["iamt"] = amounts.Igst;
        }

        if (!igstOnly && (amounts.Cgst != 0m || amounts.Sgst != 0m || amounts.Igst == 0m))
        {
            target["camt"] = amounts.Cgst;
            target["samt"] = amounts.Sgst;
        }

        target["csamt"] = amounts.Cess;
    }

    private static JsonArray HsnJson(IReadOnlyList<Gstr1HsnRow> rows) =>
        new(rows.Select((row, index) => (JsonNode)new JsonObject
        {
            ["num"] = index + 1,
            ["hsn_sc"] = row.Hsn,
            ["desc"] = row.Description.Length <= 30 ? row.Description : row.Description[..30],
            ["uqc"] = row.Uqc,
            ["qty"] = row.Quantity,
            ["rt"] = row.Rate,
            ["txval"] = row.Amounts.Taxable,
            ["iamt"] = row.Amounts.Igst,
            ["camt"] = row.Amounts.Cgst,
            ["samt"] = row.Amounts.Sgst,
            ["csamt"] = row.Amounts.Cess,
        }).ToArray());

    private static GstReturnAmounts Round(GstReturnAmounts a) => new(Money.Round(a.Taxable), Money.Round(a.Igst), Money.Round(a.Cgst), Money.Round(a.Sgst), Money.Round(a.Cess));

    private static string Date(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
}
