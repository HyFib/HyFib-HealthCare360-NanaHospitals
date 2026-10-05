using HyFib.HealthCare360.Integrations.Domain;

namespace HyFib.HealthCare360.Integrations.Payments;

/// <summary>When a simulated settlement run happens, and which transactions it may take.</summary>
/// <param name="SettleAt">The settlement's time (10:00 IST on its day, or the moment of the fetch when settling at once).</param>
/// <param name="CoverBefore">Payments and refunds made before this instant are settled in the run.</param>
internal sealed record SimulatorSettlementRun(DateTimeOffset SettleAt, DateTimeOffset CoverBefore);

/// <summary>
/// The training gateway's settlements, built like a real gateway's: captured payments are paid to the bank after
/// <c>simulatorSettlementDays</c> (T+1 by default, at 10:00 IST) less a 2 % fee and 18 % GST on the fee, and refunds are
/// taken from the next settlement. With 0 days everything captured so far settles when settlements are fetched.
/// </summary>
internal static class SimulatorSettlements
{
    public const decimal FeePercent = 2m;
    public const decimal GstPercent = 18m;

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);
    private static readonly TimeSpan SettlementHour = TimeSpan.FromHours(10);

    /// <summary>The run for <paramref name="day"/>, or null while it is not due yet.</summary>
    public static SimulatorSettlementRun? Plan(DateOnly day, int delayDays, DateTimeOffset now)
    {
        var (start, end) = Day(day);
        if (start > now)
        {
            return null;
        }

        if (delayDays <= 0)
        {
            var at = now < end ? now : end.AddSeconds(-1);
            return new SimulatorSettlementRun(at, at);
        }

        var settleAt = start + SettlementHour;
        return settleAt > now ? null : new SimulatorSettlementRun(settleAt, start.AddDays(-(Math.Min(delayDays, 7) - 1)));
    }

    /// <summary>The UTC range of an Indian calendar day.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Day(DateOnly day)
    {
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), Ist).ToUniversalTime();
        return (start, start.AddDays(1));
    }

    /// <summary>The gateway's fee on a payment, GST included, and the GST part, in rupees.</summary>
    public static (decimal Fee, decimal Tax) Fee(decimal amount)
    {
        var fee = Math.Round(amount * FeePercent / 100m, 2, MidpointRounding.AwayFromZero);
        var tax = Math.Round(fee * GstPercent / 100m, 2, MidpointRounding.AwayFromZero);
        return (fee + tax, tax);
    }

    /// <summary>Groups settled simulator transactions into settlements, with the amount that reached the "bank".</summary>
    public static IReadOnlyList<PaymentSettlement> Build(IEnumerable<SimulatorTransaction> settled) =>
        settled
            .Where(t => t.SettlementId is not null && t.SettledAt is not null)
            .GroupBy(t => t.SettlementId!)
            .Select(group =>
            {
                var items = group.OrderBy(t => t.CreatedAt).Select(Item).OfType<PaymentSettlementItem>().ToList();
                var settledAt = group.Max(t => t.SettledAt!.Value);
                return new PaymentSettlement(
                    group.Key,
                    "SIMUTR" + (group.Key.Length > 10 ? group.Key[^10..] : group.Key).ToUpperInvariant(),
                    PaymentSettlementStatus.Processed,
                    items.Sum(i => i.Credit) - items.Sum(i => i.Debit),
                    0m,
                    0m,
                    settledAt,
                    items);
            })
            .OrderBy(s => s.SettledAt)
            .ToList();

    private static PaymentSettlementItem? Item(SimulatorTransaction t)
    {
        if (t.Kind == SimulatorTransactionKind.PaymentRefund)
        {
            return new PaymentSettlementItem(t.ExternalId, PaymentTransactionType.Refund, t.Reference, null, t.Amount, 0m, 0m, 0m, t.Amount, "simulator", t.CreatedAt);
        }

        if (t.PaymentId is null)
        {
            return null;
        }

        var (fee, tax) = Fee(t.Amount);
        return new PaymentSettlementItem(t.PaymentId, PaymentTransactionType.Payment, null, t.ExternalId, t.Amount, fee, tax, t.Amount - fee, 0m, "simulator", t.CreatedAt);
    }
}
