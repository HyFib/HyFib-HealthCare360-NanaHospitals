using System.Globalization;
using System.Text;

namespace HyFib.HealthCare360.LoadTest;

/// <summary>One request: how long it took to its last byte, and its HTTP status (0 when it never got an answer).</summary>
internal sealed record Sample(string Operation, OperationKind Kind, TimeSpan Elapsed, int Status)
{
    public bool Failed => Status is < 200 or >= 400;
}

/// <summary>An operation's figures over the measured part of the run, and whether they meet its target.</summary>
internal sealed record OperationResult(
    string Name, OperationKind Kind, int Count, int Failures, double P50, double P95, double P99, double Max, double TargetMs)
{
    /// <summary>Within the target, with at least one request that worked: failures have no time to be fast in.</summary>
    public bool Met => Count > Failures && P95 <= TargetMs;
}

/// <summary>A list's largest page as the server sends it (PERF-05: paginated, compressed, under 100 KB).</summary>
internal sealed record ListSize(string Name, long Bytes, string? Encoding, int Status)
{
    public const long LimitBytes = 100 * 1024;

    public bool Met => Status is >= 200 and < 300 && Encoding is "br" or "gzip" && Bytes <= LimitBytes;
}

internal sealed record LoadTestResult(
    Uri Server,
    int Users,
    TimeSpan Measured,
    IReadOnlyList<OperationResult> Operations,
    IReadOnlyList<ListSize> Lists,
    int Requests,
    int Failures,
    IReadOnlyDictionary<int, int> FailuresByStatus)
{
    /// <summary>More than this share of failed requests fails the run, whatever the timings.</summary>
    public const double MaxFailureRate = 0.01;

    public double RequestsPerSecond => Measured > TimeSpan.Zero ? Requests / Measured.TotalSeconds : 0;

    public double FailureRate => Requests == 0 ? 1 : (double)Failures / Requests;

    public bool TargetsMet => Operations.All(o => o.Met) && Lists.All(l => l.Met);

    public bool FewFailures => Requests > 0 && FailureRate <= MaxFailureRate;
}

internal static class Results
{
    /// <summary>The nearest-rank percentile <paramref name="percent"/> of <paramref name="sorted"/> (ascending), in ms.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double percent)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percent / 100 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    /// <summary>Each operation's figures, in the order of <paramref name="operations"/>, from the measured samples.</summary>
    public static IReadOnlyList<OperationResult> Summarize(IReadOnlyList<Operation> operations, IEnumerable<Sample> samples)
    {
        var byName = samples.GroupBy(s => s.Operation, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return
        [
            .. operations.Select(operation =>
            {
                var mine = byName.GetValueOrDefault(operation.Name) ?? [];
                var times = mine.Where(s => !s.Failed).Select(s => s.Elapsed.TotalMilliseconds).Order().ToList();
                return new OperationResult(
                    operation.Name, operation.Kind, mine.Count, mine.Count(s => s.Failed),
                    Percentile(times, 50), Percentile(times, 95), Percentile(times, 99), times.Count > 0 ? times[^1] : 0,
                    Operation.TargetOf(operation.Kind).TotalMilliseconds);
            }),
        ];
    }

    public static string Markdown(LoadTestResult result)
    {
        static string Ms(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

        var text = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;
        text.AppendLine("# HC360 load test");
        text.AppendLine();
        text.AppendLine(invariant, $"{result.Server} · {result.Users} virtual users · measured for {result.Measured.TotalMinutes:0.#} min · " +
                                   $"{result.Requests:N0} requests ({result.RequestsPerSecond:0.0} a second) · " +
                                   $"{result.Failures:N0} failed ({result.FailureRate:P1})");
        text.AppendLine();
        text.AppendLine("| Operation | Target (p95) | Requests | Failed | p50 ms | p95 ms | p99 ms | Max ms | Met |");
        text.AppendLine("|---|---|--:|--:|--:|--:|--:|--:|---|");
        foreach (var o in result.Operations)
        {
            text.AppendLine(invariant,
                $"| {o.Name} | {Ms(o.TargetMs)} ms ({Operation.RequirementOf(o.Kind)}) | {o.Count:N0} | {o.Failures:N0} | {Ms(o.P50)} | {Ms(o.P95)} | {Ms(o.P99)} | {Ms(o.Max)} | {(o.Met ? "yes" : "**no**")} |");
        }

        text.AppendLine();
        text.AppendLine("| List, largest page (PERF-05) | Sent | Compression | Met |");
        text.AppendLine("|---|--:|---|---|");
        foreach (var l in result.Lists)
        {
            var sent = l.Status is >= 200 and < 300 ? $"{l.Bytes / 1024.0:0.0} KB" : $"HTTP {l.Status}";
            text.AppendLine(invariant, $"| {l.Name} | {sent} | {l.Encoding ?? "none"} | {(l.Met ? "yes" : "**no**")} |");
        }

        if (result.FailuresByStatus.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Failed requests by status: " + string.Join(", ",
                result.FailuresByStatus.OrderBy(p => p.Key).Select(p => p.Key == 0 ? $"no answer {p.Value}" : $"{p.Key}: {p.Value}")));
        }

        text.AppendLine();
        text.AppendLine(result.TargetsMet && result.FewFailures
            ? "Every SRS 7.3 target checked here is met."
            : result.FewFailures
                ? "Some SRS 7.3 targets are missed (marked **no**)."
                : $"More than {LoadTestResult.MaxFailureRate:P0} of requests failed: the figures do not say how the server performs.");
        return text.ToString();
    }
}
