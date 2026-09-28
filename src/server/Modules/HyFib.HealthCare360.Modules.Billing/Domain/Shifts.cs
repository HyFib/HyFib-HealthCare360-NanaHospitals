namespace HyFib.HealthCare360.Modules.Billing.Domain;

public enum ShiftStatus
{
    Open = 0,
    Closed,
}

/// <summary>
/// A cashier's session at a counter (OPB-07). Every counter receipt and cash refund belongs to a shift, so day-end
/// closing compares the drawer with what the system expects and records any variance against the cashier.
/// </summary>
public sealed class CashierShift : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Counter { get; set; } = string.Empty;
    public ShiftStatus Status { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal OpeningFloat { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal? DeclaredCash { get; set; }

    /// <summary>Declared − expected: negative is a shortage, positive an excess.</summary>
    public decimal? Variance { get; set; }

    public string? Notes { get; set; }
    public List<CashierShiftTotal> Totals { get; set; } = [];

    public static CashierShift Open(Guid facilityId, Guid userId, string userName, string counter, decimal openingFloat, DateTimeOffset now) => new()
    {
        FacilityId = facilityId,
        UserId = userId,
        UserName = userName,
        Counter = counter.Trim(),
        OpeningFloat = Money.Round(Math.Max(openingFloat, 0m)),
        ExpectedCash = Money.Round(Math.Max(openingFloat, 0m)),
        Status = ShiftStatus.Open,
        OpenedAt = now,
    };

    public Result Close(ShiftReconciliation reconciliation, string? notes, DateTimeOffset now)
    {
        if (Status != ShiftStatus.Open)
        {
            return Error.Rule("billing.shift_closed", "This shift is already closed.");
        }

        Status = ShiftStatus.Closed;
        ClosedAt = now;
        ExpectedCash = reconciliation.ExpectedCash;
        DeclaredCash = reconciliation.DeclaredCash;
        Variance = reconciliation.Variance;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        Totals = reconciliation.Totals
            .Select(t => new CashierShiftTotal { ShiftId = Id, Mode = t.Mode, Collected = t.Collected, Refunded = t.Refunded, Net = t.Net })
            .ToList();
        return Result.Success();
    }
}

/// <summary>Collections and refunds per payment mode, frozen when the shift closes.</summary>
public sealed class CashierShiftTotal : Entity
{
    public Guid ShiftId { get; set; }
    public PaymentMode Mode { get; set; }
    public decimal Collected { get; set; }
    public decimal Refunded { get; set; }
    public decimal Net { get; set; }
}

public sealed record ShiftMovement(PaymentMode Mode, decimal Amount, bool IsRefund);

public sealed record ShiftModeTotal(PaymentMode Mode, decimal Collected, decimal Refunded)
{
    public decimal Net => Collected - Refunded;
}

public sealed record ShiftReconciliation(IReadOnlyList<ShiftModeTotal> Totals, decimal OpeningFloat, decimal ExpectedCash, decimal DeclaredCash, decimal Variance)
{
    public decimal NetCollection => Totals.Sum(t => t.Net);
}

/// <summary>
/// Day-end cash closing: expected drawer = opening float + cash received − cash refunded; variance = declared − expected.
/// Advance adjustments and insurer settlements never pass through the counter, so they are excluded.
/// </summary>
public static class ShiftCalculator
{
    public static ShiftReconciliation Reconcile(decimal openingFloat, IEnumerable<ShiftMovement> movements, decimal declaredCash)
    {
        var totals = movements
            .Where(m => m.Mode is not (PaymentMode.Advance or PaymentMode.Insurance))
            .GroupBy(m => m.Mode)
            .OrderBy(g => g.Key)
            .Select(g => new ShiftModeTotal(g.Key, g.Where(m => !m.IsRefund).Sum(m => m.Amount), g.Where(m => m.IsRefund).Sum(m => m.Amount)))
            .ToList();

        var cash = totals.FirstOrDefault(t => t.Mode == PaymentMode.Cash);
        var expected = Money.Round(openingFloat) + (cash?.Net ?? 0m);
        var declared = Money.Round(declaredCash);
        return new ShiftReconciliation(totals, Money.Round(openingFloat), expected, declared, declared - expected);
    }
}
