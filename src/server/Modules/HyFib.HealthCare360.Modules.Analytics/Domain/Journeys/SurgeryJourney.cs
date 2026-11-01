using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

/// <summary>What the records hold about one surgery booking.</summary>
/// <param name="ScheduledStart">The slot's start; when the slot was given is not stored.</param>
/// <param name="Theatre">The theatre's code, once one is booked.</param>
/// <param name="PreOpChecks">The pre-operative checklist, consent among its items, each with when it was recorded.</param>
/// <param name="ChargesFailed">The OT charges posted at the end of the operation failed and wait to be retried.</param>
public sealed record SurgeryFacts(
    SurgeryStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? ScheduledStart,
    string? Theatre,
    string? PostponeReason,
    IReadOnlyList<ChecklistEntry> PreOpChecks,
    DateTimeOffset? ReceivedInPreOpAt,
    DateTimeOffset? SignInAt,
    DateTimeOffset? TimeOutAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? SignOutAt,
    DateTimeOffset? CompletedAt,
    bool ChargesFailed);

/// <summary>
/// An operation from request to the patient leaving the theatre with the OT charges posted (OT-01..03), the stages of the
/// OT day-of-surgery stepper. The booking's own gates keep them in order (no Sign-in before the pre-op checklist, no
/// incision before the Time-out), so only receiving the patient in pre-op can be skipped. There is no recovery (PACU) or
/// back-to-ward record, so the journey ends at the theatre door. A postponement clears the day's checks, and the
/// stages follow.
/// </summary>
public static class SurgeryJourney
{
    public static readonly StageDefinition Requested = new("requested", "Requested", StageRole.Core);
    public static readonly StageDefinition Scheduled = new("scheduled", "Scheduled", StageRole.Core);
    public static readonly StageDefinition PreOp = new("pre-op", "Pre-op checks and consent", StageRole.Core);
    public static readonly StageDefinition Received = new("received", "Received in pre-op", StageRole.Optional);
    public static readonly StageDefinition SignIn = new("sign-in", "WHO Sign-in", StageRole.Core);
    public static readonly StageDefinition TimeOut = new("time-out", "WHO Time-out", StageRole.Core);
    public static readonly StageDefinition Incision = new("incision", "Incision", StageRole.Core);
    public static readonly StageDefinition SignOut = new("sign-out", "WHO Sign-out", StageRole.Core);
    public static readonly StageDefinition OutAndBilled = new("out-and-billed", "Out of theatre and billed", StageRole.Core);

    public static IReadOnlyList<StageDefinition> Stages { get; } = [Requested, Scheduled, PreOp, Received, SignIn, TimeOut, Incision, SignOut, OutAndBilled];

    public static JourneyProgress Progress(SurgeryFacts facts, DateTimeOffset now) => JourneyStages.Resolve(Status(facts), Milestones(facts), now);

    public static JourneyStatus Status(SurgeryFacts facts) =>
        facts.Status == SurgeryStatus.Cancelled ? JourneyStatus.Cancelled
        : facts.CompletedAt is not null && !facts.ChargesFailed ? JourneyStatus.Completed
        : JourneyStatus.Active;

    internal static IReadOnlyList<Milestone> Milestones(SurgeryFacts f)
    {
        // A requested or postponed booking has no slot to keep, whatever slot it once had.
        var slotHeld = f.ScheduledStart is not null && f.Status is not (SurgeryStatus.Requested or SurgeryStatus.Postponed);
        var preOpDone = f.PreOpChecks.Count(c => c.State != ChecklistItemState.Pending);
        return
        [
            Requested.Reached(f.RequestedAt),
            slotHeld ? Scheduled.Reached(null, Slot(f))
            : f.Status == SurgeryStatus.Postponed ? Scheduled.Open(f.PostponeReason is { } reason ? $"Postponed: {reason}" : "Postponed")
            : Scheduled.Open("Waiting for a theatre and time"),
            PreOpChecklist.IsComplete(f.PreOpChecks)
                ? PreOp.Reached(Facts.Latest(f.PreOpChecks.Select(c => c.RecordedAt)))
                : PreOp.Open($"{preOpDone} of {PreOpChecklist.Items.Count} checks done{(Consented(f) ? "" : ", consent not recorded")}"),
            f.ReceivedInPreOpAt is { } receivedAt ? Received.Reached(receivedAt) : Received.Open(),
            f.SignInAt is { } signInAt ? SignIn.Reached(signInAt) : SignIn.Open(),
            f.TimeOutAt is { } timeOutAt ? TimeOut.Reached(timeOutAt) : TimeOut.Open(),
            f.StartedAt is { } startedAt ? Incision.Reached(startedAt) : Incision.Open(),
            f.SignOutAt is { } signOutAt ? SignOut.Reached(signOutAt) : SignOut.Open(),

            // The charges are posted as the patient leaves the theatre; until a failed posting is retried the operation is not billed.
            f.CompletedAt is null ? OutAndBilled.Open()
            : f.ChargesFailed ? OutAndBilled.Open("Out of theatre; the OT charges were not posted")
            : OutAndBilled.Reached(f.CompletedAt),
        ];
    }

    private static string Slot(SurgeryFacts f) =>
        $"Slot {AnalyticsClock.FormatDateTime(f.ScheduledStart!.Value)}{(f.Theatre is null ? "" : $" in {f.Theatre}")}";

    /// <summary>Consent for surgery and anaesthesia is an item of the pre-op checklist, not a consent of the patient's record.</summary>
    private static bool Consented(SurgeryFacts f) => f.PreOpChecks.Any(c => c.Key == ConsentItem && c.State == ChecklistItemState.Done);

    /// <summary>The key of the consent item in <see cref="PreOpChecklist.Items"/>.</summary>
    private const string ConsentItem = "consent";
}
