namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum BedStatus
{
    Vacant = 0,
    Occupied,
    Reserved,
    Cleaning,
    Blocked,
    Maintenance,
}

/// <summary>
/// Allowed bed status transitions (WB-02). An occupied bed always passes through Cleaning, so no patient is placed in a
/// bed that was not terminally cleaned; blocking and maintenance need a reason because they reduce the bed availability
/// reported to management and the reason is shown on the bed board.
/// </summary>
public static class BedStateMachine
{
    private static readonly Dictionary<BedStatus, BedStatus[]> Allowed = new()
    {
        [BedStatus.Vacant] = [BedStatus.Occupied, BedStatus.Reserved, BedStatus.Cleaning, BedStatus.Blocked, BedStatus.Maintenance],
        [BedStatus.Reserved] = [BedStatus.Occupied, BedStatus.Vacant],
        [BedStatus.Occupied] = [BedStatus.Cleaning],
        [BedStatus.Cleaning] = [BedStatus.Vacant, BedStatus.Blocked, BedStatus.Maintenance],
        [BedStatus.Blocked] = [BedStatus.Vacant, BedStatus.Cleaning, BedStatus.Maintenance],
        [BedStatus.Maintenance] = [BedStatus.Cleaning, BedStatus.Blocked],
    };

    public static bool CanMove(BedStatus from, BedStatus to) => Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <summary>Statuses reachable from <paramref name="from"/> (drives which actions the bed board offers).</summary>
    public static IReadOnlyList<BedStatus> NextStatuses(BedStatus from) => Allowed.TryGetValue(from, out var targets) ? targets : [];

    public static bool RequiresReason(BedStatus to) => to is BedStatus.Blocked or BedStatus.Maintenance;

    public static Result Validate(BedStatus from, BedStatus to, string? reason)
    {
        if (from == to)
        {
            return Error.Rule("bed.same_status", $"The bed is already {Describe(to)}.");
        }

        if (!CanMove(from, to))
        {
            return Error.Rule("bed.invalid_transition", $"A bed that is {Describe(from)} cannot become {Describe(to)}.");
        }

        if (RequiresReason(to) && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Rule("bed.reason_required", $"Give a reason for marking the bed {Describe(to)}.");
        }

        return Result.Success();
    }

    public static string Describe(BedStatus status) => status switch
    {
        BedStatus.Vacant => "vacant",
        BedStatus.Occupied => "occupied",
        BedStatus.Reserved => "reserved",
        BedStatus.Cleaning => "under cleaning",
        BedStatus.Blocked => "blocked",
        BedStatus.Maintenance => "under maintenance",
        _ => status.ToString(),
    };
}
