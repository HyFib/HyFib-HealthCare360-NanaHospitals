using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

/// <summary>What the records hold about one patient's stay in the emergency department (casualty).</summary>
/// <param name="VisitStatus">The emergency visit's status: a visit cancelled at the desk stops the journey.</param>
/// <param name="Level">The level of the latest triage on the Emergency Severity Index, 1 (resuscitation) to 5.</param>
/// <param name="TriagedAt">The first triage; a re-triage changes the level, not the time.</param>
/// <param name="DoctorSeenAt">When a doctor first saw the patient.</param>
/// <param name="Investigations">The lab, imaging and cardiology lines ordered on the emergency visit.</param>
/// <param name="ReferredTo">The hospital a referred patient went to.</param>
/// <param name="AdmissionNumber">For a patient admitted from casualty, the stay opened from the emergency visit, once it is.</param>
public sealed record EmergencyFacts(
    VisitStatus VisitStatus,
    DateTimeOffset ArrivedAt,
    ArrivalMode ArrivalMode,
    int? Level,
    DateTimeOffset? TriagedAt,
    DateTimeOffset? DoctorSeenAt,
    string? DoctorName,
    IReadOnlyList<InvestigationFact> Investigations,
    EmergencyDisposition? Disposition,
    DateTimeOffset? DisposedAt,
    string? ReferredTo,
    string? AdmissionNumber);

/// <summary>
/// A patient's stay in casualty, from arrival to the decision on where they go: discharged, admitted, referred out, left
/// against medical advice (LAMA), absconded or died. Each stage is read from the emergency case the department keeps (the
/// arrival, the triage on the Emergency Severity Index, the first doctor, the disposition) and the order lines of the
/// emergency visit. Triage holds the patient's position until a doctor sees them without it (a patient in resuscitation is
/// treated first and graded after); the investigations run alongside and never hold it. A patient who absconded or died
/// before triage went without it.
/// </summary>
public static class EmergencyJourney
{
    public static readonly StageDefinition Arrived = new("arrived", "Arrived", StageRole.Core);
    public static readonly StageDefinition Triaged = new("triaged", "Triaged", StageRole.Optional);
    public static readonly StageDefinition SeenByDoctor = new("seen-by-doctor", "Seen by doctor", StageRole.Core);
    public static readonly StageDefinition InvestigationsDone = new("investigations-done", "Investigations done", StageRole.Parallel);
    public static readonly StageDefinition Disposition = new("disposition", "Disposition", StageRole.Core);

    public static IReadOnlyList<StageDefinition> Stages { get; } = [Arrived, Triaged, SeenByDoctor, InvestigationsDone, Disposition];

    public static JourneyProgress Progress(EmergencyFacts facts, DateTimeOffset now) => JourneyStages.Resolve(Status(facts), Milestones(facts), now);

    /// <summary>Cancelled with its visit; finished once the patient's disposition is recorded, whatever it was.</summary>
    public static JourneyStatus Status(EmergencyFacts facts) =>
        facts.VisitStatus == VisitStatus.Cancelled ? JourneyStatus.Cancelled
        : facts.Disposition is not null ? JourneyStatus.Completed
        : JourneyStatus.Active;

    internal static IReadOnlyList<Milestone> Milestones(EmergencyFacts f)
    {
        // Until the patient leaves casualty another test may be ordered.
        var decided = f.Disposition is not null;
        return
        [
            Arrived.Reached(f.ArrivedAt, ArrivalWords(f.ArrivalMode)),
            f.TriagedAt is { } triagedAt && f.Level is { } level ? Triaged.Reached(triagedAt, $"ESI {level}") : Triaged.Open("Waiting for triage"),
            f.DoctorSeenAt is { } seenAt ? SeenByDoctor.Reached(seenAt, f.DoctorName) : SeenByDoctor.Open(),
            Facts.Investigations(InvestigationsDone, f.Investigations, decided),
            f.Disposition is { } disposition ? Disposition.Reached(f.DisposedAt, DispositionWords(disposition, f.ReferredTo, f.AdmissionNumber)) : Disposition.Open(),
        ];
    }

    /// <summary>"Brought by police".</summary>
    public static string ArrivalWords(ArrivalMode mode) => mode switch
    {
        ArrivalMode.GovernmentAmbulance => "Government ambulance",
        ArrivalMode.PrivateAmbulance => "Private ambulance",
        ArrivalMode.Police => "Brought by police",
        ArrivalMode.ReferredIn => "Referred in",
        _ => "Walk-in",
    };

    /// <summary>"Admitted · IP-2026-000123", "Referred to District Hospital", "Left against medical advice".</summary>
    public static string DispositionWords(EmergencyDisposition disposition, string? referredTo, string? admissionNumber) => disposition switch
    {
        EmergencyDisposition.Discharged => "Discharged",
        EmergencyDisposition.Admitted => admissionNumber is null ? "Admitted, bed not yet given" : $"Admitted · {admissionNumber}",
        EmergencyDisposition.ReferredOut => string.IsNullOrWhiteSpace(referredTo) ? "Referred out" : $"Referred to {referredTo}",
        EmergencyDisposition.Lama => "Left against medical advice",
        EmergencyDisposition.Absconded => "Absconded",
        _ => "Died",
    };
}
