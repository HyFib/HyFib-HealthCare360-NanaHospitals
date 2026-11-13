using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Realtime;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>What an electronic payment is for: a bill's balance or an IP advance.</summary>
internal sealed record PaymentTarget(Guid FacilityId, Guid PatientId, ReceiptPurpose Purpose, Guid? BillId, Guid? IpAccountId, decimal Due, string Description);

internal static class PaymentTargets
{
    public static async Task<Result<PaymentTarget>> ResolveAsync(HospitalDbContext db, Guid? billId, Guid? ipAccountId, decimal? amount, CancellationToken ct)
    {
        if (billId is { } id)
        {
            var bill = await db.Set<Bill>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
            if (bill is null)
            {
                return Error.NotFound(nameof(Bill), id);
            }

            if (!bill.IsOpenForPayment)
            {
                return Error.Rule("billing.bill_not_payable", "This bill has no balance to pay.");
            }

            if (amount is { } requested && requested > bill.DueAmount)
            {
                return Error.Rule("billing.overpayment", $"The balance on this bill is ₹{Money.FormatInr(bill.DueAmount)}.");
            }

            return new PaymentTarget(bill.FacilityId, bill.PatientId, ReceiptPurpose.BillPayment, bill.Id, null, bill.DueAmount, $"Bill {bill.BillNumber}");
        }

        if (ipAccountId is { } accountId)
        {
            var account = await db.Set<IpAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is null)
            {
                return Error.NotFound(nameof(IpAccount), accountId);
            }

            if (!account.IsActive)
            {
                return Error.Rule("billing.ip_account_inactive", "Advances can be collected only while the patient is admitted.");
            }

            // No advance is asked of a victim treated free of cost by law (BNSS 2023, s.397), by terminal or by link.
            if (await BillFreeTreatment.ReasonAsync(db, account.VisitId, ct) is { } free)
            {
                return Error.Rule("billing.free_treatment", $"This stay is free of cost by law ({free}), so no advance is taken.");
            }

            if (amount is not > 0)
            {
                return Error.Field("amount", "Enter the advance amount.");
            }

            return new PaymentTarget(account.FacilityId, account.PatientId, ReceiptPurpose.Deposit, null, account.Id, amount.Value, $"Advance for admission {account.AdmissionNumber}");
        }

        return Error.Field("billId", "Choose a bill or an IP account.");
    }
}

/// <summary>
/// Card/UPI terminal sales (INT-POS): push the amount to the terminal, poll its status, and record the receipt once when
/// the terminal approves — keyed on the terminal transaction id, so concurrent polls cannot record it twice. When the
/// bill or admission refuses an approved sale's receipt, the money taken is kept on the sale and in the reconciliation
/// exceptions, and the accountants are alerted, as for an online payment. The counters that bill their own sales (the
/// pharmacy, the canteen) take their payments through the same lifecycle (<see cref="CounterTerminalService"/>): their
/// approved sale is recorded by the counter's own payment instead (<see cref="UseAsync"/>), and one that no payment
/// records is kept in the exceptions the same way.
/// </summary>
public sealed class PosService(
    HospitalDbContext db,
    ReceiptService receipts,
    ShiftService shifts,
    GatewayDiscrepancyService discrepancies,
    INotificationService notifications,
    IServiceProvider services,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<PosService> logger)
{
    /// <summary>The start of the message an approved sale carries while its receipt could not be recorded.</summary>
    internal const string NotRecordedPrefix = "Paid on the terminal, but no receipt could be recorded";

    /// <summary>The start of the message a counter's approved sale carries while no payment of the counter records it.</summary>
    internal const string NotUsedPrefix = "Paid on the terminal, but no invoice records it yet";

    private static readonly Error NotConfigured =
        Error.Rule("billing.pos_not_configured", "No card/UPI terminal integration is configured. Record the payment with the slip details instead.");

    public async Task<Result<PosSaleDto>> StartAsync(StartPosSaleRequest request, CancellationToken ct)
    {
        var terminal = services.GetService<IPosTerminal>();
        if (terminal is null)
        {
            return NotConfigured;
        }

        var target = await PaymentTargets.ResolveAsync(db, request.BillId, request.IpAccountId, request.Amount, ct);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var shift = await shifts.RequireOpenShiftAsync(ct);
        if (shift.IsFailure)
        {
            return shift.Error!;
        }

        var sale = new PosTransaction
        {
            FacilityId = target.Value.FacilityId,
            Purpose = target.Value.Purpose,
            BillId = target.Value.BillId,
            IpAccountId = target.Value.IpAccountId,
            PatientId = target.Value.PatientId,
            ShiftId = shift.Value?.Id,
            Counter = shift.Value?.Counter,
        };
        var pushed = await PushAsync(terminal, sale, request.TerminalId, request.Amount, request.Mode, ct);
        if (pushed.IsFailure)
        {
            return pushed.Error!;
        }

        if (sale.Status == PosTransactionStatus.Approved)
        {
            await TryRecordAsync(sale, ct);
        }

        return ToDto(sale);
    }

    /// <summary>
    /// Pushes a counter's card or UPI amount to the terminal (<see cref="CounterTerminalService"/>): the counter that bills
    /// its own sales takes no Billing shift and gets no receipt; it records the approved sale itself (<see cref="UseAsync"/>).
    /// </summary>
    internal async Task<Result<PosTransaction>> StartForCounterAsync(
        string owner, decimal amount, string terminalId, PaymentMode mode, Guid? patientId, CancellationToken ct)
    {
        var terminal = services.GetService<IPosTerminal>();
        if (terminal is null)
        {
            return NotConfigured;
        }

        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var sale = new PosTransaction { FacilityId = facilityId, Owner = owner, PatientId = patientId };
        var pushed = await PushAsync(terminal, sale, terminalId, amount, mode, ct);
        return pushed.IsFailure ? pushed.Error! : sale;
    }

    /// <summary>Asks the terminal to take the amount and keeps the sale, whatever the terminal answered.</summary>
    private async Task<Result> PushAsync(IPosTerminal terminal, PosTransaction sale, string terminalId, decimal amount, PaymentMode mode, CancellationToken ct)
    {
        var reference = Guid.CreateVersion7().ToString("N");
        PosTransactionResult result;
        try
        {
            result = await terminal.StartSaleAsync(new PosSaleRequest(terminalId.Trim(), Money.Round(amount), reference, mode.ToString()), sale.FacilityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not start a sale on terminal {TerminalId}", terminalId);
            return Error.Rule("billing.pos_unavailable", "The terminal could not be reached. Check it is online and try again.");
        }

        sale.Provider = terminal.Provider;
        sale.TerminalId = terminalId.Trim();
        sale.TransactionId = result.TransactionId;
        sale.Reference = reference;
        sale.Amount = Money.Round(amount);
        sale.Mode = mode;
        sale.Status = PosTransactionStatus.Initiated;
        sale.InitiatedBy = currentUser.UserId;
        sale.Apply(result, time.GetUtcNow());
        db.Add(sale);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Polls the terminal (the counter calls this every few seconds while the customer pays).</summary>
    public async Task<Result<PosSaleDto>> RefreshAsync(Guid id, CancellationToken ct)
    {
        var sale = await RefreshSaleAsync(id, null, ct);
        return sale.IsFailure ? sale.Error! : ToDto(sale.Value);
    }

    public async Task<Result<PosSaleDto>> CancelAsync(Guid id, CancellationToken ct)
    {
        var sale = await CancelSaleAsync(id, null, ct);
        return sale.IsFailure ? sale.Error! : ToDto(sale.Value);
    }

    /// <summary>
    /// The sale of <paramref name="owner"/> (null: Billing's own) as the terminal has it now; an approval of Billing's own
    /// is recorded as its receipt once.
    /// </summary>
    internal async Task<Result<PosTransaction>> RefreshSaleAsync(Guid id, string? owner, CancellationToken ct)
    {
        var sale = await db.Set<PosTransaction>().FirstOrDefaultAsync(s => s.Id == id && s.Owner == owner, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PosTransaction), id);
        }

        if (sale.IsComplete && (sale.Status != PosTransactionStatus.Approved || sale.Owner is not null || sale.ReceiptId is not null))
        {
            return sale;
        }

        if (!sale.IsComplete)
        {
            var terminal = services.GetService<IPosTerminal>();
            if (terminal is null)
            {
                return Error.Rule("billing.pos_not_configured", "No card/UPI terminal integration is configured.");
            }

            sale.Apply(await terminal.GetStatusAsync(sale.TransactionId, sale.FacilityId, ct), time.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }

        if (sale.Owner is null && sale.Status == PosTransactionStatus.Approved && sale.ReceiptId is null && !await TryRecordAsync(sale, ct))
        {
            return await db.Set<PosTransaction>().AsNoTracking().FirstAsync(s => s.Id == id, ct);
        }

        return sale;
    }

    /// <summary>Cancels the sale of <paramref name="owner"/> (null: Billing's own) on the terminal.</summary>
    internal async Task<Result<PosTransaction>> CancelSaleAsync(Guid id, string? owner, CancellationToken ct)
    {
        var sale = await db.Set<PosTransaction>().FirstOrDefaultAsync(s => s.Id == id && s.Owner == owner, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PosTransaction), id);
        }

        if (sale.IsComplete)
        {
            return Error.Rule("billing.pos_complete", "This terminal sale is already complete.");
        }

        var terminal = services.GetService<IPosTerminal>();
        if (terminal is null)
        {
            return Error.Rule("billing.pos_not_configured", "No card/UPI terminal integration is configured.");
        }

        sale.Apply(await terminal.CancelAsync(sale.TransactionId, sale.FacilityId, ct), time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        if (sale.Owner is null && sale.Status == PosTransactionStatus.Approved && sale.ReceiptId is null)
        {
            // The customer completed the payment before the cancel reached the terminal: the money is ours to record.
            await TryRecordAsync(sale, ct);
        }

        return sale;
    }

    /// <summary>
    /// A counter's approved sales that no payment of the counter records yet, newest first, within the re-check's window:
    /// money taken on the terminal for a sale the counter did not finish (the screen was closed, the invoice refused).
    /// </summary>
    internal async Task<IReadOnlyList<PosTransaction>> UnusedAsync(string owner, CancellationToken ct)
    {
        var since = time.GetUtcNow() - PosRecheckRunner.Window;
        return await db.Set<PosTransaction>().AsNoTracking()
            .Where(s => s.Owner == owner && s.Status == PosTransactionStatus.Approved && s.OwnerPaymentId == null && s.CreatedAt > since)
            .OrderByDescending(s => s.CreatedAt)
            .Take(20)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Marks a counter's approved sale as recorded by the counter's payment <paramref name="paymentId"/>, on its invoice or
    /// bill <paramref name="document"/>, and clears its exception. Staged in the caller's unit of work: the counter saves
    /// it with its invoice, and the sale's version refuses a second invoice saved at the same time. The same payment may
    /// use its sale again (a retried save).
    /// </summary>
    internal async Task<Result<PosTransaction>> UseAsync(
        string owner, Guid id, Guid paymentId, string document, PaymentMode mode, decimal amount, CancellationToken ct)
    {
        var sale = await db.Set<PosTransaction>().FirstOrDefaultAsync(s => s.Id == id && s.Owner == owner, ct);
        if (sale is null)
        {
            return Error.Rule("pos.sale_unknown", "This terminal payment was not taken at this counter.");
        }

        if (sale.Status != PosTransactionStatus.Approved)
        {
            return sale.IsComplete
                ? Error.Rule("pos.sale_not_paid", $"The terminal did not take this payment ({StatusWords(sale.Status)}). Take the payment again.")
                : Error.Rule("pos.sale_pending", "The customer has not paid on the terminal yet. Wait for the terminal to approve the payment.");
        }

        if (sale.OwnerPaymentId is { } used)
        {
            return used == paymentId
                ? sale
                : Error.Conflict("pos.sale_used", $"This terminal payment is already recorded on {sale.OwnerDocument}. Take a new payment for this one.");
        }

        if (sale.Mode != mode)
        {
            return Error.Rule("pos.sale_mode", $"The terminal took this payment by {ModeWords(sale.Mode)}: record it as a {ModeWords(sale.Mode)} payment.");
        }

        if (sale.Amount != Money.Round(amount))
        {
            return Error.Rule("pos.sale_amount",
                $"The terminal took ₹{Money.FormatInr(sale.Amount)}, but this payment is ₹{Money.FormatInr(amount)}. Record ₹{Money.FormatInr(sale.Amount)} on the terminal payment and take the rest another way.");
        }

        sale.OwnerPaymentId = paymentId;
        sale.OwnerDocument = document.Length > 64 ? document[..64] : document;
        sale.Message = sale.Message?.StartsWith(NotUsedPrefix, StringComparison.Ordinal) == true ? null : sale.Message;
        await discrepancies.ClearAsync(DiscrepancyKind.UnrecordedPayment, sale.TransactionId, $"Recorded on {sale.OwnerDocument}.", ct);
        return sale;
    }

    /// <summary>
    /// The background re-check of a sale the counter left open (<see cref="PosRecheckRunner"/>): the terminal is asked
    /// with the sale's facility's settings, an approval is recorded once, and a sale still waiting for the customer after
    /// <paramref name="timeout"/> is cancelled on the terminal and marked timed out. A counter's approved sale that no
    /// payment of the counter records within <paramref name="timeout"/> of its approval is kept in the exceptions. An answer from a provider other than
    /// the one that started the sale (POS switched off, or another provider chosen since) is not applied: it would decline
    /// a sale the customer may yet pay. A cancel the terminal does not confirm leaves the sale open, to be tried again.
    /// </summary>
    internal async Task RecheckAsync(PosTransaction sale, TimeSpan timeout, CancellationToken ct)
    {
        if (!sale.IsComplete && services.GetService<IPosTerminal>() is { } terminal)
        {
            var status = await terminal.GetStatusAsync(sale.TransactionId, sale.FacilityId, ct);
            if (terminal.Provider != sale.Provider)
            {
                logger.LogWarning("Terminal sale {TransactionId} was not re-checked: it was started with {Started}, and POS now answers with {Now}",
                    sale.TransactionId, sale.Provider, terminal.Provider);
                return;
            }

            var now = time.GetUtcNow();
            sale.Apply(status, now);
            if (!sale.IsComplete && now - sale.CreatedAt >= timeout)
            {
                var cancelled = await terminal.CancelAsync(sale.TransactionId, sale.FacilityId, ct);
                if (terminal.Provider == sale.Provider)
                {
                    sale.Apply(cancelled, now);
                    if (sale.Status == PosTransactionStatus.Cancelled)
                    {
                        // Told apart from a cashier's cancel: nobody at the counter decided this.
                        sale.Status = PosTransactionStatus.TimedOut;
                        sale.Message = string.Create(CultureInfo.InvariantCulture,
                            $"Nobody completed the payment within {timeout.TotalMinutes:0} minutes; HC360 cancelled the sale on the terminal.");
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            if (sale.Owner is null && sale.IsComplete && sale.Status != PosTransactionStatus.Approved)
            {
                await realtime.ToFacilityAsync(sale.FacilityId, "billing.pos.changed", new { sale.Id, sale.Status, sale.BillId }, ct);
            }
        }

        if (sale.Status != PosTransactionStatus.Approved)
        {
            return;
        }

        if (sale.Owner is null)
        {
            if (sale.ReceiptId is null)
            {
                await TryRecordAsync(sale, ct);
            }
        }
        else if (sale.OwnerPaymentId is null && time.GetUtcNow() - (sale.CompletedAt ?? sale.CreatedAt) >= timeout)
        {
            await KeepUnusedAsync(sale, timeout, ct);
        }
    }

    internal static PosSaleDto ToDto(PosTransaction s) => new(
        s.Id, s.Provider, s.TerminalId, s.TransactionId, s.Amount, s.Mode, s.Status, s.ApprovalCode, s.CardLast4, s.Rrn, s.Message, s.ReceiptId,
        s.CreatedAt, s.CompletedAt);

    /// <summary>
    /// Records the approved sale's receipt; returns false when a concurrent poll recorded it first. When the bill or
    /// admission refuses it, the attempt is rolled back (so its receipt number goes back to the series) and the failure is
    /// kept: on the sale, for the counter, and as an exception for the accountants, who are alerted the first time. The
    /// next re-check tries again, and a receipt recorded later clears the exception.
    /// </summary>
    private async Task<bool> TryRecordAsync(PosTransaction sale, CancellationToken ct)
    {
        if (sale.Purpose is not { } purpose || sale.PatientId is not { } patientId)
        {
            // Only a counter's own sale has neither, and the counter records it as its payment (UseAsync).
            return false;
        }

        string? refused = null;
        await using (var transaction = await db.BeginOwnedTransactionAsync(ct))
        {
            try
            {
                var shift = sale.ShiftId is { } shiftId ? await db.Set<CashierShift>().FirstOrDefaultAsync(s => s.Id == shiftId, ct) : null;
                var receipt = await receipts.RecordExternalAsync(new ExternalPayment(
                    sale.FacilityId, purpose, patientId, sale.BillId, sale.IpAccountId, sale.Mode, sale.Amount, sale.TransactionId, sale.Rrn,
                    sale.CardLast4, ReceiptSource.PosTerminal, shift), ct);
                if (receipt.Error is { } error)
                {
                    // Not committed: the transaction rolls back when it is disposed.
                    refused = error.Message;
                }
                else
                {
                    sale.ReceiptId = receipt.Value.Id;
                    sale.Message = sale.Message?.StartsWith(NotRecordedPrefix, StringComparison.Ordinal) == true ? null : sale.Message;
                    await discrepancies.ClearAsync(DiscrepancyKind.UnrecordedPayment, sale.TransactionId, $"Receipt {receipt.Value.ReceiptNumber} recorded.", ct);
                    await db.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                }
            }
            catch (DbUpdateException ex)
            {
                logger.LogInformation(ex, "Terminal sale {TransactionId} was recorded by a concurrent request", sale.TransactionId);
                db.ChangeTracker.Clear();
                return false;
            }
        }

        if (refused is not null)
        {
            await KeepRecordFailureAsync(sale, refused, ct);
            return true;
        }

        await AfterSaveAsync("tell the counter", sale,
            () => realtime.ToFacilityAsync(sale.FacilityId, "billing.pos.changed", new { sale.Id, sale.Status, sale.BillId }, ct));
        return true;
    }

    /// <summary>The terminal took money that HC360 could not record yet: kept on the sale and in the exceptions.</summary>
    private async Task KeepRecordFailureAsync(PosTransaction sale, string reason, CancellationToken ct)
    {
        logger.LogError("Approved terminal sale {TransactionId} could not be recorded: {Error}", sale.TransactionId, reason);
        var message = $"{NotRecordedPrefix}: {reason} The accountants have been told; do not take the money again.";
        sale.Message = message.Length > 300 ? message[..300] : message;
        var reference = sale.Rrn is { } rrn ? $", RRN {rrn}" : sale.ApprovalCode is { } approval ? $", approval {approval}" : string.Empty;
        var exception = await discrepancies.RaiseAsync(
            DiscrepancyKind.UnrecordedPayment, sale.TransactionId, sale.FacilityId,
            $"₹{Money.FormatInr(sale.Amount)} was paid on card/UPI terminal {sale.TerminalId}{reference}, but the receipt could not be recorded: {reason}",
            d =>
            {
                d.BillId = sale.BillId;
                d.IpAccountId = sale.IpAccountId;
                d.GatewayAmount = sale.Amount;
                d.GatewayAt = sale.CompletedAt;
            },
            ct);
        var isNew = db.Entry(exception).State == EntityState.Added;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent check kept the same failure first; it alerts the accountants.
            logger.LogInformation(ex, "The failure to record terminal sale {TransactionId} was kept by a concurrent request", sale.TransactionId);
            db.ChangeTracker.Clear();
            return;
        }

        await AfterSaveAsync("tell the counter", sale,
            () => realtime.ToFacilityAsync(sale.FacilityId, "billing.pos.changed", new { sale.Id, sale.Status, sale.BillId }, ct));
        if (isNew)
        {
            await AfterSaveAsync("alert the accountants", sale, () => notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile, sale.FacilityId, "Card/UPI payment not recorded",
                $"₹{Money.FormatInr(sale.Amount)} was paid on terminal {sale.TerminalId}, but no receipt could be recorded. Open Gateway reconciliation to see why, then record it or refund it on the terminal.",
                "/billing/reconciliation", "billing", "warning", ct));
        }
    }

    /// <summary>
    /// A counter's approved sale that no payment of the counter recorded within <paramref name="timeout"/>: the customer
    /// paid, but no invoice says so. Kept on the sale, for the counter, and as an exception for the accountants, who are
    /// alerted once; the counter can still record it on the sale, which clears the exception.
    /// </summary>
    private async Task KeepUnusedAsync(PosTransaction sale, TimeSpan timeout, CancellationToken ct)
    {
        var counter = CounterWords(sale.Owner);
        var minutes = timeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture);
        logger.LogWarning("Approved terminal sale {TransactionId} at the {Counter} is not recorded on an invoice", sale.TransactionId, sale.Owner);
        var message = $"{NotUsedPrefix}: the {counter} did not record it within {minutes} minutes. Record it on the sale, or refund it on the terminal; do not take the money again.";
        sale.Message = message.Length > 300 ? message[..300] : message;
        var reference = sale.Rrn is { } rrn ? $", RRN {rrn}" : sale.ApprovalCode is { } approval ? $", approval {approval}" : string.Empty;
        var exception = await discrepancies.RaiseAsync(
            DiscrepancyKind.UnrecordedPayment, sale.TransactionId, sale.FacilityId,
            $"₹{Money.FormatInr(sale.Amount)} was paid at the {counter} on card/UPI terminal {sale.TerminalId}{reference}, but no {counter} invoice records it within {minutes} minutes of the payment.",
            d =>
            {
                d.GatewayAmount = sale.Amount;
                d.GatewayAt = sale.CompletedAt;
            },
            ct);
        var isNew = db.Entry(exception).State == EntityState.Added;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // The counter recorded it meanwhile, or a concurrent check kept it first: the next check sees which.
            logger.LogInformation(ex, "Terminal sale {TransactionId} changed while it was kept as not recorded", sale.TransactionId);
            db.ChangeTracker.Clear();
            return;
        }

        if (isNew)
        {
            await AfterSaveAsync("alert the accountants", sale, () => notifications.NotifyPermissionHoldersAsync(
                BillingPermissions.GatewayReconcile, sale.FacilityId, $"Card/UPI payment not recorded at the {counter}",
                $"₹{Money.FormatInr(sale.Amount)} was paid on terminal {sale.TerminalId}, but no {counter} invoice records it. Ask the {counter} to record it on the sale, or refund it on the terminal; see Gateway reconciliation.",
                "/billing/reconciliation", "billing", "warning", ct));
        }
    }

    /// <summary>The counter that bills its own sales, in words: "pharmacy", "canteen".</summary>
    private static string CounterWords(string? owner) => owner?.ToLowerInvariant() ?? "counter";

    private static string ModeWords(PaymentMode mode) => mode == PaymentMode.Upi ? "UPI" : "card";

    private static string StatusWords(PosTransactionStatus status) => status switch
    {
        PosTransactionStatus.Declined => "declined",
        PosTransactionStatus.Cancelled => "cancelled",
        PosTransactionStatus.TimedOut => "timed out",
        _ => status.ToString(),
    };

    /// <summary>
    /// Tells the counter or the accountants once the sale is saved. Best effort: the money and the sale are recorded by
    /// then, and a failure here must not turn the cashier's request into an error, which lets them start the sale again
    /// and charge the customer twice. The exception stays in Gateway reconciliation even when its alert could not go.
    /// </summary>
    private async Task AfterSaveAsync(string what, PosTransaction sale, Func<Task> tell)
    {
        try
        {
            await tell();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not {What} about terminal sale {TransactionId}", what, sale.TransactionId);
        }
    }
}
