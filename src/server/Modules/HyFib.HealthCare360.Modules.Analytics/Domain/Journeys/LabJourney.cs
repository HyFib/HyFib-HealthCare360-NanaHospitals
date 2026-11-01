using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

namespace HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

/// <summary>
/// One test of the accession that is not cancelled. Each time is null until the test has reached that point; a test's
/// times are those of its tube (collected, received or sent out to a reference lab) and of its results.
/// </summary>
/// <param name="CoveredByPackage">A health-checkup package component: paid with the package, not on its own.</param>
/// <param name="Paid">Its charge is on a bill the patient has paid.</param>
/// <param name="PaidAt">When that bill was settled.</param>
/// <param name="ResultedAt">When the last of its results was entered; null while any is still to come.</param>
/// <param name="AutoValidated">Every result was verified by the auto-validation rules rather than a pathologist.</param>
public sealed record LabTestFact(
    bool CoveredByPackage,
    bool Paid,
    DateTimeOffset? PaidAt,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? ResultedAt,
    DateTimeOffset? VerifiedAt,
    bool AutoValidated,
    DateTimeOffset? ReleasedAt);

/// <summary>What the records hold about one lab accession.</summary>
/// <param name="PaysBeforeCollection">An out-patient or walk-in, who pays before the sample is drawn (not one treated free by law).</param>
/// <param name="HasOrderingDoctor">Ordered by a doctor of the hospital, who reviews the released result.</param>
/// <param name="ReviewedAt">When the ordering doctor marked the report reviewed; an amended report asks for review again.</param>
public sealed record LabFacts(
    LabOrderStatus Status,
    DateTimeOffset OrderedAt,
    bool PaysBeforeCollection,
    bool HasOrderingDoctor,
    IReadOnlyList<LabTestFact> Tests,
    DateTimeOffset? ReviewedAt);

/// <summary>
/// A lab accession from the order to the doctor's review of the report (LIS-02..07). A stage is reached when every test
/// of the accession has reached it, at the time the last one did, so a 72-hour culture keeps the accession at "Resulted"
/// while its CBC is out. Paying first applies to out-patients and walk-ins (the collection payment gate); it is skipped
/// when the sample was drawn without it, as a hospital that turns the gate off allows.
/// </summary>
public static class LabJourney
{
    public static readonly StageDefinition Ordered = new("ordered", "Ordered", StageRole.Core);
    public static readonly StageDefinition PaymentCleared = new("payment-cleared", "Payment cleared", StageRole.Optional);
    public static readonly StageDefinition Collected = new("collected", "Sample collected", StageRole.Core);
    public static readonly StageDefinition Received = new("received", "Received in lab", StageRole.Core);
    public static readonly StageDefinition Resulted = new("resulted", "Resulted", StageRole.Core);
    public static readonly StageDefinition Verified = new("verified", "Verified", StageRole.Core);
    public static readonly StageDefinition Released = new("released", "Report released", StageRole.Core);
    public static readonly StageDefinition Reviewed = new("reviewed", "Reviewed by the doctor", StageRole.Core);

    public static IReadOnlyList<StageDefinition> Stages { get; } = [Ordered, PaymentCleared, Collected, Received, Resulted, Verified, Released, Reviewed];

    public static JourneyProgress Progress(LabFacts facts, DateTimeOffset now) => JourneyStages.Resolve(Status(facts), Milestones(facts), now);

    /// <summary>Finished once every test is released and, for a doctor's order, the doctor has reviewed it.</summary>
    public static JourneyStatus Status(LabFacts facts) =>
        facts.Status == LabOrderStatus.Cancelled ? JourneyStatus.Cancelled
        : AllReleased(facts) && (!facts.HasOrderingDoctor || facts.ReviewedAt is not null) ? JourneyStatus.Completed
        : JourneyStatus.Active;

    internal static IReadOnlyList<Milestone> Milestones(LabFacts f)
    {
        var payable = f.Tests.Where(t => !t.CoveredByPackage).ToList();
        var verified = Every(Verified, f.Tests, t => t.VerifiedAt, "verified");
        return
        [
            Ordered.Reached(f.OrderedAt, Facts.Count(f.Tests.Count, "test")),
            !f.PaysBeforeCollection || payable.Count == 0 ? PaymentCleared.NotApplicable()
            : payable.All(t => t.Paid) ? PaymentCleared.Reached(Facts.Latest(payable.Select(t => t.PaidAt)))
            : PaymentCleared.Open($"{payable.Count(t => !t.Paid)} of {Facts.Count(payable.Count, "test")} unpaid"),
            Every(Collected, f.Tests, t => t.CollectedAt, "collected"),
            Every(Received, f.Tests, t => t.ReceivedAt, "received"),
            Every(Resulted, f.Tests, t => t.ResultedAt, "resulted"),
            verified.Status == MilestoneStatus.Reached && f.Tests.All(t => t.AutoValidated) ? verified with { Detail = "Auto-validated" } : verified,
            Every(Released, f.Tests, t => t.ReleasedAt, "released"),
            !f.HasOrderingDoctor ? Reviewed.NotApplicable("No ordering doctor in the hospital")
            : f.ReviewedAt is { } reviewedAt ? Reviewed.Reached(reviewedAt)
            : Reviewed.Open(),
        ];
    }

    private static bool AllReleased(LabFacts facts) => facts.Tests.Count > 0 && facts.Tests.All(t => t.ReleasedAt is not null);

    /// <summary>Reached when every test has reached it, at the time of the last; "1 of 3 collected" while some have.</summary>
    private static Milestone Every(StageDefinition stage, IReadOnlyList<LabTestFact> tests, Func<LabTestFact, DateTimeOffset?> at, string verb)
    {
        var reached = tests.Count(t => at(t) is not null);
        return tests.Count > 0 && reached == tests.Count
            ? stage.Reached(Facts.Latest(tests.Select(at)))
            : stage.Open(reached > 0 ? $"{reached} of {tests.Count} {verb}" : null);
    }
}
