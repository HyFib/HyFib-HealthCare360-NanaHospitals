namespace HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

/// <summary>
/// An investigation ordered for the visit or stay (lab, imaging, cardiology). Its order line is completed when its report
/// is released, which is the only way a line completes today; procedures, surgery and therapy lines never do, so they are
/// not counted as investigations.
/// </summary>
public sealed record InvestigationFact(DateTimeOffset? CompletedAt);

/// <summary>A finalized bill (not cancelled): what the patient still owes on it, and when the last payment came in.</summary>
public sealed record BillFact(DateTimeOffset? FinalizedAt, decimal Due, DateTimeOffset? LastPaymentAt, bool IsFinalBill = false)
{
    /// <summary>When the patient's share was settled: the last payment, or the finalization when the payer took it all.</summary>
    public DateTimeOffset? SettledAt => LastPaymentAt ?? FinalizedAt;
}

/// <summary>The services charged to a visit (not cancelled), those not yet on a finalized bill, and the bills holding the rest.</summary>
public sealed record BillingFacts(int Charges, int Unbilled, IReadOnlyList<BillFact> Bills)
{
    public static readonly BillingFacts None = new(0, 0, []);

    public decimal Due => Bills.Sum(b => b.Due);
}

/// <summary>Small helpers shared by the journeys' rules.</summary>
internal static class Facts
{
    /// <summary>The latest of the times known; null when none is.</summary>
    public static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> instants) => instants.Max();

    /// <summary>The earliest of the times known; null when none is.</summary>
    public static DateTimeOffset? Earliest(params DateTimeOffset?[] instants) => instants.Min();

    /// <summary>"1 service", "3 services".</summary>
    public static string Count(int count, string thing) => count == 1 ? $"1 {thing}" : $"{count} {thing}s";

    public static string Rupees(decimal amount) => $"₹{Money.FormatInr(amount)}";

    /// <summary>
    /// The investigations of a visit or stay as one stage: done when every line has its report, with the time of the last.
    /// With nothing ordered it does not apply, once <paramref name="decided"/> (the doctor has signed the consultation,
    /// or the visit is closed); before that a test may still be ordered.
    /// </summary>
    public static Milestone Investigations(StageDefinition stage, IReadOnlyList<InvestigationFact> lines, bool decided)
    {
        if (lines.Count == 0)
        {
            return decided ? stage.NotApplicable("None ordered") : stage.Open();
        }

        var resulted = lines.Count(l => l.CompletedAt is not null);
        return resulted == lines.Count
            ? stage.Reached(Latest(lines.Select(l => l.CompletedAt)))
            : stage.Open($"{resulted} of {lines.Count} resulted");
    }
}
