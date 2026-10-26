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
/// pharmacy's B2B sales) are turned into the portal's JSON here; the hospital uploads it to the Invoice Registration
/// Portal (directly or through its GST suvidha provider) and records the IRN, acknowledgement number and signed QR code
/// the portal gives back. The signed QR code must name this invoice, and the invoice then prints it.
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
                    state == EInvoiceState.Pending && today > reportBy, registration?.Irn, registration?.AckNo, registration?.AckDate);
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

        var (document, seller) = found.Value;
        var (_, problems) = InvoiceRegistrationPayload.Build(seller, document);
        return await DetailAsync(document, problems, ct);
    }

    /// <summary>The e-invoice JSON to upload to the portal, or why the portal would refuse it.</summary>
    public async Task<Result<(string Number, JsonObject Payload)>> PayloadAsync(string source, Guid id, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var (document, seller) = found.Value;
        if (await LiveAsync(document.Source, document.Id, ct) is not null)
        {
            return Error.Rule("einvoice.registered", "This invoice already has an IRN.");
        }

        var (payload, problems) = InvoiceRegistrationPayload.Build(seller, document);
        return payload is null ? Problems(problems) : (document.Number, payload);
    }

    public async Task<Result<EInvoiceDetail>> RecordAsync(string source, Guid id, RecordIrnRequest request, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var (document, seller) = found.Value;
        if (document.IsCancelled)
        {
            return Error.Rule("einvoice.document_cancelled", "The invoice is cancelled here; it needs no IRN.");
        }

        if (await LiveAsync(document.Source, document.Id, ct) is not null)
        {
            return Error.Conflict("einvoice.registered", "This invoice already has an IRN. Cancel it first to record another.");
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
            return Error.Field("signedQrCode", $"This signed QR code is not this invoice's: {string.Join(" ", mismatches)}");
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

        var (document, seller) = found.Value;
        var registration = await db.Set<EInvoice>()
            .Where(e => e.Source == document.Source && e.SourceId == document.Id && e.Status == EInvoiceStatus.Registered)
            .FirstOrDefaultAsync(ct);
        if (registration is null)
        {
            return Error.Rule("einvoice.not_registered", "This invoice has no live IRN to cancel.");
        }

        db.SetExpectedVersion(registration, request.Version);
        var cancelled = registration.Cancel(request.Reason, request.Remark, time.GetUtcNow());
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        await db.SaveChangesAsync(ct);
        var (_, problems) = InvoiceRegistrationPayload.Build(seller, document);
        return await DetailAsync(document, problems, ct);
    }

    /// <summary>The live IRN of an invoice, if any.</summary>
    public Task<EInvoice?> LiveAsync(string source, Guid id, CancellationToken ct) =>
        db.Set<EInvoice>().AsNoTracking().FirstOrDefaultAsync(e => e.Source == source && e.SourceId == id && e.Status == EInvoiceStatus.Registered, ct);

    public static EInvoiceStamp Stamp(EInvoice e) =>
        new(e.Irn, e.AckNo, e.AckDate, e.SignedQrCode, DocumentAssets.QrCode(e.SignedQrCode).DataUri);

    private async Task<Result<(EInvoiceDocument Document, EInvoiceSeller Seller)>> DocumentAsync(string source, Guid id, CancellationToken ct)
    {
        var from = sources.FirstOrDefault(s => s.Source == source);
        var document = from is null ? null : await from.FindAsync(id, ct);
        if (document is null)
        {
            return Error.NotFound("B2B tax invoice", id);
        }

        if (await ApplicableFromAsync(document.FacilityId, ct) is not { } applicable)
        {
            return Error.Rule("einvoice.not_applicable",
                $"E-invoicing is not switched on for this hospital. Set the date it applies from ({ApplicableFromSetting}) under Hospital setup.");
        }

        if (document.Date < applicable)
        {
            return Error.Rule("einvoice.not_applicable",
                $"E-invoicing applies to invoices from {applicable.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}; this one is older.");
        }

        var facility = await db.Set<Facility>().AsNoTracking().FirstAsync(f => f.Id == document.FacilityId, ct);
        return (document, new EInvoiceSeller(facility.Gstin, facility.LegalName ?? facility.Name, facility.Address, facility.Phone, facility.Email));
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
                    registration.CanCancel(now), DocumentAssets.QrCode(registration.SignedQrCode).DataUri, registration.Version));
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

    private static Error Problems(IReadOnlyList<string> problems) =>
        Error.Rule("einvoice.invalid", $"The portal would refuse this invoice: {string.Join(" ", problems)}");
}

/// <summary>Billing's own B2B tax invoices: finalized bills with GST, to a payer registered for GST.</summary>
internal sealed class BillEInvoiceSource(HospitalDbContext db) : IEInvoiceSource
{
    public string Source => "bill";

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
