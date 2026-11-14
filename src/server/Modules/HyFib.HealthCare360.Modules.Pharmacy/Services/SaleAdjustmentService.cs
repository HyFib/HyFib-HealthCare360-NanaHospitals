using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>The cancellation asked for: the invoice, why, the day it was asked on, and how its money goes back.</summary>
public sealed record SaleCancelPayload(Guid SaleId, string Reason, DateOnly AskedOn, IReadOnlyList<RefundPart> PaidBack);

/// <summary>
/// The return asked for, exactly as it will be made: its lines (each restocked or written off), the refund, what is set
/// against the balance due and how the rest goes back.
/// </summary>
public sealed record SaleReturnPayload(Guid SaleId, IReadOnlyList<ReturnInput> Lines, string Reason, decimal Refund, decimal AgainstDue, IReadOnlyList<RefundPart> PaidBack);

/// <summary>A return made at once (<see cref="Created"/>: restocked and set against the balance due), or asked for (<see cref="Asked"/>).</summary>
public sealed record SaleReturnOutcome(SaleReturnCreated? Created, ApprovalRequestDto? Asked);

/// <summary>
/// After-sale changes to pharmacy invoices (PH-02): collecting credit balances, same-day cancellation (full restock, queue
/// re-opened) and sales returns (restock unexpired stock, refund against dues first). Money paid back goes back the way it
/// was paid (<see cref="SaleRefunds"/>), never cash for a card or UPI payment. A cancellation, and a return that pays money
/// out or writes stock off, are made only when a second person approves them (maker-checker): one pharmacist asks
/// (<see cref="PharmacyPermissions.Returns"/>), and someone who approves every other refund
/// (<see cref="PharmacyPermissions.RefundsApprove"/>) approves. Whoever sold the invoice, or took its payment, neither asks
/// nor approves. Controlled items coming back, and written off, are entered in the register.
/// </summary>
public sealed class SaleAdjustmentService(
    HospitalDbContext db,
    INumberSeries numbers,
    StockService stock,
    ControlledDrugRegisterService register,
    IEInvoiceRegister einvoices,
    IEInvoiceQueue einvoiceQueue,
    CashLimit cashLimit,
    ICounterTerminal terminal,
    IApprovals approvals,
    IOutbox outbox,
    IAuditLogger audit,
    ICurrentUser currentUser,
    TimeProvider time)
{
    /// <summary>A same-day invoice cancelled, its money paid back and its stock put back.</summary>
    public const string CancelKind = "pharmacy.sale_cancel";

    /// <summary>A return that pays money out, or writes stock off.</summary>
    public const string ReturnKind = "pharmacy.sale_return";

    /// <summary>Whoever sold the invoice, or took its payment, does not cancel it, ask for it or approve it.</summary>
    public const string CancelOwnSale = "pharmacy.cancel_own_sale";

    /// <summary>Whoever sold the invoice, or took its payment, does not take a return of it or approve one.</summary>
    public const string ReturnOwnSale = "pharmacy.return_own_sale";

    /// <summary>Collects a balance left on an invoice; cash is held to the Income-tax Act's limit (<see cref="CashLimit"/>).</summary>
    public async Task<Result> CollectAsync(Guid saleId, CollectPaymentRequest request, CancellationToken ct)
    {
        var sale = await db.Set<PharmacySale>().Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), saleId);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var cash = await cashLimit.CheckAsync(sale.FacilityId, sale.PatientId, request.Mode == PaymentMode.Cash ? request.Amount : 0m, "invoice",
            c => PharmacyCashReceipts.CashForSaleAsync(db, sale.Id, c), ct);
        if (cash.IsFailure)
        {
            return cash;
        }

        var collected = sale.CollectPayment(request.Mode, request.Amount, request.Reference, time.GetUtcNow(), currentUser.UserId, request.TerminalSaleId);
        if (collected.IsSuccess)
        {
            var terminalPaid = await PharmacyTerminalPayments.UseAsync(terminal, sale, [sale.Payments[^1]], ct);
            if (terminalPaid.IsFailure)
            {
                return terminalPaid;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return collected;
    }

    /// <summary>A cancellation or a return of the invoice waiting for approval, for every reader of the invoice.</summary>
    public Task<IReadOnlyList<PendingApproval>> PendingAsync(Guid saleId, CancellationToken ct) =>
        PendingApprovals.ForAsync(db, nameof(PharmacySale), saleId, [CancelKind, ReturnKind], currentUser.UserId, time.GetUtcNow(), ct);

    /// <summary>
    /// Asks for a same-day invoice to be cancelled. Nothing changes until someone else who approves refunds approves it;
    /// then every payment is paid back whole, the way it was paid, the stock goes back and the prescription returns to the
    /// queue (<see cref="CancelApprovedAsync"/>).
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> AskCancelAsync(Guid saleId, string reason, CancellationToken ct)
    {
        var sale = await LoadAsync(saleId, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), saleId);
        }

        if (sale.HandledBy(currentUser.UserId))
        {
            return Error.Forbidden(CancelOwnSale,
                $"You sold invoice {sale.InvoiceNumber} or took its payment, so you cannot ask for it to be cancelled. Another pharmacist asks, and someone who approves refunds decides.");
        }

        var cancellable = await CancellableAsync(sale, ct);
        if (cancellable.IsFailure)
        {
            return cancellable.Error!;
        }

        var today = time.Today();
        var paidBack = SaleRefunds.Plan(sale.Payments, sale.PaidAmount - sale.RefundedAmount);
        var party = await PartyAsync(sale, ct);
        List<ApprovalDetail> details =
        [
            new("Invoice", await InvoiceWordsAsync(sale, ct)),
            new("Patient", party),
            new("Items", Items(sale.Items.Select(i => (i.ItemName, i.BatchNumber, i.Quantity)))),
            new("Amount", DocumentFormat.Money(sale.NetAmount)),
            new("Paid back", SaleRefunds.Words(paidBack)),
        ];
        if (sale.DueAmount > 0)
        {
            details.Add(new("Balance due cancelled", DocumentFormat.Money(sale.DueAmount)));
        }

        details.Add(new("Stock", sale.QueueEntryId is null
            ? "Goes back on the shelf, into the batches it was sold from"
            : "Goes back on the shelf, into the batches it was sold from; the prescription returns to the queue"));
        details.Add(new("Approve by",
            $"The end of {DocumentFormat.Date(today)}: an invoice is cancelled only on the day it was sold. After that, take a return instead."));
        var summary = paidBack.Count == 0
            ? $"Cancel invoice {sale.InvoiceNumber} ({party}): nothing to pay back"
            : $"Cancel invoice {sale.InvoiceNumber} ({party}): ₹{Money.FormatInr(paidBack.Sum(p => p.Amount))} back ({SaleRefunds.ByMode(paidBack)})";
        return await approvals.RequestAsync(CancelKind, Subject(sale), new SaleCancelPayload(sale.Id, reason.Trim(), today, paidBack), Fit(summary, 300), details,
            reason, ct);
    }

    /// <summary>
    /// Cancels an approved invoice in the approval's transaction (the approval saves): only the invoice as it was asked
    /// for, still cancellable today, and never by an approver who sold it.
    /// </summary>
    internal async Task<Result<string>> CancelApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<SaleCancelPayload>(approval.Payload);
        var sale = await LoadAsync(asked.SaleId, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), asked.SaleId);
        }

        if (sale.HandledBy(currentUser.UserId))
        {
            return Error.Forbidden(CancelOwnSale,
                $"You sold invoice {sale.InvoiceNumber} or took its payment, so you cannot approve its cancellation. Someone else who approves refunds decides it.");
        }

        if (sale.Version != approval.SubjectVersion)
        {
            return Changed(sale);
        }

        var cancellable = await CancellableAsync(sale, ct);
        if (cancellable.IsFailure)
        {
            return Error.Rule("approval.stale", $"Invoice {sale.InvoiceNumber} can no longer be cancelled, so nothing was changed: {cancellable.Error!.Message}");
        }

        if (!SaleRefunds.Plan(sale.Payments, sale.PaidAmount - sale.RefundedAmount).SequenceEqual(asked.PaidBack))
        {
            return Changed(sale);
        }

        var now = time.GetUtcNow();
        var cancelled = sale.Cancel(asked.Reason, time.Today(), HospitalClock.ToLocalDate(sale.SoldAt), now, currentUser.UserId);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        var reference = new StockReference(PharmacyStockReferences.SaleCancel, sale.Id, sale.InvoiceNumber,
            Fit($"Invoice cancelled: {asked.Reason}; asked for by {approval.RequestedByName}, approved by {CheckerName()}", 300));
        var movements = new List<StockMovement>();
        foreach (var item in sale.Items)
        {
            var moved = await stock.ReturnToBatchAsync(item.BatchId, item.Quantity, StockMovementType.Return, reference, null, ct);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            movements.Add(moved.Value);
        }

        if (sale.QueueEntryId is { } entryId
            && await db.Set<PharmacyQueueEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, ct) is { } entry)
        {
            foreach (var item in sale.Items.Where(i => i.QueueLineId is not null))
            {
                entry.ReverseDispensed(item.QueueLineId!.Value, item.Quantity, now);
            }

            PharmacyRealtime.QueueChangedAfterCommit(outbox, entry);
        }

        await RecordRegisterAsync(sale, movements, StockMovementType.Return, inward: true, reference, now, ct);
        var outcome = $"Invoice {sale.InvoiceNumber} cancelled: {SaleRefunds.Words(cancelled.Value)}; the stock is back on the shelf";
        Audit(sale, "pharmacy.sale_cancelled", $"{outcome}. Asked for by {approval.RequestedByName}, approved by {CheckerName()}: {asked.Reason}", approval);
        return outcome;
    }

    /// <summary>
    /// A sales return. Restocked and set wholly against the balance due, it is made at once; one that pays money out, or
    /// writes stock off (expired, or not put back on the shelf), waits for someone else who approves refunds.
    /// </summary>
    public async Task<Result<SaleReturnOutcome>> ReturnAsync(Guid saleId, SaleReturnRequest request, CancellationToken ct)
    {
        var sale = await LoadAsync(saleId, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), saleId);
        }

        if (sale.Status == SaleStatus.Cancelled)
        {
            return Error.Rule("sale.cancelled", $"Invoice {sale.InvoiceNumber} is cancelled.");
        }

        if (sale.HandledBy(currentUser.UserId))
        {
            return Error.Forbidden(ReturnOwnSale,
                $"You sold invoice {sale.InvoiceNumber} or took its payment, so you cannot take a return of it. Another pharmacist takes it.");
        }

        var plan = sale.PlanReturn(request.Lines.Select(l => new ReturnInput(l.SaleItemId, l.Quantity, l.Restock)).ToList(), time.Today());
        if (plan.IsFailure)
        {
            return plan.Error!;
        }

        var reason = request.Reason.Trim();
        if (plan.Value.NeedsApproval)
        {
            var asked = await AskReturnAsync(sale, plan.Value, reason, ct);
            return asked.IsFailure ? asked.Error! : new SaleReturnOutcome(null, asked.Value);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var made = await MakeReturnAsync(sale, plan.Value, reason, null, ct);
        if (made.IsFailure)
        {
            return made.Error!;
        }

        var saved = await stock.SaveAsync(ct);
        if (saved.IsFailure)
        {
            return saved.Error!;
        }

        await transaction.CommitAsync(ct);
        return new SaleReturnOutcome(made.Value, null);
    }

    /// <summary>
    /// Makes an approved return in the approval's transaction (the approval saves): only over the invoice as it was asked
    /// for, and only if it would still come to exactly what was approved.
    /// </summary>
    internal async Task<Result<string>> ReturnApprovedAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<SaleReturnPayload>(approval.Payload);
        var sale = await LoadAsync(asked.SaleId, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PharmacySale), asked.SaleId);
        }

        if (sale.HandledBy(currentUser.UserId))
        {
            return Error.Forbidden(ReturnOwnSale,
                $"You sold invoice {sale.InvoiceNumber} or took its payment, so you cannot approve a return of it. Someone else who approves refunds decides it.");
        }

        if (sale.Version != approval.SubjectVersion)
        {
            return Changed(sale);
        }

        // Worked out again on the invoice as it is: a line that expired since is no longer restocked, and that is refused.
        var plan = sale.PlanReturn(asked.Lines, time.Today());
        if (plan.IsFailure)
        {
            return Error.Rule("approval.stale", $"The return on invoice {sale.InvoiceNumber} can no longer be made, so nothing was changed: {plan.Error!.Message}");
        }

        if (plan.Value.Refund != asked.Refund || plan.Value.AgainstDue != asked.AgainstDue || !plan.Value.PaidBack.SequenceEqual(asked.PaidBack)
            || !plan.Value.Lines.Select(l => new ReturnInput(l.Item.Id, l.Quantity, l.Restock)).SequenceEqual(asked.Lines))
        {
            return Changed(sale);
        }

        var made = await MakeReturnAsync(sale, plan.Value, asked.Reason, approval, ct);
        if (made.IsFailure)
        {
            return made.Error!;
        }

        var outcome = $"Return {made.Value.ReturnNumber} on invoice {sale.InvoiceNumber}: {ReturnWords(plan.Value)}";
        Audit(sale, "pharmacy.sale_returned", $"{outcome}. Asked for by {approval.RequestedByName}, approved by {CheckerName()}: {asked.Reason}", approval);
        return outcome;
    }

    private async Task<Result<ApprovalRequestDto>> AskReturnAsync(PharmacySale sale, SaleReturnPlan plan, string reason, CancellationToken ct)
    {
        var party = await PartyAsync(sale, ct);
        List<ApprovalDetail> details =
        [
            new("Invoice", await InvoiceWordsAsync(sale, ct)),
            new("Patient", party),
            new("Items returned", string.Join("; ", plan.Lines.Select(l =>
                $"{l.Item.ItemName} × {StockErrors.Format(l.Quantity)} (batch {l.Item.BatchNumber}), {(l.Restock ? "back on the shelf" : l.Expired ? "not restocked (expired): written off" : "not restocked: written off")}"))),
            new("Refund", DocumentFormat.Money(plan.Refund)),
        ];
        if (plan.AgainstDue > 0)
        {
            details.Add(new("Taken off the balance due", DocumentFormat.Money(plan.AgainstDue)));
        }

        details.Add(new("Paid back", SaleRefunds.Words(plan.PaidBack)));
        details.Add(new("Stock", plan.WritesOff
            ? "Not all of it goes back on the shelf: what is not restocked is written off (destroyed), and a controlled drug is entered in the register as returned and written off"
            : "Goes back on the shelf, into the batches it was sold from"));
        var summary = $"Return on invoice {sale.InvoiceNumber} ({party}): {ReturnWords(plan)}";
        var payload = new SaleReturnPayload(sale.Id, plan.Lines.Select(l => new ReturnInput(l.Item.Id, l.Quantity, l.Restock)).ToList(), reason, plan.Refund,
            plan.AgainstDue, plan.PaidBack);
        return await approvals.RequestAsync(ReturnKind, Subject(sale), payload, Fit(summary, 300), details, reason, ct);
    }

    /// <summary>
    /// Makes the return worked out in <paramref name="plan"/>, in the caller's transaction: restocked lines go back into
    /// their batch; the rest come back and go out again as a write-off, so the ledger and the register show both; the
    /// refund is set against the due, and the rest paid back the way it was paid.
    /// </summary>
    private async Task<Result<SaleReturnCreated>> MakeReturnAsync(PharmacySale sale, SaleReturnPlan plan, string reason, ApprovalRequest? approval, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var saleReturn = new PharmacySaleReturn
        {
            FacilityId = sale.FacilityId,
            ReturnNumber = await numbers.NextAsync(PharmacyNumberSeries.SaleReturn, sale.FacilityId, ct),
            SaleId = sale.Id,
            PatientId = sale.PatientId,
            ReturnedAt = now,
            Reason = reason,
        };
        var approved = approval is null ? string.Empty : $"; asked for by {approval.RequestedByName}, approved by {CheckerName()}";
        var reference = new StockReference(PharmacyStockReferences.SaleReturn, saleReturn.Id, saleReturn.ReturnNumber, Fit($"Return against {sale.InvoiceNumber}{approved}", 300));
        var writeOff = reference with { Remarks = Fit($"Returned against {sale.InvoiceNumber}, not restocked: {reason}{approved}", 300) };
        var restocked = new List<StockMovement>();
        var cameBack = new List<StockMovement>();
        var writtenOff = new List<(StockMovement Movement, StockMovementType Type)>();
        foreach (var line in plan.Lines)
        {
            var returned = line.Item.Return(line.Quantity);
            if (returned.IsFailure)
            {
                return returned.Error!;
            }

            var moved = await stock.ReturnToBatchAsync(line.Item.BatchId, line.Quantity, StockMovementType.Return, line.Restock ? reference : writeOff, null, ct);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            if (line.Restock)
            {
                restocked.Add(moved.Value);
            }
            else
            {
                cameBack.Add(moved.Value);
                var type = line.Expired ? StockMovementType.ExpiryWriteOff : StockMovementType.Adjustment;
                var destroyed = await stock.AdjustBatchAsync(line.Item.BatchId, -line.Quantity, type, writeOff, ct);
                if (destroyed.IsFailure)
                {
                    return destroyed.Error!;
                }

                writtenOff.Add((destroyed.Value, type));
            }

            saleReturn.Items.Add(new PharmacySaleReturnItem
            {
                SaleReturnId = saleReturn.Id,
                SaleItemId = line.Item.Id,
                ItemId = line.Item.ItemId,
                ItemName = line.Item.ItemName,
                BatchId = line.Item.BatchId,
                BatchNumber = line.Item.BatchNumber,
                Quantity = line.Quantity,
                RefundAmount = line.Refund,
                Restocked = line.Restock,
            });
        }

        var applied = sale.ApplyReturn(plan.Refund, now, currentUser.UserId);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var main = applied.Value.PaidBack.OrderByDescending(p => p.Amount).FirstOrDefault();
        saleReturn.RefundAmount = plan.Refund;
        saleReturn.AdjustedAgainstDue = applied.Value.AdjustedAgainstDue;
        saleReturn.CashRefund = applied.Value.PaidBack.Sum(p => p.Amount);
        saleReturn.RefundMode = main?.Mode ?? PaymentMode.Cash;
        saleReturn.RefundReference = main?.Reference;
        db.Add(saleReturn);
        var returnedOn = HospitalClock.ToLocalDate(now);
        if (sale.BuyerGstin is not null && returnedOn <= GstCreditNotes.LastDay(HospitalClock.ToLocalDate(sale.SoldAt)))
        {
            // The return of a B2B sale is a credit note, an e-invoice too (GST rule 48(4)): its IRN is asked for once it is made.
            await einvoiceQueue.QueueAsync(PharmacyReturnEInvoiceSource.Key, saleReturn.Id, saleReturn.FacilityId, returnedOn, ct);
        }

        await RecordRegisterAsync(sale, restocked, StockMovementType.Return, inward: true, reference, now, ct);
        await RecordRegisterAsync(sale, cameBack, StockMovementType.Return, inward: true, writeOff, now, ct);
        foreach (var group in writtenOff.GroupBy(w => w.Type))
        {
            await RecordRegisterAsync(sale, group.Select(w => w.Movement).ToList(), group.Key, inward: false, writeOff, now, ct);
        }

        return new SaleReturnCreated(saleReturn.Id, saleReturn.ReturnNumber, plan.Refund, saleReturn.AdjustedAgainstDue, saleReturn.CashRefund);
    }

    /// <summary>An invoice with an IRN is cancelled on the portal, not here; otherwise only today's, with nothing returned.</summary>
    private async Task<Result> CancellableAsync(PharmacySale sale, CancellationToken ct)
    {
        if (sale.BuyerGstin is not null && await einvoices.FindAsync(PharmacyEInvoiceSource.Key, sale.Id, ct) is not null)
        {
            return Error.Rule("einvoice.active",
                "This invoice has an IRN. Cancel the IRN on the portal within 24 hours and record it in the e-invoice register, or take a return.");
        }

        return sale.CanCancel(time.Today(), HospitalClock.ToLocalDate(sale.SoldAt));
    }

    private async Task RecordRegisterAsync(
        PharmacySale sale, List<StockMovement> movements, StockMovementType type, bool inward, StockReference reference, DateTimeOffset now, CancellationToken ct)
    {
        if (!movements.Any(m => m.RequiresRegister))
        {
            return;
        }

        var items = await InventoryQueries.ItemSummariesAsync(db, movements.Select(m => m.ItemId), ct);
        var patient = sale.PatientId is { } patientId ? await PharmacyLookups.PatientAsync(db, patientId, ct) : null;
        register.Record(sale.FacilityId, movements, type, inward, reference,
            PharmacyLookups.Party(patient, sale.CustomerName, sale.PrescriberName, sale.PrescriberRegistrationNumber, sale.PrescriptionNumber), items, now);
    }

    private void Audit(PharmacySale sale, string action, string summary, ApprovalRequest approval) =>
        audit.Add(new AuditEntry(AuditCategories.Data, action, Fit(summary, 1000))
        {
            PatientId = sale.PatientId,
            EntityType = nameof(PharmacySale),
            EntityId = sale.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { sale.InvoiceNumber, approval = approval.Id, requestedBy = approval.RequestedBy, approvedBy = currentUser.UserId },
        });

    private static ApprovalSubject Subject(PharmacySale sale) => new(nameof(PharmacySale), sale.Id, sale.Version, sale.FacilityId, sale.PatientId);

    private static Error Changed(PharmacySale sale) => Error.Rule("approval.stale",
        $"Invoice {sale.InvoiceNumber} changed after this was asked for (a payment, a return or a cancellation), so nothing was changed. Ask again, against the invoice as it is now.");

    /// <summary>What a return comes to, in short: "₹100.00 back (₹60.00 card, ₹40.00 cash), 1 line not restocked (written off)".</summary>
    private static string ReturnWords(SaleReturnPlan plan)
    {
        var money = plan.PaidBackAmount > 0
            ? $"₹{Money.FormatInr(plan.PaidBackAmount)} back ({SaleRefunds.ByMode(plan.PaidBack)})"
            : $"₹{Money.FormatInr(plan.AgainstDue)} off the balance due, nothing paid back";
        var writtenOff = plan.Lines.Count(l => !l.Restock);
        return writtenOff == 0 ? money : $"{money}, {writtenOff} line{(writtenOff == 1 ? string.Empty : "s")} not restocked (written off)";
    }

    private static string Items(IEnumerable<(string Name, string Batch, decimal Quantity)> items) =>
        string.Join("; ", items.Select(i => $"{i.Name} × {StockErrors.Format(i.Quantity)} (batch {i.Batch})"));

    private async Task<string> PartyAsync(PharmacySale sale, CancellationToken ct)
    {
        var patient = sale.PatientId is { } patientId ? await PharmacyLookups.PatientAsync(db, patientId, ct) : null;
        return patient is not null ? $"{patient.Name} ({patient.Uhid})" : sale.CustomerName ?? "Walk-in customer";
    }

    private async Task<string> InvoiceWordsAsync(PharmacySale sale, CancellationToken ct)
    {
        var soldBy = await db.Set<User>().Where(u => u.Id == sale.SoldByUserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct);
        return $"{sale.InvoiceNumber}, sold {DocumentFormat.DateTime(sale.SoldAt)}{(soldBy is null ? string.Empty : $" by {soldBy}")}";
    }

    private string CheckerName() => currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private Task<PharmacySale?> LoadAsync(Guid saleId, CancellationToken ct) =>
        db.Set<PharmacySale>().Include(s => s.Items).Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == saleId, ct);
}

/// <summary>
/// A same-day pharmacy invoice cancelled: one pharmacist asks, and someone who approves every other refund (accountants
/// and hospital administrators by default) approves, never whoever sold it. Its money goes back the way it was paid.
/// </summary>
internal sealed class SaleCancelApproval(SaleAdjustmentService sales) : IApprovalAction
{
    public string Kind => SaleAdjustmentService.CancelKind;

    public string Title => "Pharmacy invoice cancellation";

    public string ApprovePermission => PharmacyPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => sales.CancelApprovedAsync(request, ct);
}

/// <summary>A pharmacy return that pays money out or writes stock off: asked for and approved as a cancellation is.</summary>
internal sealed class SaleReturnApproval(SaleAdjustmentService sales) : IApprovalAction
{
    public string Kind => SaleAdjustmentService.ReturnKind;

    public string Title => "Pharmacy return";

    public string ApprovePermission => PharmacyPermissions.RefundsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => sales.ReturnApprovedAsync(request, ct);
}
