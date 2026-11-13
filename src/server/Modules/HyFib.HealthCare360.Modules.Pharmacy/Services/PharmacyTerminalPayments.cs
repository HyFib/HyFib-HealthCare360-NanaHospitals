using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Services;

/// <summary>
/// Records the card and UPI payments of an invoice that were taken on the hospital's terminal (<see cref="ICounterTerminal"/>):
/// each is marked as used by the pharmacy's payment, and its reference, approval code and card digits are the terminal's.
/// The marking is staged in the caller's unit of work, so the caller saves it with the invoice and neither is kept without
/// the other.
/// </summary>
internal static class PharmacyTerminalPayments
{
    /// <summary>Marks every payment that names a terminal sale as used, stopping at the first one the terminal refuses.</summary>
    public static async Task<Result> UseAsync(ICounterTerminal terminal, PharmacySale sale, IEnumerable<PharmacyPayment> payments, CancellationToken ct)
    {
        foreach (var payment in payments.Where(p => p.TerminalSaleId is not null))
        {
            CounterPaymentMode? taken = payment.Mode switch
            {
                PaymentMode.Card => CounterPaymentMode.Card,
                PaymentMode.Upi => CounterPaymentMode.Upi,
                _ => null,
            };
            if (taken is not { } mode)
            {
                return Error.Field("payments", "Only a card or UPI payment is taken on the terminal.");
            }

            var used = await terminal.UseAsync(
                new CounterTerminalUse(PharmacyDayBookSource.Key, payment.TerminalSaleId!.Value, payment.Id, sale.InvoiceNumber, mode, payment.Amount), ct);
            if (used.IsFailure)
            {
                return used.Error!;
            }

            payment.Reference = used.Value.Reference;
            payment.ApprovalCode = used.Value.ApprovalCode;
            payment.CardLast4 = used.Value.CardLast4;
        }

        return Result.Success();
    }
}
