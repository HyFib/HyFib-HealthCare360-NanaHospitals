using System.Globalization;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.EInvoices;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Notifications;
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
    IInvoiceRegistrationPortal portal,
    IAuditLogger audit,
    INotificationService notifications,
    TimeProvider time)
{
    public const string ApplicableFromSetting = "gst.einvoice.from";

    /// <summary>
    /// The consignment value above which goods move with an e-way bill (CGST rule 138: ₹50,000, lower in some states for
    /// movement within them).
    /// </summary>
    public const string EwayBillThresholdSetting = "gst.ewaybill.threshold";

    public const decimal DefaultEwayBillThreshold = 50000m;

    /// <summary>The portal takes an invoice up to 30 days after its date from a large taxpayer; reporting it later is refused.</summary>
    public const int ReportWithinDays = 30;

    /// <summary>
    /// How many days a document may wait for its IRN before the morning reminder names it (<see cref="RemindAsync"/>); an
    /// IRN is due when the document is issued, and asked for automatically then when the facility is connected to the portal.
    /// </summary>
    public const string AlertAfterDaysSetting = "gst.einvoice.alert_after_days";

    public const int DefaultAlertAfterDays = 2;

    public async Task<EInvoiceRegister> RegisterAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var end = to ?? BillingTime.Today(time);
        var start = from ?? end.AddDays(-30);
        if (currentUser.FacilityId is not { } facility || await ApplicableFromAsync(facility, ct) is not { } applicable)
        {
            return new EInvoiceRegister(false, null, start, end, []);
        }

        if (start < applicable)
        {
            start = applicable;
        }

        return new EInvoiceRegister(true, applicable, start, end, await RowsAsync(facility, start, end, ct));
    }

    /// <summary>
    /// Tells holders of <see cref="BillingPermissions.EInvoice"/> at the facility which documents have waited longer than
    /// it allows (<see cref="AlertAfterDaysSetting"/>) for an IRN, as of <paramref name="today"/> (<see cref="EInvoiceReminders"/>):
    /// the register's own pending rows, since e-invoicing applied. Nothing when none has waited that long.
    /// </summary>
    internal async Task<EInvoiceReminder?> RemindAsync(Guid facilityId, DateOnly today, CancellationToken ct)
    {
        if (await ApplicableFromAsync(facilityId, ct) is not { } applicable)
        {
            return null;
        }

        var afterDays = await settings.GetAsync<int?>(AlertAfterDaysSetting, facilityId, ct) is { } days and > 0 ? days : DefaultAlertAfterDays;
        var reminder = EInvoiceReminders.Of(await RowsAsync(facilityId, applicable, today.AddDays(-afterDays - 1), ct), today, afterDays);
        if (reminder is not null)
        {
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.EInvoice, facilityId, reminder.Title, reminder.Body, "/billing/einvoices", "billing", reminder.Severity, ct);
        }

        return reminder;
    }

    /// <summary>
    /// The register's rows: the facility's B2B documents of the days given (the hospital's), each with its state and its
    /// latest IRN, those waiting for one first. What the register lists, and what the daily reminder reads.
    /// </summary>
    internal async Task<IReadOnlyList<EInvoiceRow>> RowsAsync(Guid facility, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var today = BillingTime.Today(time);
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
        return rows;
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

        var check = await CheckWithPortalAsync(document, seller, qr, request.AckNo, ct);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var (signedQrCode, generatedAt) = IrnVerification.Recorded(check.Value.Held, request.SignedQrCode, qr.IrnDt);
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
            AckDate = generatedAt.ToUniversalTime(),
            SignedQrCode = signedQrCode,
            SignedInvoice = check.Value.Held?.SignedInvoice is { Length: > 0 } signed ? signed : null,
            RecordedByName = currentUser.DisplayName ?? currentUser.UserName ?? "Staff",
            PrecedingSource = document.Preceding?.Source,
            PrecedingId = document.Preceding?.Id,
        };
        db.Add(registration);
        audit.Add(new AuditEntry(AuditCategories.Data, "billing.einvoice.irn_recorded",
            $"IRN {qr.Irn} of {noun} {document.Number} recorded by hand: {check.Value.How}.")
        {
            EntityType = nameof(EInvoice),
            EntityId = registration.Id.ToString(),
        });
        await db.SaveChangesAsync(ct);
        return await DetailAsync(document, [], ct);
    }

    /// <summary>
    /// An IRN recorded by hand, checked with the portal when the facility is connected to it (<see cref="IrnVerification"/>):
    /// it is recorded only when the portal holds it, active, for this document, or when the portal can no longer be asked
    /// (the IRN is older than the time the portal gives an IRN's details). The answer is the portal's IRN, when it was asked,
    /// and how the IRN was checked, for the audit trail; or why it is not recorded.
    /// </summary>
    private async Task<Result<(IrpIrn? Held, string How)>> CheckWithPortalAsync(
        EInvoiceDocument document, EInvoiceSeller seller, SignedQrCode.Contents qr, string ackNo, CancellationToken ct)
    {
        if (!(await portal.ConnectionAsync(document.FacilityId, ct)).Connected)
        {
            return ((IrpIrn?)null, "the facility is not connected to the e-invoice portal, so the portal was not asked");
        }

        if (!IrnVerification.AsksPortal(qr.IrnDt, time.GetUtcNow()))
        {
            return ((IrpIrn?)null, $"generated on {qr.IrnDt.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}, longer ago than the portal gives an IRN's details, so the portal was not asked");
        }

        var held = await portal.GetByIrnAsync(document.FacilityId, Gstin.Normalize(seller.Gstin)!, qr.Irn, ct);
        var noun = InvoiceRegistrationPayload.Noun(document);
        switch (held.Error)
        {
            // Switched on, the connection could not be used (its credentials are incomplete): the IRN is not taken unchecked.
            case { Code: IrpError.NotConnected } unusable:
                return Error.Rule("einvoice.irp_not_connected",
                    $"{unusable.Message} The IRN could not be checked with the portal, and nothing was recorded. Try again once the connection works; "
                    + "until then, an administrator can switch it off (Administration → Integrations) to record IRNs by hand without the check.");
            case { Code: IrpError.Unreachable } unanswered:
                return Error.Rule("einvoice.irp_unreachable",
                    $"{unanswered.Message} The IRN could not be checked with the portal, and nothing was recorded. Try again once the portal answers; "
                    + "while it does not, an administrator can switch the portal connection off (Administration → Integrations) to record IRNs by hand without the check.");
            case { } refused:
                return Error.Rule("einvoice.irp_unconfirmed",
                    $"The portal does not confirm IRN {qr.Irn} for the hospital's GSTIN ({refused.Code}: {refused.Message}). Nothing was recorded: check the IRN on the portal.");
        }

        var contradictions = IrnVerification.Contradictions(held.Value!, qr.Irn, ackNo, document, seller.Gstin);
        return contradictions.Count > 0
            ? Error.Rule("einvoice.irp_unconfirmed", $"The portal does not confirm IRN {qr.Irn} for this {noun}. {string.Join(" ", contradictions)} Nothing was recorded.")
            : (held.Value, "the e-invoice portal holds it, active, for this document");
    }

    /// <summary>
    /// Asks the portal for the document's IRN (the facility must be connected, <c>integrations.irp</c>): the JSON is the
    /// one the hospital would upload, checked as it is; the IRN, acknowledgement, signed invoice and signed QR code the
    /// portal answers are recorded after the code is checked against the document. A document the portal already holds
    /// (2150, as after a lost answer) is fetched by its IRN and recorded the same way, so asking twice never makes two.
    /// </summary>
    public async Task<Result<EInvoiceDetail>> GenerateAsync(string source, Guid id, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        return found.IsFailure
            ? found.Error!
            : await GenerateAsync(found.Value.Document, found.Value.Seller, found.Value.Applicable, currentUser.DisplayName ?? currentUser.UserName ?? "Staff", ct);
    }

    private async Task<Result<EInvoiceDetail>> GenerateAsync(
        EInvoiceDocument document, EInvoiceSeller seller, DateOnly applicable, string recordedBy, CancellationToken ct)
    {
        var noun = InvoiceRegistrationPayload.Noun(document);
        if (document.IsCancelled)
        {
            return Error.Rule("einvoice.document_cancelled", $"The {noun} is cancelled here; it needs no IRN.");
        }

        if (await LiveAsync(document.Source, document.Id, ct) is not null)
        {
            return Error.Conflict("einvoice.registered", $"This {noun} already has an IRN.");
        }

        var (payload, problems) = InvoiceRegistrationPayload.Build(seller, document);
        var preceding = await PrecedingProblemsAsync(document, applicable, ct);
        if (payload is null || preceding.Count > 0)
        {
            return Problems(document, [.. problems, .. preceding]);
        }

        var connection = await portal.ConnectionAsync(document.FacilityId, ct);
        if (!connection.Connected)
        {
            return Error.Rule("einvoice.irp_not_connected", $"{connection.Problem} Until it is, upload the JSON on the portal and record the IRN by hand.");
        }

        var gstin = Gstin.Normalize(seller.Gstin)!;
        var answer = await portal.GenerateAsync(document.FacilityId, gstin, payload, ct);
        if (answer.Error is { Code: IrpError.DuplicateIrn, ExistingIrn: { } existing })
        {
            answer = await portal.GetByIrnAsync(document.FacilityId, gstin, existing, ct);
            if (answer.Value is { IsCancelled: true })
            {
                return Error.Rule("einvoice.irp_cancelled",
                    $"The portal holds this {noun}'s number under IRN {existing}, cancelled there, and registers a number only once: nothing is recorded. "
                    + $"Cancel the {noun} here and issue it again under a new number.");
            }
        }

        if (!answer.IsSuccess)
        {
            return PortalError(answer.Error!);
        }

        var irn = answer.Value!;
        var read = SignedQrCode.Read(irn.SignedQrCode);
        if (read.IsFailure || SignedQrCode.Mismatches(read.Value, document, seller.Gstin) is { Count: > 0 } mismatches)
        {
            var why = read.IsFailure ? read.Error!.Message : string.Join(" ", SignedQrCode.Mismatches(read.Value, document, seller.Gstin));
            return Error.Rule("einvoice.irp_mismatch",
                $"The portal gave IRN {irn.Irn}, but its signed QR code is not this {noun}'s: {why} It is not recorded; check the IRN on the portal with the GST adviser.");
        }

        if (await db.Set<EInvoice>().IgnoreQueryFilters().AnyAsync(e => e.Irn == irn.Irn, ct))
        {
            return Error.Conflict("einvoice.irn_exists", "This IRN is already recorded against an invoice.");
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
            Irn = irn.Irn,
            AckNo = irn.AckNo,
            AckDate = irn.AckDate.ToUniversalTime(),
            SignedQrCode = irn.SignedQrCode,
            SignedInvoice = string.IsNullOrEmpty(irn.SignedInvoice) ? null : irn.SignedInvoice,
            Provider = connection.Provider ?? EInvoice.ManualProvider,
            RecordedByName = recordedBy,
            PrecedingSource = document.Preceding?.Source,
            PrecedingId = document.Preceding?.Id,
        };
        if (irn.EwbNo is { } ewb && irn.EwbDate is { } ewbDate)
        {
            registration.RecordEwayBill(ewb, ewbDate.ToUniversalTime(), irn.EwbValidTill?.ToUniversalTime());
        }

        db.Add(registration);
        await db.SaveChangesAsync(ct);
        return await DetailAsync(document, [], ct);
    }

    /// <summary>
    /// Gets a document's IRN on HC360's own, when the outbox delivers the request queued as the document became final
    /// (<see cref="EInvoiceQueue"/>), and says what the outbox does next (<see cref="EInvoiceAutomation"/>). Nothing more
    /// when the document needs no IRN now: it is no longer a B2B e-invoice of e-invoicing's days, it is cancelled, it has an
    /// IRN (live, or cancelled on the portal), or the facility is not connected to the portal (its IRN is recorded by hand).
    /// Tried again later when the portal did not answer, or the invoice a credit or debit note names still waits for its
    /// own IRN. Given up on, with holders of <see cref="BillingPermissions.EInvoice"/> told at once, when the document's
    /// problems or the portal stop it.
    /// </summary>
    internal async Task<(EInvoiceAutoOutcome Outcome, string Reason)> GenerateQueuedAsync(string source, Guid id, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        if (found.IsFailure)
        {
            return (EInvoiceAutoOutcome.Done, found.Error!.Message);
        }

        var (document, seller, applicable) = found.Value;
        if (document.IsCancelled || await db.Set<EInvoice>().AsNoTracking().AnyAsync(e => e.Source == document.Source && e.SourceId == document.Id, ct))
        {
            return (EInvoiceAutoOutcome.Done, "It is cancelled, or has an IRN already.");
        }

        if (await portal.ConnectionAsync(document.FacilityId, ct) is { Connected: false } connection)
        {
            return (EInvoiceAutoOutcome.Done, $"{connection.Problem} Its IRN is recorded by hand.");
        }

        if (await PrecedingAsync(document, applicable, ct) is { Waiting: true, Problem: { } waiting })
        {
            return (EInvoiceAutoOutcome.Retry, waiting);
        }

        var generated = await GenerateAsync(document, seller, applicable, EInvoiceAutomation.RecordedBy, ct);
        if (generated.IsSuccess || await LiveAsync(document.Source, document.Id, ct) is not null)
        {
            // Registered now, here or by a request that got there first.
            return (EInvoiceAutoOutcome.Done, "Registered.");
        }

        var error = generated.Error!;
        var outcome = EInvoiceAutomation.After(error.Code);
        if (outcome == EInvoiceAutoOutcome.GiveUp)
        {
            var noun = InvoiceRegistrationPayload.Noun(document);
            await notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.EInvoice, document.FacilityId, $"{char.ToUpperInvariant(noun[0])}{noun[1..]} {document.Number} did not get its IRN",
                $"HC360 asked the e-invoice portal for it on its own: {error.Message} It waits in the e-invoice register.",
                "/billing/einvoices", "billing", "warning", ct);
        }

        return (outcome, error.Message);
    }

    /// <summary>
    /// Generates the e-way bill of a B2B invoice of goods (the pharmacy's) from its live IRN, when its value is above
    /// <see cref="EwayBillThresholdSetting"/> (CGST rule 138): Part-B by the vehicle, or Part-A only with the transporter's
    /// id when the transporter will fill the vehicle in.
    /// </summary>
    public async Task<Result<EInvoiceDetail>> EwayBillAsync(string source, Guid id, GenerateEwayBillRequest request, CancellationToken ct)
    {
        var found = await DocumentAsync(source, id, ct);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var (document, seller, _) = found.Value;
        if (document.Type != EInvoiceDocumentTypes.Invoice || document.Items.All(i => i.IsService))
        {
            return Error.Rule("einvoice.ewaybill_goods", "An e-way bill is for an invoice of goods; services move without one.");
        }

        var threshold = await EwayBillThresholdAsync(document.FacilityId, ct);
        if (document.Total <= threshold)
        {
            return Error.Rule("einvoice.ewaybill_not_needed",
                $"Goods worth ₹{Money.FormatInr(document.Total)} move without an e-way bill (needed above ₹{Money.FormatInr(threshold)}).");
        }

        var registration = await db.Set<EInvoice>()
            .FirstOrDefaultAsync(e => e.Source == document.Source && e.SourceId == document.Id && e.Status == EInvoiceStatus.Registered, ct);
        if (registration is null)
        {
            return Error.Rule("einvoice.not_registered", "Get the invoice's IRN first: the e-way bill is generated from it.");
        }

        if (registration.EwbNo is { } existing)
        {
            return Error.Conflict("einvoice.ewaybill_exists", $"E-way bill {existing} is already generated for this invoice.");
        }

        var connection = await portal.ConnectionAsync(document.FacilityId, ct);
        if (!connection.Connected)
        {
            return Error.Rule("einvoice.irp_not_connected", connection.Problem ?? "The e-invoice portal (IRP) is not connected.");
        }

        var body = new JsonObject { ["Irn"] = registration.Irn, ["Distance"] = request.DistanceKm, ["TransMode"] = ((int)request.Mode).ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(request.TransporterGstin))
        {
            body["TransId"] = request.TransporterGstin.Trim().ToUpperInvariant();
        }

        if (!string.IsNullOrWhiteSpace(request.TransporterName))
        {
            body["TransName"] = request.TransporterName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.TransportDocumentNumber))
        {
            body["TransDocNo"] = request.TransportDocumentNumber.Trim();
        }

        if (request.TransportDocumentDate is { } docDate)
        {
            body["TransDocDt"] = InvoiceRegistrationPayload.Date(docDate);
        }

        if (!string.IsNullOrWhiteSpace(request.VehicleNumber))
        {
            body["VehNo"] = request.VehicleNumber.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
            body["VehType"] = request.OverDimensionalCargo ? "O" : "R";
        }

        var answer = await portal.GenerateEwayBillAsync(document.FacilityId, Gstin.Normalize(seller.Gstin)!, body, ct);
        if (!answer.IsSuccess)
        {
            return PortalError(answer.Error!);
        }

        var recorded = registration.RecordEwayBill(answer.Value!.EwbNo, answer.Value.EwbDate.ToUniversalTime(), answer.Value.ValidTill?.ToUniversalTime());
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

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
        var now = time.GetUtcNow();

        // Connected to the portal, HC360 cancels the IRN there first; one cancelled on the portal by hand is only recorded.
        var cancelled = !request.CancelledOnPortal && await portal.ConnectionAsync(document.FacilityId, ct) is { Connected: true }
            ? await CancelOnPortalAsync(document.FacilityId, Gstin.Normalize(seller.Gstin)!, registration, request, now, ct)
            : registration.Cancel(request.Reason, request.Remark, now);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        await db.SaveChangesAsync(ct);
        var (_, problems) = InvoiceRegistrationPayload.Build(seller, document);
        return await DetailAsync(document, [.. problems, .. await PrecedingProblemsAsync(document, applicable, ct)], ct);
    }

    /// <summary>
    /// Cancels the IRN on the portal and records it. A cancellation that fails is not taken at its word: the portal is asked
    /// how it holds the IRN (<see cref="IrnCancellations"/>), since one whose answer was lost, or one made before (the portal
    /// then refuses an IRN no longer active), is cancelled there all the same. Past the 24 hours here, the portal cancels
    /// nothing more, and is only asked whether it cancelled the IRN in time with its answer lost.
    /// </summary>
    private async Task<Result> CancelOnPortalAsync(
        Guid facilityId, string gstin, EInvoice registration, CancelIrnRequest request, DateTimeOffset now, CancellationToken ct)
    {
        // Past the 24 hours the portal would only refuse it (2270), so it is asked how it holds the IRN alone.
        var failure = new IrpError(IrpError.CancelWindowCrossed, "The portal cancels an IRN only within 24 hours of its generation.");
        if (registration.CanCancel(now))
        {
            var answer = await portal.CancelAsync(facilityId, gstin, registration.Irn, (int)request.Reason, request.Remark.Trim(), ct);
            if (answer.IsSuccess)
            {
                return registration.Cancel(request.Reason, request.Remark, now);
            }

            failure = answer.Error!;
        }

        var details = await portal.GetByIrnAsync(facilityId, gstin, registration.Irn, ct);
        return IrnCancellations.After(failure, details) switch
        {
            IrnCancelOutcome.Cancelled => registration.RecordPortalCancellation(request.Reason, request.Remark, details.Value!.CancelledAt, now),
            IrnCancelOutcome.WindowCrossed => EInvoice.CancelWindowCrossed(),
            IrnCancelOutcome.WindowCrossedUnconfirmed => CancelledInTimeUnknown(details, registration.Irn),
            IrnCancelOutcome.NotCancelled => Error.Rule("einvoice.irp_unreachable",
                $"{failure.Message} The portal still holds IRN {registration.Irn} as active, so nothing was cancelled. Try again."),
            IrnCancelOutcome.Unknown => CancelUnknown(failure.Code == IrpError.Unreachable ? failure : details.Error!, registration.Irn),
            _ => PortalError(failure),
        };
    }

    /// <summary>
    /// Past the 24 hours, the portal did not say how it holds the IRN (<paramref name="details"/>): it may have cancelled it in
    /// time, its answer lost, and asking again may not tell, so staff look on the portal before undoing the invoice.
    /// </summary>
    private static Error CancelledInTimeUnknown(IrpResult<IrpIrn> details, string irn)
    {
        var unsaid = details.Error is { } error ? $"{error.Code}: {error.Message}"
            : details.Value?.Status is { } status ? $"it holds it as {status}"
            : "its answer gave no status";
        return Error.Rule("einvoice.irp_cancel_unknown",
            $"The portal cancels an IRN only within 24 hours of its generation, and did not say whether it cancelled IRN {irn} in time ({unsaid}). "
            + "Check the IRN on the portal: while it is active there, issue a credit note against the invoice instead.");
    }

    /// <summary><paramref name="unanswered"/>: the call the portal did not answer, the cancellation or the question how it holds the IRN.</summary>
    private static Error CancelUnknown(IrpError unanswered, string irn) => Error.Rule("einvoice.irp_cancel_unknown",
        $"{unanswered.Message} Whether the portal cancelled IRN {irn} is not known: it did not say how it holds it. "
        + "Try again once the portal answers: HC360 then asks it first, and records the cancellation if it was made.");

    /// <summary>The live IRN of an invoice, if any.</summary>
    public Task<EInvoice?> LiveAsync(string source, Guid id, CancellationToken ct) =>
        db.Set<EInvoice>().AsNoTracking().FirstOrDefaultAsync(e => e.Source == source && e.SourceId == id && e.Status == EInvoiceStatus.Registered, ct);

    private async Task<decimal> EwayBillThresholdAsync(Guid facilityId, CancellationToken ct) =>
        await settings.GetAsync<decimal?>(EwayBillThresholdSetting, facilityId, ct) is { } value and > 0 ? value : DefaultEwayBillThreshold;

    /// <summary>The portal's refusal, in its own words and code, or why it could not be reached.</summary>
    private static Error PortalError(IrpError error) => error.Code switch
    {
        IrpError.NotConnected => Error.Rule("einvoice.irp_not_connected", error.Message),
        IrpError.Unreachable => Error.Rule("einvoice.irp_unreachable", $"{error.Message} Nothing was recorded; try again."),
        _ => Error.Rule("einvoice.irp_refused", $"The e-invoice portal refused it ({error.Code}): {error.Message}"),
    };

    public static EInvoiceStamp Stamp(EInvoice e) =>
        new(e.Irn, e.AckNo, e.AckDate, e.SignedQrCode, DocumentAssets.QrCode(e.SignedQrCode).DataUri);

    /// <summary>
    /// A credit note goes to the portal after the invoice it reduces. An invoice of e-invoicing's time needs its own IRN
    /// first, or the portal, the buyer's GSTR-2B and the hospital's GSTR-1 would hold a credit note against an invoice
    /// they never had. An invoice from before e-invoicing applied has no IRN, and its credit note goes without one.
    /// </summary>
    private async Task<IReadOnlyList<string>> PrecedingProblemsAsync(EInvoiceDocument document, DateOnly applicable, CancellationToken ct) =>
        (await PrecedingAsync(document, applicable, ct)).Problem is { } problem ? [problem] : [];

    /// <summary>
    /// What stops a credit or debit note from going to the portal before the invoice it names (<see cref="PrecedingProblemsAsync"/>):
    /// the invoice has no IRN yet. <c>Waiting</c>: the invoice can still get its IRN, after which the note goes.
    /// </summary>
    private async Task<(string? Problem, bool Waiting)> PrecedingAsync(EInvoiceDocument document, DateOnly applicable, CancellationToken ct)
    {
        if (document.Preceding is not { } preceding || preceding.Date < applicable || await LiveAsync(preceding.Source, preceding.Id, ct) is not null)
        {
            return (null, false);
        }

        var reportBy = preceding.Date.AddDays(ReportWithinDays);
        var by = reportBy.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return BillingTime.Today(time) <= reportBy
            ? ($"Invoice {preceding.Number} has no IRN yet: record its IRN first (the portal takes it until {by}); this credit note names it.", true)
            : ($"Invoice {preceding.Number} was due on the portal by {by} and has no IRN. Settle it with the hospital's GST adviser before reporting a credit note against it.", false);
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
                    registration.CanCancel(now), DocumentAssets.QrCode(registration.SignedQrCode).DataUri, registration.Version,
                    registration.Provider, registration.EwbNo, registration.EwbDate, registration.EwbValidTill),
            document.Type,
            document.Preceding?.Number,
            document.Preceding?.Date,
            (await portal.ConnectionAsync(document.FacilityId, ct)).Connected,
            document.Type == EInvoiceDocumentTypes.Invoice && document.Items.Any(i => !i.IsService)
                && document.Total > await EwayBillThresholdAsync(document.FacilityId, ct));
    }

    /// <summary>
    /// What the print of a document says of its e-invoice, by the register's own rule: its live IRN, whatever else holds;
    /// otherwise, for a document its source calls a B2B e-invoice (<paramref name="document"/> not null), dated on or after
    /// the day e-invoicing applies from (<paramref name="applicableFrom"/>), the register's state of it (<see cref="StateOf"/>)
    /// when that says it waits for an IRN or its IRN was cancelled; nothing for any other document, a cancelled one
    /// included.
    /// </summary>
    internal static EInvoicePrint PrintOf(EInvoiceDocument? document, DateOnly? applicableFrom, EInvoice? latest)
    {
        if (latest is { Status: EInvoiceStatus.Registered })
        {
            return new EInvoicePrint(Stamp(latest), null);
        }

        if (document is null || applicableFrom is not { } from || document.Date < from)
        {
            return EInvoicePrint.None;
        }

        return StateOf(document.IsCancelled, latest) switch
        {
            EInvoiceState.Pending => new EInvoicePrint(null, MissingIrn.Pending),
            EInvoiceState.IrnCancelled => new EInvoicePrint(null, MissingIrn.IrnCancelled),
            _ => EInvoicePrint.None,
        };
    }

    private Task<DateOnly?> ApplicableFromAsync(Guid facilityId, CancellationToken ct) => ApplicableFromAsync(settings, facilityId, ct);

    /// <summary>The first document date e-invoicing applies to at the facility (<see cref="ApplicableFromSetting"/>); null when it does not e-invoice.</summary>
    internal static async Task<DateOnly?> ApplicableFromAsync(ISettingsService settings, Guid facilityId, CancellationToken ct) =>
        DateOnly.TryParseExact(await settings.GetAsync<string?>(ApplicableFromSetting, facilityId, ct), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>The live registration of a document if it has one, otherwise its latest cancelled one.</summary>
    internal static EInvoice? Latest(IEnumerable<EInvoice> registrations) =>
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
        // The bill and its payer first, its lines only for a B2B tax invoice: the register is asked of every bill a screen
        // or print opens (IEInvoiceRegister.PrintAsync), and most are to patients, some with hundreds of lines.
        var bill = await db.Set<Bill>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (bill is not { Status: not BillStatus.Draft, PayerId: { } payerId, FinalizedAt: { } finalizedAt } || bill.TaxAmount == 0)
        {
            return null;
        }

        var payer = await db.Set<Payer>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == payerId, cancellationToken);
        if (payer?.Gstin is not { } gstin)
        {
            return null;
        }

        var lines = await db.Set<BillLine>().AsNoTracking().Where(l => l.BillId == bill.Id).OrderBy(l => l.SortOrder).ToListAsync(cancellationToken);
        var items = lines
            .Select(l => new EInvoiceItem(
                l.Description, IsService: true, l.SacCode, l.Quantity, l.UnitPrice, l.GrossAmount, l.GrossAmount - l.TaxableAmount, l.TaxableAmount,
                l.TaxRate, l.IgstAmount, l.CgstAmount, l.SgstAmount, l.TaxableAmount + l.IgstAmount + l.CgstAmount + l.SgstAmount))
            .ToList();
        return new EInvoiceDocument(
            Source, bill.Id, bill.FacilityId, bill.BillNumber ?? string.Empty, BillingTime.LocalDate(finalizedAt),
            new EInvoiceBuyer(gstin, payer.Name, payer.Address, payer.Phone, payer.Email), bill.PlaceOfSupply ?? Gstin.StateCode(gstin) ?? string.Empty,
            items, bill.RoundOffAmount, bill.NetAmount, bill.Status == BillStatus.Cancelled,
            lines.Any(l => l.Quantity < 0) ? "The bill has returned services; report it to the portal with a credit note for them." : null);
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
                where c.FacilityId == facilityId && (c.Kind == CreditNoteKind.CreditNote || c.Kind == CreditNoteKind.PayerShare) && !c.GstTimeBarred && b.TaxAmount != 0 && p.Gstin != null
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
        if (note is not { Kind: CreditNoteKind.CreditNote or CreditNoteKind.PayerShare, GstTimeBarred: false } || note.Lines.Count == 0)
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

/// <summary>
/// The register as other modules see it: an invoice's live IRN, to keep it from being cancelled; and what a document's
/// print says of its e-invoice, by the rule the register itself keeps (<see cref="EInvoiceService.PrintOf"/>).
/// </summary>
internal sealed class EInvoiceRegisterLookup(HospitalDbContext db, IEnumerable<IEInvoiceSource> sources, ISettingsService settings) : IEInvoiceRegister
{
    public async Task<EInvoiceStamp?> FindAsync(string source, Guid id, CancellationToken cancellationToken)
    {
        var live = await db.Set<EInvoice>().AsNoTracking()
            .FirstOrDefaultAsync(e => e.Source == source && e.SourceId == id && e.Status == EInvoiceStatus.Registered, cancellationToken);
        return live is null ? null : EInvoiceService.Stamp(live);
    }

    public async Task<EInvoicePrint> PrintAsync(string source, Guid id, CancellationToken cancellationToken)
    {
        var latest = EInvoiceService.Latest(await db.Set<EInvoice>().AsNoTracking()
            .Where(e => e.Source == source && e.SourceId == id)
            .ToListAsync(cancellationToken));
        if (latest is { Status: EInvoiceStatus.Registered })
        {
            return EInvoiceService.PrintOf(null, null, latest);
        }

        var document = sources.FirstOrDefault(s => s.Source == source) is { } from ? await from.FindAsync(id, cancellationToken) : null;
        var applicable = document is null ? null : await EInvoiceService.ApplicableFromAsync(settings, document.FacilityId, cancellationToken);
        return EInvoiceService.PrintOf(document, applicable, latest);
    }
}
