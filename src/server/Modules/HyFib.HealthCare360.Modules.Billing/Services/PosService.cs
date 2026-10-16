using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Platform.Integrations;
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
/// the terminal approves — keyed on the terminal transaction id, so concurrent polls cannot record it twice.
/// </summary>
public sealed class PosService(
    HospitalDbContext db,
    ReceiptService receipts,
    ShiftService shifts,
    IServiceProvider services,
    IRealtimePublisher realtime,
    ICurrentUser currentUser,
    TimeProvider time,
    ILogger<PosService> logger)
{
    public async Task<Result<PosSaleDto>> StartAsync(StartPosSaleRequest request, CancellationToken ct)
    {
        var terminal = services.GetService<IPosTerminal>();
        if (terminal is null)
        {
            return Error.Rule("billing.pos_not_configured", "No card/UPI terminal integration is configured. Record the payment with the slip details instead.");
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

        var reference = Guid.CreateVersion7().ToString("N");
        PosTransactionResult result;
        try
        {
            result = await terminal.StartSaleAsync(
                new PosSaleRequest(request.TerminalId.Trim(), Money.Round(request.Amount), reference, request.Mode.ToString()), target.Value.FacilityId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not start a sale on terminal {TerminalId}", request.TerminalId);
            return Error.Rule("billing.pos_unavailable", "The terminal could not be reached. Check it is online and try again.");
        }

        var sale = new PosTransaction
        {
            FacilityId = target.Value.FacilityId,
            Provider = terminal.Provider,
            TerminalId = request.TerminalId.Trim(),
            TransactionId = result.TransactionId,
            Reference = reference,
            Amount = Money.Round(request.Amount),
            Mode = request.Mode,
            Purpose = target.Value.Purpose,
            BillId = target.Value.BillId,
            IpAccountId = target.Value.IpAccountId,
            PatientId = target.Value.PatientId,
            Status = PosTransactionStatus.Initiated,
            InitiatedBy = currentUser.UserId,
            ShiftId = shift.Value?.Id,
            Counter = shift.Value?.Counter,
        };
        sale.Apply(result, time.GetUtcNow());
        db.Add(sale);
        await db.SaveChangesAsync(ct);

        if (sale.Status == PosTransactionStatus.Approved)
        {
            await TryRecordAsync(sale, ct);
        }

        return ToDto(sale);
    }

    /// <summary>Polls the terminal (the counter calls this every few seconds while the customer pays).</summary>
    public async Task<Result<PosSaleDto>> RefreshAsync(Guid id, CancellationToken ct)
    {
        var sale = await db.Set<PosTransaction>().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sale is null)
        {
            return Error.NotFound(nameof(PosTransaction), id);
        }

        if (sale.IsComplete && (sale.Status != PosTransactionStatus.Approved || sale.ReceiptId is not null))
        {
            return ToDto(sale);
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

        if (sale.Status == PosTransactionStatus.Approved && sale.ReceiptId is null && !await TryRecordAsync(sale, ct))
        {
            var current = await db.Set<PosTransaction>().AsNoTracking().FirstAsync(s => s.Id == id, ct);
            return ToDto(current);
        }

        return ToDto(sale);
    }

    public async Task<Result<PosSaleDto>> CancelAsync(Guid id, CancellationToken ct)
    {
        var sale = await db.Set<PosTransaction>().FirstOrDefaultAsync(s => s.Id == id, ct);
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
        if (sale.Status == PosTransactionStatus.Approved && sale.ReceiptId is null)
        {
            // The customer completed the payment before the cancel reached the terminal: the money is ours to record.
            await TryRecordAsync(sale, ct);
        }

        return ToDto(sale);
    }

    /// <summary>
    /// The background re-check of a sale the counter left open (<see cref="PosRecheckRunner"/>): the terminal is asked
    /// with the sale's facility's settings, an approval is recorded once, and a sale still waiting for the customer after
    /// <paramref name="timeout"/> is cancelled on the terminal and marked timed out. An answer from a provider other than
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
            if (sale.IsComplete && sale.Status != PosTransactionStatus.Approved)
            {
                await realtime.ToFacilityAsync(sale.FacilityId, "billing.pos.changed", new { sale.Id, sale.Status, sale.BillId }, ct);
            }
        }

        if (sale.Status == PosTransactionStatus.Approved && sale.ReceiptId is null)
        {
            await TryRecordAsync(sale, ct);
        }
    }

    internal static PosSaleDto ToDto(PosTransaction s) => new(
        s.Id, s.Provider, s.TerminalId, s.TransactionId, s.Amount, s.Mode, s.Status, s.ApprovalCode, s.CardLast4, s.Rrn, s.Message, s.ReceiptId,
        s.CreatedAt, s.CompletedAt);

    /// <summary>Records the approved sale's receipt; returns false when a concurrent poll recorded it first.</summary>
    private async Task<bool> TryRecordAsync(PosTransaction sale, CancellationToken ct)
    {
        await using (var transaction = await db.BeginOwnedTransactionAsync(ct))
        {
            try
            {
                var shift = sale.ShiftId is { } shiftId ? await db.Set<CashierShift>().FirstOrDefaultAsync(s => s.Id == shiftId, ct) : null;
                var receipt = await receipts.RecordExternalAsync(new ExternalPayment(
                    sale.FacilityId, sale.Purpose, sale.PatientId, sale.BillId, sale.IpAccountId, sale.Mode, sale.Amount, sale.TransactionId, sale.Rrn,
                    sale.CardLast4, ReceiptSource.PosTerminal, shift), ct);
                if (receipt.IsFailure)
                {
                    logger.LogError("Approved terminal sale {TransactionId} could not be recorded: {Error}", sale.TransactionId, receipt.Error!.Message);
                    return true;
                }

                sale.ReceiptId = receipt.Value.Id;
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                logger.LogInformation(ex, "Terminal sale {TransactionId} was recorded by a concurrent request", sale.TransactionId);
                db.ChangeTracker.Clear();
                return false;
            }
        }

        await realtime.ToFacilityAsync(sale.FacilityId, "billing.pos.changed", new { sale.Id, sale.Status, sale.BillId }, ct);
        return true;
    }
}
