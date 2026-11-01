using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

/// <summary>
/// Billing's position on the stay: whether anything was charged, the services not yet on a finalized bill, the finalized
/// bills (the final bill among them) and when management approved a discharge with dues.
/// </summary>
public sealed record ClearanceFacts(bool HasCharges, int Unbilled, IReadOnlyList<BillFact> Bills, DateTimeOffset? DuesApprovedAt)
{
    public static readonly ClearanceFacts None = new(false, 0, [], null);
}

/// <summary>The terminal cleaning of the bed the patient left at discharge (the housekeeping task, unless it was cancelled).</summary>
public sealed record CleaningFact(DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

/// <summary>What the records hold about one in-patient stay.</summary>
/// <param name="Source">Where the admission desk recorded the patient came from.</param>
/// <param name="SourceVisitNumber">The OPD or casualty visit the admission links, which the stay goes on from.</param>
/// <param name="BeddedAt">The start of the stay's first bed (every admission takes a bed today, so it is the admission time).</param>
/// <param name="Bed">The ward and bed the patient is in ("General Ward — Male · GWM-07").</param>
/// <param name="FirstRoundAt">The first ward round, nursing note or nursing assessment of the stay.</param>
/// <param name="SummaryDrafted">A discharge summary is being written but is not signed.</param>
/// <param name="Cleaning">The cleaning of the bed left at discharge; null before discharge, or when the bed was not left for cleaning.</param>
public sealed record InpatientFacts(
    AdmissionStatus Status,
    DateTimeOffset AdmittedAt,
    AdmissionSource Source,
    string? SourceVisitNumber,
    DateTimeOffset? BeddedAt,
    string? Bed,
    DateTimeOffset? FirstRoundAt,
    IReadOnlyList<InvestigationFact> Investigations,
    DateTimeOffset? DischargeInitiatedAt,
    DateTimeOffset? SummarySignedAt,
    bool SummaryDrafted,
    ClearanceFacts Clearance,
    DateTimeOffset? DischargedAt,
    DischargeType? DischargeType,
    CleaningFact? Cleaning)
{
    /// <summary>The stay ended: discharged, or left against advice, absconded or died (each records when).</summary>
    public bool HasLeft => DischargedAt is not null && Status is not (AdmissionStatus.Admitted or AdmissionStatus.DischargeInitiated or AdmissionStatus.Cancelled);
}

/// <summary>
/// An in-patient stay from admission until the bed the patient left is clean again (ADT, WB-05). Tests and the discharge
/// summary run alongside the stay; billing clearance holds a normal discharge (the discharge itself checks it) but not
/// a patient who leaves against advice, absconds or dies, so it is skipped then.
/// </summary>
public static class InpatientJourney
{
    public static readonly StageDefinition Admitted = new("admitted", "Admitted", StageRole.Core);
    public static readonly StageDefinition Bedded = new("bedded", "In a bed", StageRole.Core);
    public static readonly StageDefinition RoundsAndNursing = new("rounds-and-nursing", "Rounds and nursing", StageRole.Optional);
    public static readonly StageDefinition InvestigationsComplete = new("investigations-complete", "Investigations complete", StageRole.Parallel);
    public static readonly StageDefinition DischargeInitiated = new("discharge-initiated", "Discharge initiated", StageRole.Core);
    public static readonly StageDefinition SummarySigned = new("summary-signed", "Discharge summary signed", StageRole.Parallel);
    public static readonly StageDefinition Cleared = new("cleared", "Cleared by billing", StageRole.Optional);
    public static readonly StageDefinition Discharged = new("discharged", "Discharged", StageRole.Core);
    public static readonly StageDefinition BedCleaned = new("bed-cleaned", "Bed cleaned", StageRole.Core);

    public static IReadOnlyList<StageDefinition> Stages { get; } =
        [Admitted, Bedded, RoundsAndNursing, InvestigationsComplete, DischargeInitiated, SummarySigned, Cleared, Discharged, BedCleaned];

    public static JourneyProgress Progress(InpatientFacts facts, DateTimeOffset now) => JourneyStages.Resolve(Status(facts), Milestones(facts), now);

    /// <summary>Finished once the patient has left and the bed is clean (or was not left for cleaning).</summary>
    public static JourneyStatus Status(InpatientFacts facts) =>
        facts.Status == AdmissionStatus.Cancelled ? JourneyStatus.Cancelled
        : facts.HasLeft && (facts.Cleaning is null || facts.Cleaning.CompletedAt is not null) ? JourneyStatus.Completed
        : JourneyStatus.Active;

    internal static IReadOnlyList<Milestone> Milestones(InpatientFacts f) =>
    [
        Admitted.Reached(f.AdmittedAt, From(f.Source, f.SourceVisitNumber)),
        f.BeddedAt is { } beddedAt ? Bedded.Reached(beddedAt, f.Bed) : Bedded.Open(),
        f.FirstRoundAt is { } roundAt ? RoundsAndNursing.Reached(roundAt) : RoundsAndNursing.Open(),

        // A stay can go days without a test: until one is ordered there is nothing to complete.
        Facts.Investigations(InvestigationsComplete, f.Investigations, decided: true),
        f.DischargeInitiatedAt is { } initiatedAt ? DischargeInitiated.Reached(initiatedAt) : DischargeInitiated.Open(),
        f.SummarySignedAt is { } signedAt ? SummarySigned.Reached(signedAt) : SummarySigned.Open(f.SummaryDrafted ? "Draft not signed yet" : null),
        Clearance(f.Clearance),
        f.HasLeft ? Discharged.Reached(f.DischargedAt, Describe(f.DischargeType)) : Discharged.Open(),
        !f.HasLeft ? BedCleaned.Open()
        : f.Cleaning is null ? BedCleaned.NotApplicable("Bed not sent for cleaning")
        : f.Cleaning.CompletedAt is { } cleanedAt ? BedCleaned.Reached(cleanedAt)
        : BedCleaned.Open("Waiting for housekeeping"),
    ];

    /// <summary>
    /// Cleared as Billing clears a discharge: everything billed on a final bill and nothing due from the patient, at the
    /// later of the final bill and the last payment; or a discharge with dues that management approved.
    /// </summary>
    private static Milestone Clearance(ClearanceFacts c)
    {
        if (c.DuesApprovedAt is { } approvedAt)
        {
            return Cleared.Reached(approvedAt, "Discharge with dues approved");
        }

        if (!c.HasCharges)
        {
            return Cleared.NotApplicable("Nothing charged");
        }

        var due = c.Bills.Sum(b => b.Due);
        var finalBill = c.Bills.Any(b => b.IsFinalBill);
        if (finalBill && c.Unbilled == 0 && due == 0)
        {
            return Cleared.Reached(Facts.Latest(c.Bills.Select(b => b.SettledAt)));
        }

        return Cleared.Open(
            c.Unbilled > 0 ? $"{Facts.Count(c.Unbilled, "service")} not billed yet"
            : !finalBill ? "No final bill yet"
            : $"{Facts.Rupees(due)} due");
    }

    /// <summary>"From OPD visit NHV260000076": where the stay comes from, in the admit screen's words, naming the visit it links.</summary>
    private static string From(AdmissionSource source, string? visitNumber) => (source, visitNumber) switch
    {
        (AdmissionSource.OutPatient, { } number) => $"From OPD visit {number}",
        (AdmissionSource.OutPatient, null) => "Advised in OPD",
        (AdmissionSource.Emergency, { } number) => $"From casualty visit {number}",
        (AdmissionSource.Emergency, null) => "From casualty / ER",
        (AdmissionSource.Referral, _) => "Referred by a doctor or hospital",
        _ => "Direct admission",
    };

    private static string? Describe(DischargeType? type) => type switch
    {
        DischargeType.Referred => "Referred to another hospital",
        DischargeType.Lama => "Left against medical advice (LAMA)",
        DischargeType.Dama => "Discharged against medical advice (DAMA)",
        DischargeType.Absconded => "Absconded",
        DischargeType.Expired => "Expired",
        _ => null,
    };
}
