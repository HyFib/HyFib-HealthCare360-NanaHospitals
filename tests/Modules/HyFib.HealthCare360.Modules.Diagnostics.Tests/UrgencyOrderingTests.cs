using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Contracts;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

/// <summary>Worklists put STAT work first even though priorities are stored as text (which sorts alphabetically).</summary>
public sealed class UrgencyOrderingTests
{
    private static readonly Row[] Rows =
    [
        new("routine, old", OrderPriority.Routine, 1),
        new("stat", OrderPriority.Stat, 2),
        new("urgent", OrderPriority.Urgent, 3),
        new("routine, new", OrderPriority.Routine, 4),
    ];

    [Fact]
    public void STAT_comes_first_then_urgent_then_routine() =>
        Rows.AsQueryable().OrderByUrgency(r => r.Priority).ThenBy(r => r.Sequence).Select(r => r.Name)
            .ShouldBe(["stat", "urgent", "routine, old", "routine, new"]);

    [Fact]
    public void Urgency_can_follow_a_more_important_sort_key() =>
        Rows.AsQueryable().OrderByDescending(r => r.Name.StartsWith("routine", StringComparison.Ordinal)).ThenByUrgency(r => r.Priority).ThenBy(r => r.Sequence)
            .Select(r => r.Name)
            .ShouldBe(["routine, old", "routine, new", "stat", "urgent"]);

    private sealed record Row(string Name, OrderPriority Priority, int Sequence);
}
