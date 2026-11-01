using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

/// <summary>The visit's queue token: when the doctor called it and began the service; null fields where nothing happened yet.</summary>
public sealed record TokenFact(string Label, DateTimeOffset? CalledAt, DateTimeOffset? ServiceStartedAt, string? Room);

/// <summary>A clinician's encounter of the visit: started, and signed (an amended note stays signed).</summary>
public sealed record EncounterFact(DateTimeOffset StartedAt, DateTimeOffset? SignedAt);

/// <summary>An e-prescription of the visit waiting in the pharmacy (cancelled ones and refills left out).</summary>
public sealed record DispensingFact(QueueEntryStatus Status, DateTimeOffset? LastDispensedAt);

/// <summary>What the records hold about one out-patient visit, from every module it passed through.</summary>
/// <param name="Token">The visit's queue token; null when the desk issued none.</param>
/// <param name="ClosedAt">When the visit was completed (or cancelled).</param>
public sealed record OutpatientFacts(
    VisitStatus Status,
    DateTimeOffset VisitedAt,
    DateTimeOffset? ClosedAt,
    TokenFact? Token,
    IReadOnlyList<EncounterFact> Encounters,
    IReadOnlyList<InvestigationFact> Investigations,
    IReadOnlyList<DispensingFact> Prescriptions,
    BillingFacts Billing);

/// <summary>
/// An out-patient visit from check-in to closing (OPD). Each stage is read from the record that proves it: the visit, its
/// queue token, the doctor's encounter, the order lines, the pharmacy queue and the visit's charges and bills. Tests,
/// medicines and billing run alongside the consultation (a consultation is often paid at registration), so they never
/// hold the patient's position; the consultation and the closing do.
/// </summary>
public static class OutpatientJourney
{
    public static readonly StageDefinition CheckedIn = new("checked-in", "Checked in", StageRole.Core);
    public static readonly StageDefinition Called = new("called", "Called", StageRole.Optional);
    public static readonly StageDefinition InConsultation = new("in-consultation", "In consultation", StageRole.Core);
    public static readonly StageDefinition ConsultationSigned = new("consultation-signed", "Consultation signed", StageRole.Core);
    public static readonly StageDefinition InvestigationsDone = new("investigations-done", "Investigations done", StageRole.Parallel);
    public static readonly StageDefinition Dispensed = new("dispensed", "Medicines dispensed", StageRole.Parallel);
    public static readonly StageDefinition Billed = new("billed", "Billed", StageRole.Parallel);
    public static readonly StageDefinition Paid = new("paid", "Paid", StageRole.Parallel);
    public static readonly StageDefinition Closed = new("closed", "Visit closed", StageRole.Core);

    public static IReadOnlyList<StageDefinition> Stages { get; } =
        [CheckedIn, Called, InConsultation, ConsultationSigned, InvestigationsDone, Dispensed, Billed, Paid, Closed];

    public static JourneyProgress Progress(OutpatientFacts facts, DateTimeOffset now) => JourneyStages.Resolve(Status(facts), Milestones(facts), now);

    public static JourneyStatus Status(OutpatientFacts facts) => facts.Status switch
    {
        VisitStatus.Cancelled => JourneyStatus.Cancelled,
        VisitStatus.Completed => JourneyStatus.Completed,
        _ => JourneyStatus.Active,
    };

    internal static IReadOnlyList<Milestone> Milestones(OutpatientFacts f)
    {
        var closed = f.Status == VisitStatus.Completed;

        // The consultation begins when the doctor starts the token or opens the encounter, whichever came first.
        var consultationAt = Facts.Earliest(f.Token?.ServiceStartedAt, f.Encounters.Count == 0 ? null : f.Encounters.Min(e => e.StartedAt));
        var signed = f.Encounters.Count > 0 && f.Encounters.All(e => e.SignedAt is not null);

        // Until the doctor signs the consultation (or the visit closes) a test or a prescription may still come.
        var decided = signed || closed;

        var (billed, paid) = Billing(f.Billing, decided);
        return
        [
            CheckedIn.Reached(f.VisitedAt, f.Token is { } token ? $"Token {token.Label}" : null),
            f.Token switch
            {
                null => Called.NotApplicable("No queue token"),
                { CalledAt: { } calledAt, Room: var room } => Called.Reached(calledAt, room),
                _ => Called.Open(),
            },
            consultationAt is { } startedAt ? InConsultation.Reached(startedAt) : InConsultation.Open(),
            signed
                ? ConsultationSigned.Reached(Facts.Latest(f.Encounters.Select(e => e.SignedAt)))
                : ConsultationSigned.Open(f.Encounters.Count > 0 ? "Note not signed yet" : null),
            Facts.Investigations(InvestigationsDone, f.Investigations, decided),
            Dispensing(f.Prescriptions, decided),
            billed,
            paid,
            closed ? Closed.Reached(f.ClosedAt) : Closed.Open(),
        ];
    }

    private static Milestone Dispensing(IReadOnlyList<DispensingFact> prescriptions, bool decided)
    {
        if (prescriptions.Count == 0)
        {
            return decided ? Dispensed.NotApplicable("Nothing prescribed") : Dispensed.Open();
        }

        // A pharmacist who closes an entry with part of it given marks it dispensed; the rest was declined or bought elsewhere.
        var dispensed = prescriptions.Count(p => p.Status == QueueEntryStatus.Dispensed);
        if (dispensed == prescriptions.Count)
        {
            return Dispensed.Reached(Facts.Latest(prescriptions.Select(p => p.LastDispensedAt)));
        }

        return Dispensed.Open(prescriptions.Any(p => p.Status == QueueEntryStatus.PartiallyDispensed) ? "Partly dispensed" : "Waiting at the pharmacy");
    }

    /// <summary>
    /// Billed once every service charged to the visit is on a finalized bill; paid once nothing is left to bill and nothing
    /// is due. A service charged later (a test the doctor orders after the consultation was paid) makes both pending again.
    /// </summary>
    private static (Milestone Billed, Milestone Paid) Billing(BillingFacts billing, bool decided)
    {
        if (billing.Charges == 0)
        {
            return decided
                ? (Billed.NotApplicable("Nothing charged"), Paid.NotApplicable("Nothing charged"))
                : (Billed.Open(), Paid.Open());
        }

        var billed = billing.Unbilled > 0
            ? Billed.Open($"{Facts.Count(billing.Unbilled, "service")} not billed yet")
            : Billed.Reached(Facts.Latest(billing.Bills.Select(b => b.FinalizedAt)));
        var due = billing.Due;
        var paid = billing.Unbilled == 0 && billing.Bills.Count > 0 && due == 0
            ? Paid.Reached(Facts.Latest(billing.Bills.Select(b => b.SettledAt)))
            : Paid.Open(due > 0 ? $"{Facts.Rupees(due)} due" : null);
        return (billed, paid);
    }
}
