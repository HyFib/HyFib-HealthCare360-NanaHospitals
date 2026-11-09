using System.Globalization;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The GST returns of the signed-in facility's GSTIN for a month: every facility registered under that GSTIN is in them
/// (GSTR-1 is filed per GSTIN, not per facility). The outward documents come from every <see cref="IGstReturnSource"/>
/// (Billing, the pharmacy), the purchases from every <see cref="IGstPurchaseSource"/> (the stores).
/// </summary>
public sealed class GstReturnService(
    HospitalDbContext db,
    IEnumerable<IGstReturnSource> sources,
    IEnumerable<IGstPurchaseSource> purchases,
    ICurrentUser currentUser)
{
    public async Task<Result<Gstr1Return>> Gstr1Async(int year, int month, CancellationToken ct)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var (gstin, facilities) = scope.Value;
        var (from, to) = Month(year, month);
        var documents = new List<GstReturnDocument>();
        foreach (var source in sources)
        {
            documents.AddRange(await source.OutwardAsync(facilities, from, to, ct));
        }

        return GstReturnBuilder.Gstr1(gstin, year, month, documents);
    }

    public async Task<Result<Gstr3bReturn>> Gstr3bAsync(int year, int month, CancellationToken ct)
    {
        var gstr1 = await Gstr1Async(year, month, ct);
        if (gstr1.IsFailure)
        {
            return gstr1.Error!;
        }

        var (_, facilities) = (await ScopeAsync(ct)).Value;
        var (from, to) = Month(year, month);
        var bought = new List<GstPurchase>();
        foreach (var source in purchases)
        {
            bought.AddRange(await source.InwardAsync(facilities, from, to, ct));
        }

        return GstReturnBuilder.Gstr3b(gstr1.Value, bought);
    }

    public async Task<Result<(string FileName, JsonObject Json)>> Gstr1JsonAsync(int year, int month, CancellationToken ct)
    {
        var gstr1 = await Gstr1Async(year, month, ct);
        return gstr1.IsFailure
            ? gstr1.Error!
            : ($"GSTR1_{gstr1.Value.Gstin}_{gstr1.Value.Period}.json", GstReturnBuilder.Gstr1Json(gstr1.Value));
    }

    private async Task<Result<(string Gstin, IReadOnlyCollection<Guid> Facilities)>> ScopeAsync(CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility: GST returns are of its GSTIN.");
        }

        var own = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == facilityId).Select(f => f.Gstin).FirstOrDefaultAsync(ct);
        if (Gstin.Normalize(own) is not { } gstin || !Gstin.IsWellFormed(gstin))
        {
            return Error.Rule("gst.no_gstin", "Enter the facility's GSTIN under Hospital setup: returns are filed per GSTIN.");
        }

        var all = await db.Set<Facility>().AsNoTracking().IgnoreQueryFilters().Where(f => f.Gstin != null).Select(f => new { f.Id, f.Gstin }).ToListAsync(ct);
        return (gstin, all.Where(f => Gstin.Normalize(f.Gstin) == gstin).Select(f => f.Id).ToList());
    }

    private static (DateOnly From, DateOnly To) Month(int year, int month)
    {
        var from = new DateOnly(year, month, 1);
        return (from, from.AddMonths(1).AddDays(-1));
    }
}

/// <summary>
/// Billing's outward documents for the GST returns: finalized bills (a bill to a payer registered for GST is a B2B
/// invoice to it, as for the e-invoice), credit notes (not write-offs, nor notes issued after the bill's GST could be
/// reduced) and debit notes. A line with GST is taxable; a line without is an exempt healthcare service.
/// </summary>
internal sealed class BillingGstReturnSource(HospitalDbContext db, IEnumerable<IVoidedDocumentNumbers> voided) : IGstReturnSource
{
    public async Task<IReadOnlyList<GstReturnDocument>> OutwardAsync(IReadOnlyCollection<Guid> facilityIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var (start, end) = BillingTime.Range(from, to);
        var payers = await db.Set<Payer>().AsNoTracking().Select(p => new { p.Id, p.Name, p.Gstin }).ToDictionaryAsync(p => p.Id, ct);
        var documents = new List<GstReturnDocument>();

        var bills = await db.Set<Bill>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(b => b.Lines)
            .Where(b => facilityIds.Contains(b.FacilityId) && b.Status != BillStatus.Draft && b.FinalizedAt >= start && b.FinalizedAt < end && b.BillNumber != null)
            .ToListAsync(ct);
        foreach (var bill in bills)
        {
            var payer = bill.PayerId is { } pid ? payers.GetValueOrDefault(pid) : null;
            documents.Add(new GstReturnDocument(
                "bill", bill.Id, GstDocumentKind.Invoice, bill.BillNumber!, BillingTime.LocalDate(bill.FinalizedAt!.Value), payer?.Gstin,
                payer?.Gstin is not null ? payer.Name : "Patient", bill.PlaceOfSupply ?? string.Empty, bill.NetAmount,
                bill.Status is BillStatus.Cancelled or BillStatus.Refunded,
                bill.Lines.OrderBy(l => l.SortOrder).Select(l => Line(l.SacCode, l.Description, l.TaxRate, l.TaxableAmount, l.IgstAmount, l.CgstAmount, l.SgstAmount)).ToList(),
                bill.Type == BillType.InPatient ? "IP bills" : "OP bills"));
        }

        var credits = await (
                from c in db.Set<CreditNote>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(c => c.Lines)
                join b in db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on c.BillId equals b.Id
                where facilityIds.Contains(c.FacilityId) && c.Kind != CreditNoteKind.WriteOff && c.IssuedAt >= start && c.IssuedAt < end
                select new { Note = c, b.PayerId, b.BillNumber, b.FinalizedAt, b.NetAmount, b.PlaceOfSupply })
            .ToListAsync(ct);
        foreach (var c in credits)
        {
            var payer = c.PayerId is { } pid ? payers.GetValueOrDefault(pid) : null;
            // A note issued after the bill's GST could be reduced lowers no GST: it is not a GST credit note.
            var lines = c.Note.GstTimeBarred ? [] : c.Note.Lines.OrderBy(l => l.SortOrder)
                .Select(l => Line(l.SacCode, l.Description, l.GstRate, l.TaxableAmount, l.IgstAmount, l.CgstAmount, l.SgstAmount)).ToList();
            documents.Add(new GstReturnDocument(
                "creditnote", c.Note.Id, GstDocumentKind.CreditNote, c.Note.Number, BillingTime.LocalDate(c.Note.IssuedAt), payer?.Gstin,
                payer?.Gstin is not null ? payer.Name : "Patient", c.PlaceOfSupply ?? string.Empty, c.Note.Amount, IsCancelled: false, lines, "Credit notes",
                c.BillNumber, c.FinalizedAt is { } f ? BillingTime.LocalDate(f) : null, c.NetAmount, c.PlaceOfSupply));
        }

        var debits = await (
                from n in db.Set<DebitNote>().AsNoTracking().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(n => n.Lines)
                join b in db.Set<Bill>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on n.BillId equals b.Id
                where facilityIds.Contains(n.FacilityId) && n.IssuedAt >= start && n.IssuedAt < end
                select new { Note = n, b.PayerId, b.BillNumber, b.FinalizedAt, b.NetAmount, b.PlaceOfSupply })
            .ToListAsync(ct);
        foreach (var d in debits)
        {
            var payer = d.Note.Party == DebitNoteParty.Payer && d.PayerId is { } pid ? payers.GetValueOrDefault(pid) : null;
            documents.Add(new GstReturnDocument(
                "debitnote", d.Note.Id, GstDocumentKind.DebitNote, d.Note.Number, BillingTime.LocalDate(d.Note.IssuedAt), payer?.Gstin,
                payer?.Gstin is not null ? payer.Name : "Patient", d.PlaceOfSupply ?? string.Empty, d.Note.Amount, IsCancelled: false,
                d.Note.Lines.OrderBy(l => l.SortOrder).Select(l => Line(l.SacCode, l.Description, l.GstRate, l.TaxableAmount, l.IgstAmount, l.CgstAmount, l.SgstAmount)).ToList(),
                "Debit notes", d.BillNumber, d.FinalizedAt is { } f ? BillingTime.LocalDate(f) : null, d.NetAmount, d.PlaceOfSupply));
        }

        // GSTR-1 table 13, "Documents issued during the tax period": per nature and series, Sr. No. From and To, Total number,
        // Cancelled and Net issued (Form GSTR-1). A bill number reserved for a downtime and voided unused lies inside the
        // series' From–To range without a bill: it is counted in Total and in Cancelled, so Net issued is what was billed.
        var series = new Dictionary<string, string>(StringComparer.Ordinal) { [BillingNumberSeries.OpBill.Key] = "OP bills", [BillingNumberSeries.IpBill.Key] = "IP bills" };
        foreach (var source in voided)
        {
            foreach (var number in await source.VoidedAsync(facilityIds, from, to, ct))
            {
                if (series.TryGetValue(number.SeriesKey, out var name))
                {
                    documents.Add(new GstReturnDocument("voided", Guid.Empty, GstDocumentKind.Invoice, number.Number, number.VoidedOn, null, "Voided unused", string.Empty, 0m,
                        true, [], name));
                }
            }
        }

        return documents;
    }

    /// <summary>A service line: taxable when it carries GST, otherwise an exempt healthcare service; services have no quantity in table 12.</summary>
    private static GstReturnLine Line(string? sac, string description, decimal rate, decimal taxable, decimal igst, decimal cgst, decimal sgst) =>
        new(sac, description, IsService: true, "NA", 0m, rate, rate > 0m ? GstSupplyKind.Taxable : GstSupplyKind.Exempt, taxable, igst, cgst, sgst);
}
