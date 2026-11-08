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

    /// <summary>
    /// Closed short by more than the hospital's tolerance, and the shortage not yet accepted by a supervisor: the shift is
    /// closed (no money moves in it, and the cashier may open another), but the shortage stands on the cashier's word
    /// alone until someone else who approves refunds accepts it (MAKER-CHECKER.md, slice 6). An excess needs no one.
    /// </summary>
    public bool ShortageAwaiting { get; set; }

    /// <summary>The supervisor who accepted the shortage, never the cashier: their account, name and when.</summary>
    public Guid? ShortageAcceptedBy { get; set; }

    public string? ShortageAcceptedByName { get; set; }
    public DateTimeOffset? ShortageAcceptedAt { get; set; }

    public List<CashierShiftTotal> Totals { get; set; } = [];

    /// <summary>The cash short at the close (a positive amount), or 0 for a drawer that balanced or was over.</summary>
    public decimal Shortage => Variance is < 0 ? -Variance.Value : 0m;

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

    /// <param name="shortageTolerance">How much the drawer may be short and still close on the cashier's word.</param>
    public Result Close(ShiftReconciliation reconciliation, string? notes, DateTimeOffset now, decimal shortageTolerance = 0m)
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
        ShortageAwaiting = ShiftCalculator.ShortageNeedsSupervisor(reconciliation.Variance, shortageTolerance);
        return Result.Success();
    }

    /// <summary>A supervisor accepts the shortage the shift closed with; never the cashier whose shift it is.</summary>
    public Result AcceptShortage(Guid supervisorId, string supervisorName, DateTimeOffset now)
    {
        if (!ShortageAwaiting)
        {
            return Error.Rule("billing.shift_shortage_not_awaiting", "This shift's shortage is not waiting for a supervisor.");
        }

        if (supervisorId == UserId)
        {
            return Error.Forbidden(ShiftCalculator.OwnShortageCode, "This is your own shift, so another supervisor accepts its shortage.");
        }

        ShortageAwaiting = false;
        ShortageAcceptedBy = supervisorId;
        ShortageAcceptedByName = supervisorName;
        ShortageAcceptedAt = now;
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
    /// <summary>Nobody accepts the shortage of their own shift.</summary>
    public const string OwnShortageCode = "billing.shift_shortage_own";

    /// <summary>
    /// Whether a drawer closed with <paramref name="variance"/> (declared − expected) waits for a supervisor: a shortage
    /// larger than <paramref name="tolerance"/> does; an excess, or a drawer that balanced, never does.
    /// </summary>
    public static bool ShortageNeedsSupervisor(decimal variance, decimal tolerance) => variance < 0 && -variance > Math.Max(tolerance, 0m);

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
