using System.Globalization;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Settings;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>
/// The GST e-invoice register (GST rule 48(4)). E-invoicing applies to a hospital whose turnover crossed the notified
/// limit, from the date set in <c>gst.einvoice.from</c>. Its B2B tax invoices (bills to payers registered for GST, the
/// pharmacy's B2B sales) and the credit notes against them (a bill's credit notes, the pharmacy's returns) are turned
/// into the portal's JSON here; the hospital uploads it to the Invoice Registration Portal (directly or through its GST
/// suvidha provider) and records the IRN, acknowledgement number and signed QR code the portal gives back. The signed QR
/// code must name this document, and the document then prints it. A credit note goes after the invoice it reduces.
/// </summary>
public sealed class EInvoiceService(
    HospitalDbContext db,
    IEnumerable<IEInvoiceSource> sources,
    ISettingsService settings,
    ICurrentUser currentUser,
    TimeProvider time)
{
    public const string ApplicableFromSetting = "gst.einvoice.from";

    /// <summary>The portal takes an invoice up to 30 days after its date from a large taxpayer; reporting it later is refused.</summary>
    public const int ReportWithinDays = 30;

    public async Task<EInvoiceRegister> RegisterAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var today = BillingTime.Today(time);
        var end = to ?? today;
        var start = from ?? end.AddDays(-30);
        if (currentUser.FacilityId is not { } facility || await ApplicableFromAsync(facility, ct) is not { } applicable)
        {
            return new EInvoiceRegister(false, null, start, end, []);
        }

        if (start < applicable)
        {
            start = applicable;
        }

        var candidates = new List<EInvoiceCandidate>();
        if (start <= end)
        {
            foreach (var source in sources)
            {
                candidates.AddRange(await source.ListAsync(facility, start, end, ct));
            }
        }

        var ids = candidates.Select(c => c.Id).ToList();
        var registrations = (await db.Set<EInvoice>().AsNoTracking().Where(e => ids.Contains(e.SourceId)).ToListAsync(ct))
            .GroupBy(e => (e.Source, e.SourceId))
            .ToDictionary(g => g.Key, g => Latest(g));
        var rows = candidates
            .Select(c =>
            {
                var registration = registrations.GetValueOrDefault((c.Source, c.Id));
                var state = StateOf(c.IsCancelled, registration);
                var reportBy = c.Date.AddDays(ReportWithinDays);
                return new EInvoiceRow(c.Source, c.Id, c.Number, c.Date, c.BuyerGstin, c.BuyerName, c.Total, state, reportBy,
                    state == EInvoiceState.Pending && today > reportBy, registration?.Irn, registration?.AckNo, registration?.AckDate, c.Type,
                    c.PrecedingNumber);
            })
            .OrderBy(r => r.State == EInvoiceState.Pending ? 0 : 1)
            .ThenByDescending(r => r.Date)
            .ThenBy(r => r.Number, StringComparer.Ordinal)
            .ToList();
        return new EInvoiceRegister(true, applicable, start, end, rows);
    }

    public async Task<Result<EInvoiceDetail>> DetailAsync(string source, Guid id, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var (document, seller, applicable) = found.Value;
        var (_, problems) = InvoiceRegistrationPayload.Build(seller, document);
        return await DetailAsync(document, [.. problems, .. await PrecedingProblemsAsync(document, applicable, ct)], ct);
    }

    /// <summary>The e-invoice JSON to upload to the portal, or why the portal would refuse it.</summary>
    public async Task<Result<(string Number, JsonObject Payload)>> PayloadAsync(string source, Guid id, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var (document, seller, applicable) = found.Value;
        if (await LiveAsync(document.Source, document.Id, ct) is not null)
        {
            return Error.Rule("einvoice.registered", $"This {InvoiceRegistrationPayload.Noun(document)} already has an IRN.");
        }

        var (payload, problems) = InvoiceRegistrationPayload.Build(seller, document);
        var preceding = await PrecedingProblemsAsync(document, applicable, ct);
        return payload is null || preceding.Count > 0 ? Problems(document, [.. problems, .. preceding]) : (document.Number, payload);
    }

    public async Task<Result<EInvoiceDetail>> RecordAsync(string source, Guid id, RecordIrnRequest request, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var (document, seller, _) = found.Value;
        var noun = InvoiceRegistrationPayload.Noun(document);
        if (document.IsCancelled)
        {
            return Error.Rule("einvoice.document_cancelled", $"The {noun} is cancelled here; it needs no IRN.");
        }

        if (await LiveAsync(document.Source, document.Id, ct) is not null)
        {
            return Error.Conflict("einvoice.registered", $"This {noun} already has an IRN. Cancel it first to record another.");
        }

        var read = SignedQrCode.Read(request.SignedQrCode);
        if (read.IsFailure)
        {
            return read.Error!;
        }

        var qr = read.Value;
        var mismatches = SignedQrCode.Mismatches(qr, document, seller.Gstin);
        if (mismatches.Count > 0)
        {
            return Error.Field("signedQrCode", $"This signed QR code is not this {noun}'s: {string.Join(" ", mismatches)}");
        }

        if (await db.Set<EInvoice>().IgnoreQueryFilters().AnyAsync(e => e.Irn == qr.Irn, ct))
        {
            return Error.Conflict("einvoice.irn_exists", "This IRN is already recorded against an invoice.");
        }

        if (qr.IrnDt > time.GetUtcNow().AddMinutes(10))
        {
            return Error.Field("signedQrCode", "The signed QR code is dated in the future.");
        }

        var registration = new EInvoice
        {
            FacilityId = document.FacilityId,
            Source = document.Source,
            SourceId = document.Id,
            DocumentNumber = document.Number,
            DocumentDate = document.Date,
            BuyerGstin = Gstin.Normalize(document.Buyer.Gstin)!,
            InvoiceValue = document.Total,
            Irn = qr.Irn,
            AckNo = request.AckNo.Trim(),
            AckDate = qr.IrnDt.ToUniversalTime(),
            SignedQrCode = request.SignedQrCode.Trim(),
            RecordedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            PrecedingSource = document.Preceding?.Source,
            PrecedingId = document.Preceding?.Id,
        };
        db.Add(registration);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(document, [], ct);
    }

    public async Task<Result<EInvoiceDetail>> CancelAsync(string source, Guid id, CancelIrnRequest request, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var (document, seller, applicable) = found.Value;
        var registration = await db.Set<EInvoice>()
            .Where(e => e.Source == document.Source && e.SourceId == document.Id && e.Status == EInvoiceStatus.Registered)
            .FirstOrDefaultAsync(ct);
        if (registration is null)
        {
            return Error.Rule("einvoice.not_registered", $"This {InvoiceRegistrationPayload.Noun(document)} has no live IRN to cancel.");
        }

        // A credit note with an IRN names this invoice: cancelling the invoice alone would leave the portal, the buyer's
        // GSTR-2B and the hospital's GSTR-1 with a credit note against an invoice they no longer have.
        var credits = await db.Set<EInvoice>().AsNoTracking()
            .Where(e => e.PrecedingSource == document.Source && e.PrecedingId == document.Id && e.Status == EInvoiceStatus.Registered)
            .Select(e => e.DocumentNumber)
            .ToListAsync(ct);
        if (credits.Count > 0)
        {
            return Error.Rule("einvoice.credited",
                $"Credit note {string.Join(", ", credits)} against this invoice has an IRN. Cancel its IRN on the portal and record that first.");
        }

        db.SetExpectedVersion(registration, request.Version);
        var cancelled = registration.Cancel(request.Reason, request.Remark, time.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        await db.SaveChangesAsync(ct);
        var (_, problems) = InvoiceRegistrationPayload.Build(seller, document);
        return await DetailAsync(document, [.. problems, .. await PrecedingProblemsAsync(document, applicable, ct)], ct);
    }

    /// <summary>The live IRN of an invoice, if any.</summary>
    public Task<EInvoice?> LiveAsync(string source, Guid id, CancellationToken ct) =>
        db.Set<EInvoice>().AsNoTracking().FirstOrDefaultAsync(e => e.Source == source && e.SourceId == id && e.Status == EInvoiceStatus.Registered, ct);

    public static EInvoiceStamp Stamp(EInvoice e) =>
        new(e.Irn, e.AckNo, e.AckDate, e.SignedQrCode, DocumentAssets.QrCode(e.SignedQrCode).DataUri);

    /// <summary>
    /// A credit note goes to the portal after the invoice it reduces. An invoice of e-invoicing's time needs its own IRN
    /// first, or the portal, the buyer's GSTR-2B and the hospital's GSTR-1 would hold a credit note against an invoice
    /// they never had. An invoice from before e-invoicing applied has no IRN, and its credit note goes without one.
    /// </summary>
    private async Task<IReadOnlyList<string>> PrecedingProblemsAsync(EInvoiceDocument document, DateOnly applicable, CancellationToken ct)
    {
        if (document.Preceding is not { } preceding || preceding.Date < applicable || await LiveAsync(preceding.Source, preceding.Id, ct) is not null)
        {
            return [];
        }

        var reportBy = preceding.Date.AddDays(ReportWithinDays);
        var by = reportBy.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return BillingTime.Today(time) <= reportBy
            ? [$"Invoice {preceding.Number} has no IRN yet: record its IRN first (the portal takes it until {by}); this credit note names it."]
            : [$"Invoice {preceding.Number} was due on the portal by {by} and has no IRN. Settle it with the hospital's GST adviser before reporting a credit note against it."];
    }

    private async Task<Result<(EInvoiceDocument Document, EInvoiceSeller Seller, DateOnly Applicable)>> DocumentAsync(string source, Guid id, CancellationToken ct)
    {
        var from = sources.FirstOrDefault(s => s.Source == source);
        var document = from is null ? null : await from.FindAsync(id, ct);
        if (document is null)
        {
            return Error.NotFound("B2B tax invoice or credit note", id);
        }

        if (await ApplicableFromAsync(document.FacilityId, ct) is not { } applicable)
        {
            return Error.Rule("einvoice.not_applicable",
                $"E-invoicing is not switched on for this hospital. Set the date it applies from ({ApplicableFromSetting}) under Hospital setup.");
        }

        if (document.Date < applicable)
        {
            return Error.Rule("einvoice.not_applicable",
                $"E-invoicing applies to documents from {applicable.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}; this one is older.");
        }

        var facility = await db.Set<Facility>().AsNoTracking().FirstAsync(f => f.Id == document.FacilityId, ct);
        return (document, new EInvoiceSeller(facility.Gstin, facility.LegalName ?? facility.Name, facility.Address, facility.Phone, facility.Email), applicable);
    }

    private async Task<EInvoiceDetail> DetailAsync(EInvoiceDocument document, IReadOnlyList<string> problems, CancellationToken ct)
    {
        var registration = Latest(await db.Set<EInvoice>().AsNoTracking()
            .Where(e => e.Source == document.Source && e.SourceId == document.Id)
            .ToListAsync(ct));
        var now = time.GetUtcNow();
        return new EInvoiceDetail(
            document.Source, document.Id, document.Number, document.Date, document.Buyer.Gstin, document.Buyer.LegalName, document.PlaceOfSupply,
            document.Items.Count, document.Total, StateOf(document.IsCancelled, registration),
            registration?.Status == EInvoiceStatus.Registered ? [] : problems,
            registration is null
                ? null
                : new EInvoiceRegistrationDto(
                    registration.Irn, registration.AckNo, registration.AckDate, registration.Status, registration.RecordedByName, registration.CreatedAt,
                    registration.CancelledAt, registration.CancelReason, registration.CancelRemark, registration.AckDate + EInvoice.CancelWindow,
                    registration.CanCancel(now), DocumentAssets.QrCode(registration.SignedQrCode).DataUri, registration.Version),
            document.Type,
            document.Preceding?.Number,
            document.Preceding?.Date);
    }

    private async Task<DateOnly?> ApplicableFromAsync(Guid facilityId, CancellationToken ct) =>
        DateOnly.TryParseExact(await settings.GetAsync<string?>(ApplicableFromSetting, facilityId, ct), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;

    private static EInvoice? Latest(IEnumerable<EInvoice> registrations) =>
        registrations.OrderBy(e => e.Status == EInvoiceStatus.Registered ? 0 : 1).ThenByDescending(e => e.AckDate).FirstOrDefault();

    private static EInvoiceState StateOf(bool documentCancelled, EInvoice? registration) => registration?.Status switch
    {
        EInvoiceStatus.Registered => EInvoiceState.Registered,
        EInvoiceStatus.Cancelled => documentCancelled ? EInvoiceState.NotNeeded : EInvoiceState.IrnCancelled,
        _ => documentCancelled ? EInvoiceState.NotNeeded : EInvoiceState.Pending,
    };

    private static Error Problems(EInvoiceDocument document, IReadOnlyList<string> problems) =>
        Error.Rule("einvoice.invalid", $"The portal would refuse this {InvoiceRegistrationPayload.Noun(document)}: {string.Join(" ", problems)}");
}

/// <summary>Billing's own B2B tax invoices: finalized bills with GST, to a payer registered for GST.</summary>
internal sealed class BillEInvoiceSource(HospitalDbContext db) : IEInvoiceSource
{
    public const string Key = "bill";

    public string Source => Key;

    public async Task<IReadOnlyList<EInvoiceCandidate>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (start, end) = BillingTime.Range(from, to);
        var bills = await (
                from b in db.Set<Bill>().AsNoTracking()
                join p in db.Set<Payer>() on b.PayerId equals p.Id
                where b.FacilityId == facilityId && b.Status != BillStatus.Draft && b.TaxAmount != 0 && p.Gstin != null
                      && b.FinalizedAt >= start && b.FinalizedAt < end
                select new { b.Id, b.BillNumber, b.FinalizedAt, p.Gstin, p.Name, b.NetAmount, b.Status })
            .ToListAsync(cancellationToken);
        return bills
            .Select(b => new EInvoiceCandidate(Source, b.Id, b.BillNumber ?? string.Empty, BillingTime.LocalDate(b.FinalizedAt!.Value), b.Gstin!, b.Name,
                b.NetAmount, b.Status == BillStatus.Cancelled))
            .ToList();
    }

    public async Task<EInvoiceDocument?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var bill = await db.Set<Bill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (bill is not { Status: not BillStatus.Draft, PayerId: { } payerId, FinalizedAt: { } finalizedAt } || bill.TaxAmount == 0)
        {
            return null;
        }

        var payer = await db.Set<Payer>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == payerId, cancellationToken);
        if (payer?.Gstin is not { } gstin)
        {
            return null;
        }

        var items = bill.Lines
            .OrderBy(l => l.SortOrder)
            .Select(l => new EInvoiceItem(
                l.Description, IsService: true, l.SacCode, l.Quantity, l.UnitPrice, l.GrossAmount, l.GrossAmount - l.TaxableAmount, l.TaxableAmount,
                l.TaxRate, l.IgstAmount, l.CgstAmount, l.SgstAmount, l.TaxableAmount + l.IgstAmount + l.CgstAmount + l.SgstAmount))
            .ToList();
        return new EInvoiceDocument(
            Source, bill.Id, bill.FacilityId, bill.BillNumber ?? string.Empty, BillingTime.LocalDate(finalizedAt),
            new EInvoiceBuyer(gstin, payer.Name, payer.Address, payer.Phone, payer.Email), bill.PlaceOfSupply ?? Gstin.StateCode(gstin) ?? string.Empty,
            items, bill.RoundOffAmount, bill.NetAmount, bill.Status == BillStatus.Cancelled,
            bill.Lines.Any(l => l.Quantity < 0) ? "The bill has returned services; report it to the portal with a credit note for them." : null);
    }
}

/// <summary>
/// Billing's credit notes that are e-invoices (GST rule 48(4)): a credit note (not a write-off) that names the services it
/// corrects, on a bill with GST to a payer registered for GST, issued while the bill's GST could still be reduced. It goes
/// to the portal as a <c>CRN</c> to the bill's buyer, naming the bill, with each service at the taxable value and GST it
/// takes off; a value credit is one unit of the service at that value.
/// </summary>
internal sealed class CreditNoteEInvoiceSource(HospitalDbContext db) : IEInvoiceSource
{
    public const string Key = "creditnote";

    public string Source => Key;

    public async Task<IReadOnlyList<EInvoiceCandidate>> ListAsync(Guid facilityId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var (start, end) = BillingTime.Range(from, to);
        var notes = await (
                from c in db.Set<CreditNote>().AsNoTracking()
                join b in db.Set<Bill>() on c.BillId equals b.Id
                join p in db.Set<Payer>() on b.PayerId equals p.Id
                where c.FacilityId == facilityId && c.Kind == CreditNoteKind.CreditNote && !c.GstTimeBarred && b.TaxAmount != 0 && p.Gstin != null
                      && c.IssuedAt >= start && c.IssuedAt < end && db.Set<CreditNoteLine>().Any(l => l.CreditNoteId == c.Id)
                select new { c.Id, c.Number, c.IssuedAt, p.Gstin, p.Name, c.Amount, b.BillNumber })
            .ToListAsync(cancellationToken);
        return notes
            .Select(n => new EInvoiceCandidate(Source, n.Id, n.Number, BillingTime.LocalDate(n.IssuedAt), n.Gstin!, n.Name, n.Amount, IsCancelled: false,
                EInvoiceDocumentTypes.CreditNote, n.BillNumber))
            .ToList();
    }

    public async Task<EInvoiceDocument?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var note = await db.Set<CreditNote>().AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (note is not { Kind: CreditNoteKind.CreditNote, GstTimeBarred: false } || note.Lines.Count == 0)
        {
            return null;
        }

        var bill = await db.Set<Bill>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == note.BillId, cancellationToken);
        if (bill is not { PayerId: { } payerId, FinalizedAt: { } invoiced } || bill.TaxAmount == 0)
        {
            return null;
        }

        var payer = await db.Set<Payer>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == payerId, cancellationToken);
        if (payer?.Gstin is not { } gstin)
        {
            return null;
        }

        var items = note.Lines
            .OrderBy(l => l.SortOrder)
            .Select(l => new EInvoiceItem(
                l.Description, IsService: true, l.SacCode, 1m, l.TaxableAmount, l.TaxableAmount, 0m, l.TaxableAmount, l.GstRate, l.IgstAmount,
                l.CgstAmount, l.SgstAmount, l.Amount))
            .ToList();
        return new EInvoiceDocument(
            Source, note.Id, note.FacilityId, note.Number, BillingTime.LocalDate(note.IssuedAt),
            new EInvoiceBuyer(gstin, payer.Name, payer.Address, payer.Phone, payer.Email), bill.PlaceOfSupply ?? Gstin.StateCode(gstin) ?? string.Empty,
            items, 0m, note.Amount, IsCancelled: false, Type: EInvoiceDocumentTypes.CreditNote,
            Preceding: new EInvoicePreceding(BillEInvoiceSource.Key, bill.Id, bill.BillNumber ?? string.Empty, BillingTime.LocalDate(invoiced)));
    }
}

/// <summary>The register as other modules see it: an invoice's live IRN, for its print and to keep it from being cancelled.</summary>
internal sealed class EInvoiceRegisterLookup(HospitalDbContext db) : IEInvoiceRegister
{
    public async Task<EInvoiceStamp?> FindAsync(string source, Guid id, CancellationToken cancellationToken)
    {
        var live = await db.Set<EInvoice>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Source == source && e.SourceId == id && e.Status == EInvoiceStatus.Registered, cancellationToken);
        return live is null ? null : EInvoiceService.Stamp(live);
    }
}
