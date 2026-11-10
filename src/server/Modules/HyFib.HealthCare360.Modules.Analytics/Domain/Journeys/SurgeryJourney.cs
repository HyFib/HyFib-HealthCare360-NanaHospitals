using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

/// <summary>What the records hold about one surgery booking.</summary>
/// <param name="ScheduledStart">The slot's start; when the slot was given is not stored.</param>
/// <param name="Theatre">The theatre's code, once one is booked.</param>
/// <param name="PreOpChecks">The pre-operative checklist, consent among its items, each with when it was recorded.</param>
/// <param name="ChargesFailed">The OT charges posted at the end of the operation failed and wait to be retried.</param>
/// <param name="RecoveryReceivedAt">The recovery nurse received the patient (the recovery record's time in).</param>
/// <param name="RecoveryReceivedBy">Who received the patient in recovery.</param>
/// <param name="RecoveryDischargedAt">The patient left the recovery room (time out), for <paramref name="RecoveryDestination"/>.</param>
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
    bool ChargesFailed,
    DateTimeOffset? RecoveryReceivedAt = null,
    string? RecoveryReceivedBy = null,
    DateTimeOffset? RecoveryDischargedAt = null,
    RecoveryDestination? RecoveryDestination = null);

/// <summary>
/// An operation from request until the patient leaves the recovery room (PACU) for the ward or intensive care, with the OT
/// charges posted (OT-01..03), the stages of the OT day-of-surgery stepper. The booking's own gates keep them in order (no
/// Sign-in before the pre-op checklist, no incision before the Time-out), so only receiving the patient in pre-op can be
/// skipped. From the WHO Sign-out the patient is in recovery until discharged from it; billing, posted as the patient
/// leaves the theatre, runs alongside. A postponement clears the day's checks, and the stages follow.
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
    public static readonly StageDefinition InRecovery = new("in-recovery", "In recovery", StageRole.Core);
    public static readonly StageDefinition RecoveryDischarged = new("recovery-discharged", "Discharged from recovery", StageRole.Core);
    public static readonly StageDefinition OutAndBilled = new("out-and-billed", "Out of theatre and billed", StageRole.Parallel);

    public static IReadOnlyList<StageDefinition> Stages { get; } =
        [Requested, Scheduled, PreOp, Received, SignIn, TimeOut, Incision, SignOut, InRecovery, RecoveryDischarged, OutAndBilled];

    public static JourneyProgress Progress(SurgeryFacts facts, DateTimeOffset now) => JourneyStages.Resolve(Status(facts), Milestones(facts), now);

    public static JourneyStatus Status(SurgeryFacts facts) =>
        facts.Status == SurgeryStatus.Cancelled ? JourneyStatus.Cancelled
        : facts.CompletedAt is not null && !facts.ChargesFailed && facts.RecoveryDischargedAt is not null ? JourneyStatus.Completed
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

            // From the Sign-out the patient is the recovery room's, received there or on the way to it.
            f.RecoveryReceivedAt is { } inAt ? InRecovery.Reached(inAt, f.RecoveryReceivedBy is { } by ? $"Received by {by}" : null)
            : f.SignOutAt is { } outAt && f.RecoveryDischargedAt is null ? InRecovery.Reached(outAt, "Not yet received in recovery")
            : f.RecoveryDischargedAt is not null ? InRecovery.NotApplicable("No recovery record")
            : InRecovery.Open(),
            f.RecoveryDischargedAt is { } leftAt ? RecoveryDischarged.Reached(leftAt, To(f.RecoveryDestination)) : RecoveryDischarged.Open(),

            // The charges are posted as the patient leaves the theatre; until a failed posting is retried the operation is not billed.
            f.CompletedAt is null ? OutAndBilled.Open()
            : f.ChargesFailed ? OutAndBilled.Open("Out of theatre; the OT charges were not posted")
            : OutAndBilled.Reached(f.CompletedAt),
        ];
    }

    private static string? To(RecoveryDestination? destination) => destination switch
    {
        RecoveryDestination.Ward => "Back to the ward",
        RecoveryDestination.Icu => "To the ICU",
        RecoveryDestination.Hdu => "To the HDU",
        _ => null,
    };

    private static string Slot(SurgeryFacts f) =>
        $"Slot {AnalyticsClock.FormatDateTime(f.ScheduledStart!.Value)}{(f.Theatre is null ? "" : $" in {f.Theatre}")}";

    /// <summary>Consent for surgery and anaesthesia is an item of the pre-op checklist, not a consent of the patient's record.</summary>
    private static bool Consented(SurgeryFacts f) => f.PreOpChecks.Any(c => c.Key == ConsentItem && c.State == ChecklistItemState.Done);

    /// <summary>The key of the consent item in <see cref="PreOpChecklist.Items"/>.</summary>
    private const string ConsentItem = "consent";
}
