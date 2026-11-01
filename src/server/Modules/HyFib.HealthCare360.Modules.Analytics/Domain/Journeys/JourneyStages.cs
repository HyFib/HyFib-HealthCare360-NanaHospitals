namespace HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

/// <summary>How a stage of a patient's journey shows on the pipeline.</summary>
public enum StageState
{
    /// <summary>It happened.</summary>
    Done = 0,

    /// <summary>Where the patient is now: the stage reached last that nothing before it still holds up.</summary>
    Current,

    /// <summary>It has not happened yet.</summary>
    Pending,

    /// <summary>The journey went past it without it happening (seen without being called, a stay that ended before billing cleared it).</summary>
    Skipped,

    /// <summary>It does not apply to this patient: nothing was ordered, nothing is paid before the test.</summary>
    NotApplicable,

    /// <summary>The visit, stay, operation or test was cancelled before it happened.</summary>
    Stopped,
}

public enum JourneyStatus
{
    Active = 0,
    Completed,
    Cancelled,
}

/// <summary>How a stage takes part in the patient's position on the pipeline.</summary>
public enum StageRole
{
    /// <summary>The patient is not past it until it happens: the consultation, the discharge, the incision.</summary>
    Core = 0,

    /// <summary>
    /// Holds the patient up like a core stage until a later core or optional stage happens without it; it is then skipped
    /// (a doctor who starts the consultation without calling the token, a sample drawn without paying first where the
    /// hospital allows it).
    /// </summary>
    Optional,

    /// <summary>
    /// Runs alongside the others and never holds the patient's position: tests, medicines and billing happen before, during
    /// or after the consultation. It stays pending until it happens, so what is still to come keeps showing.
    /// </summary>
    Parallel,
}

public enum MilestoneStatus
{
    Reached = 0,
    Open,
    NotApplicable,
}

/// <summary>A stage of one kind of journey: its key (stable, for the staff app), its label and its part in the patient's position.</summary>
public sealed record StageDefinition(string Key, string Label, StageRole Role)
{
    /// <summary>It happened, at <paramref name="at"/> (null where nothing stores the time, such as when an operation was given its slot).</summary>
    public Milestone Reached(DateTimeOffset? at, string? detail = null) => new(this, MilestoneStatus.Reached, at, detail);

    public Milestone Open(string? detail = null) => new(this, MilestoneStatus.Open, null, detail);

    public Milestone NotApplicable(string? detail = null) => new(this, MilestoneStatus.NotApplicable, null, detail);
}

/// <summary>A stage as the records show it: whether it happened, when, and a short note ("Token GM-012", "₹350.00 due").</summary>
public sealed record Milestone(StageDefinition Stage, MilestoneStatus Status, DateTimeOffset? At, string? Detail);

/// <summary>
/// A stage as the pipeline shows it. <see cref="MinutesInStage"/> is set on the current stage alone, and only when the
/// time it was reached is stored.
/// </summary>
public sealed record JourneyStage(string Key, string Label, StageState State, DateTimeOffset? At, int? MinutesInStage, string? Detail);

/// <summary>A journey's status and its stages in order, ready for the pipeline.</summary>
public sealed record JourneyProgress(JourneyStatus Status, IReadOnlyList<JourneyStage> Stages)
{
    public JourneyStage? Current => Stages.FirstOrDefault(s => s.State == StageState.Current);
}

/// <summary>
/// Turns what the records say about each stage into what the pipeline shows. Nothing here is set by hand: a stage is done
/// when the record that proves it exists, and the patient's position follows from the stages done.
/// </summary>
public static class JourneyStages
{
    public static JourneyProgress Resolve(JourneyStatus status, IReadOnlyList<Milestone> milestones, DateTimeOffset now)
    {
        if (status != JourneyStatus.Active)
        {
            // A cancelled journey stops where it was; a finished one went without whatever did not happen. Neither has a
            // stage the patient is in.
            var unreached = status == JourneyStatus.Cancelled ? StageState.Stopped : StageState.Skipped;
            return new JourneyProgress(status, [.. milestones.Select(m => Show(m, m.Status switch
            {
                MilestoneStatus.Reached => StageState.Done,
                MilestoneStatus.NotApplicable => StageState.NotApplicable,
                _ => unreached,
            }, now))]);
        }

        var states = new StageState[milestones.Count];
        var holdUp = milestones.Count;
        for (var i = 0; i < milestones.Count; i++)
        {
            var m = milestones[i];
            states[i] = m.Status switch
            {
                MilestoneStatus.Reached => StageState.Done,
                MilestoneStatus.NotApplicable => StageState.NotApplicable,
                _ when m.Stage.Role == StageRole.Optional && PassedOver(milestones, i) => StageState.Skipped,
                _ => StageState.Pending,
            };

            if (states[i] == StageState.Pending && m.Stage.Role != StageRole.Parallel && holdUp == milestones.Count)
            {
                holdUp = i;
            }
        }

        var current = CurrentIndex(milestones, holdUp);
        if (current >= 0)
        {
            states[current] = StageState.Current;
        }

        return new JourneyProgress(status, [.. milestones.Select((m, i) => Show(m, states[i], now))]);
    }

    /// <summary>A later stage that holds the patient's position happened without this optional one.</summary>
    private static bool PassedOver(IReadOnlyList<Milestone> milestones, int index) =>
        milestones.Skip(index + 1).Any(m => m.Status == MilestoneStatus.Reached && m.Stage.Role != StageRole.Parallel);

    /// <summary>
    /// The stage the patient is in: of the stages reached before the first one that holds them up, the one reached last.
    /// A stage whose time is not stored counts as reached after the stages before it, so an operation given its slot is
    /// past its request. Two at the same moment (admitted and put in a bed) go to the later stage.
    /// </summary>
    private static int CurrentIndex(IReadOnlyList<Milestone> milestones, int holdUp)
    {
        var current = -1;
        var currentAt = DateTimeOffset.MinValue;
        var knownSoFar = DateTimeOffset.MinValue;
        for (var i = 0; i < holdUp; i++)
        {
            var m = milestones[i];
            if (m.Status != MilestoneStatus.Reached)
            {
                continue;
            }

            var at = m.At ?? knownSoFar;
            if (current < 0 || at >= currentAt)
            {
                current = i;
                currentAt = at;
            }

            if (m.At is { } known && known > knownSoFar)
            {
                knownSoFar = known;
            }
        }

        return current;
    }

    private static JourneyStage Show(Milestone m, StageState state, DateTimeOffset now) => new(
        m.Stage.Key,
        m.Stage.Label,
        state,
        m.At,
        state == StageState.Current && m.At is { } at ? (int)Math.Max(0, Math.Floor((now - at).TotalMinutes)) : null,
        m.Detail);
}
