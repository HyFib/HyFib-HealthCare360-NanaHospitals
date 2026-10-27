namespace HyFib.HealthCare360.Modules.Quality.Domain;

public enum DowntimeKind
{
    /// <summary>The system went down unexpectedly.</summary>
    Unplanned = 0,

    /// <summary>Taken down on purpose: an upgrade, maintenance, a move of the server.</summary>
    Planned,

    /// <summary>A rehearsal of the downtime procedure: the staff worked on paper while the system stayed up.</summary>
    Drill,
}

public enum DowntimeCause
{
    ServerFailure = 0,
    NetworkOutage,
    PowerFailure,
    SoftwareFault,
    CyberIncident,
    Maintenance,
    Other,
}

public enum DowntimeStatus
{
    /// <summary>The system is still down (or the drill still running).</summary>
    Ongoing = 0,

    /// <summary>Back up; the paper records are still being entered.</summary>
    BackEntryPending,

    /// <summary>Back up and every paper record entered and checked.</summary>
    Closed,
}

/// <summary>
/// One spell of the hospital working without HC360, or a drill of it (NABH IMS: downtime is planned for, its procedure
/// rehearsed, and the downtime monitored; JCI MOI: records kept on paper during it are entered and checked afterwards).
/// It is usually recorded after the fact, since HC360 was down: when it began and ended, what was down and why, what was
/// done, and the sign-off once the paper records are entered.
/// </summary>
public sealed class DowntimeEvent : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>"{FAC}DT{YY}{SEQ:4}".</summary>
    public string Number { get; set; } = string.Empty;

    public DowntimeKind Kind { get; set; }
    public DowntimeCause Cause { get; set; }

    /// <summary>What could not be used: "All of HC360", "Laboratory and pharmacy", "Ward 3 network".</summary>
    public string Affected { get; set; } = string.Empty;

    public string? Description { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? RecordedByName { get; set; }

    /// <summary>What was done to restore the system, and what will stop it happening again.</summary>
    public string? ActionsTaken { get; set; }

    public string? EndedByName { get; set; }

    public DateTimeOffset? BackEntryCompletedAt { get; set; }

    /// <summary>How many paper records were entered: registrations, orders, results, notes, bills.</summary>
    public int? PaperRecords { get; set; }

    public string? BackEntryNote { get; set; }
    public string? BackEntrySignedByName { get; set; }

    public DowntimeStatus Status => EndedAt is null
        ? DowntimeStatus.Ongoing
        : BackEntryCompletedAt is null ? DowntimeStatus.BackEntryPending : DowntimeStatus.Closed;

    /// <summary>Whole minutes from start to end, or to <paramref name="now"/> while it lasts.</summary>
    public int Minutes(DateTimeOffset now) => (int)Math.Max(0, Math.Round(((EndedAt ?? now) - StartedAt).TotalMinutes));

    public Result End(DateTimeOffset endedAt, string? actionsTaken, string? byName, DateTimeOffset now)
    {
        if (EndedAt is not null)
        {
            return Error.Rule("downtime.ended", "This downtime has already ended.");
        }

        var check = CheckTimes(StartedAt, endedAt, now);
        if (check.IsFailure)
        {
            return check;
        }

        EndedAt = endedAt;
        ActionsTaken = Clean(actionsTaken);
        EndedByName = byName;
        return Result.Success();
    }

    /// <summary>The paper records kept during the downtime are entered and checked: the downtime is closed.</summary>
    public Result SignOffBackEntry(int paperRecords, string? note, string? byName, DateTimeOffset now)
    {
        if (EndedAt is null)
        {
            return Error.Rule("downtime.ongoing", "End the downtime before signing off its back-entry.");
        }

        if (BackEntryCompletedAt is not null)
        {
            return Error.Rule("downtime.closed", "The back-entry of this downtime has already been signed off.");
        }

        if (paperRecords < 0)
        {
            return Error.Field("paperRecords", "Enter how many paper records were entered, 0 or more.");
        }

        BackEntryCompletedAt = now;
        PaperRecords = paperRecords;
        BackEntryNote = Clean(note);
        BackEntrySignedByName = byName;
        return Result.Success();
    }

    /// <summary>A start (and end, when given) that make sense: not in the future, and the end after the start.</summary>
    public static Result CheckTimes(DateTimeOffset startedAt, DateTimeOffset? endedAt, DateTimeOffset now)
    {
        if (startedAt > now.AddMinutes(5))
        {
            return Error.Field("startedAt", "The start cannot be in the future.");
        }

        if (endedAt is { } end)
        {
            if (end > now.AddMinutes(5))
            {
                return Error.Field("endedAt", "The end cannot be in the future.");
            }

            if (end <= startedAt)
            {
                return Error.Field("endedAt", "The end must be after the start.");
            }
        }

        return Result.Success();
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
