namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;

public sealed record WestgardEvaluation(QcOutcome Outcome, IReadOnlyList<string> Violations)
{
    public bool IsRejected => Outcome == QcOutcome.Rejected;
}

/// <summary>
/// Westgard multirule QC evaluation (LIS-06, pure; unit-tested). A new control result is judged together with the
/// assay's recent accepted results, expressed as z-scores so that different levels and lots are comparable. The
/// series runs oldest to newest and ends with the new result.
/// <list type="bullet">
/// <item><b>1-2s</b> (warning): the new result is beyond ±2 SD.</item>
/// <item><b>1-3s</b> (reject, random error): the new result is beyond ±3 SD.</item>
/// <item><b>2-2s</b> (reject, systematic error): the last two results are beyond 2 SD on the same side.</item>
/// <item><b>R-4s</b> (reject, random error): of the last two results, one is above +2 SD and the other below −2 SD.</item>
/// <item><b>4-1s</b> (reject, systematic error): the last four results are beyond 1 SD on the same side.</item>
/// <item><b>10x</b> (reject, systematic error): the last ten results are on the same side of the mean.</item>
/// </list>
/// All rejection rules are always checked, not only after a 1-2s warning. Gradual shifts (4-1s, 10x) often develop
/// without any result crossing 2 SD. A limit crossing is strict: a result exactly at +2 SD does not violate 1-2s.
/// </summary>
internal static class WestgardEvaluator
{
    public const string Warning12s = "1-2s";
    public const string Reject13s = "1-3s";
    public const string Reject22s = "2-2s";
    public const string RejectR4s = "R-4s";
    public const string Reject41s = "4-1s";
    public const string Reject10x = "10x";

    /// <summary>How many earlier runs the rules can look back over (10x needs nine plus the new one).</summary>
    public const int HistoryDepth = 9;

    /// <param name="history">Z-scores of earlier accepted runs of the same assay, oldest first. Rejected runs are left out because they were acted on already.</param>
    /// <param name="current">Z-score of the new run.</param>
    public static WestgardEvaluation Evaluate(IReadOnlyList<decimal> history, decimal current)
    {
        var series = history.TakeLast(HistoryDepth).Append(current).ToList();
        var violations = new List<string>();

        if (Math.Abs(current) > 3)
        {
            violations.Add(Reject13s);
        }

        if (SameSideBeyond(series, 2, 2))
        {
            violations.Add(Reject22s);
        }

        if (series.Count >= 2)
        {
            var previous = series[^2];
            if ((current > 2 && previous < -2) || (current < -2 && previous > 2))
            {
                violations.Add(RejectR4s);
            }
        }

        if (SameSideBeyond(series, 4, 1))
        {
            violations.Add(Reject41s);
        }

        if (SameSideBeyond(series, 10, 0))
        {
            violations.Add(Reject10x);
        }

        if (violations.Count > 0)
        {
            return new WestgardEvaluation(QcOutcome.Rejected, violations);
        }

        return Math.Abs(current) > 2
            ? new WestgardEvaluation(QcOutcome.AcceptedWithWarning, [Warning12s])
            : new WestgardEvaluation(QcOutcome.Accepted, []);
    }

    /// <summary>True when the last <paramref name="count"/> z-scores all exceed +limit, or all fall below −limit.</summary>
    private static bool SameSideBeyond(IReadOnlyList<decimal> series, int count, decimal limit)
    {
        if (series.Count < count)
        {
            return false;
        }

        var window = series.Skip(series.Count - count).ToList();
        return window.All(z => z > limit) || window.All(z => z < -limit);
    }
}
