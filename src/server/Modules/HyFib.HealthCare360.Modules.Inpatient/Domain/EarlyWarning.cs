namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>How urgently NEWS2 asks for a doctor.</summary>
public enum EscalationLevel
{
    /// <summary>Aggregate 5–6, or a single parameter scoring 3: urgent review by the ward's doctor.</summary>
    Urgent = 0,

    /// <summary>Aggregate 7 or more: emergency response.</summary>
    Emergency,
}

public enum EscalationStatus
{
    Open = 0,
    Responded,
}

/// <summary>What the clinician who responded did.</summary>
public enum EscalationOutcome
{
    /// <summary>Reviewed; plan unchanged, observations continue at a closer interval.</summary>
    ReviewedContinueObservation = 0,

    /// <summary>Reviewed and treatment started or changed.</summary>
    TreatmentChanged,

    /// <summary>The rapid response or critical-care outreach team was called.</summary>
    RapidResponseCalled,

    /// <summary>Moved (or being moved) to critical care.</summary>
    TransferToCriticalCare,
}

/// <summary>
/// An early-warning escalation: a set of vital signs taken during a stay scored NEWS2 5 or more, or 3 in a single
/// parameter, so the ward's doctor (the stay's consultant) and the nurses in charge were told. It stays open until a
/// clinician other than whoever took the vital signs records who responded, when and what they did, so the chart shows
/// the escalation was answered and how fast. One open escalation per stay and level: a later score at the same or a lower
/// level does not raise another; a rise from urgent to emergency does.
/// </summary>
public sealed class EarlyWarningEscalation : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? WardId { get; set; }
    public Guid? BedId { get; set; }

    /// <summary>The vital signs (Clinical) that scored it.</summary>
    public Guid VitalSignsId { get; set; }

    public DateTimeOffset VitalsRecordedAt { get; set; }
    public Guid? VitalsRecordedBy { get; set; }
    public int Score { get; set; }
    public bool AnySingleParameterThree { get; set; }
    public bool IsScoreComplete { get; set; }

    /// <summary>"NEWS2 6", "NEWS2 3 (respiration scored 3)".</summary>
    public string Summary { get; set; } = string.Empty;

    public EscalationLevel Level { get; set; }
    public EscalationStatus Status { get; set; }
    public DateTimeOffset RaisedAt { get; set; }
    public Guid? ConsultantPractitionerId { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public Guid? RespondedBy { get; set; }
    public string? RespondedByName { get; set; }

    /// <summary>The one who responded is a doctor (their account is linked to a practitioner).</summary>
    public bool RespondedByDoctor { get; set; }

    public EscalationOutcome? Outcome { get; set; }
    public string? Response { get; set; }

    /// <summary>When the ward was reminded, once, that nobody had responded (<see cref="ReminderAfter"/>).</summary>
    public DateTimeOffset? ReminderSentAt { get; set; }

    /// <summary>
    /// How long an escalation, urgent or emergency, waits for a response before the ward is reminded: 30 minutes. Counted
    /// from when the escalation was raised, so vital signs entered late do not set off a reminder the moment they are saved.
    /// </summary>
    public static readonly TimeSpan ReminderAfter = TimeSpan.FromMinutes(30);

    /// <summary>Still open, not yet reminded, and waiting longer than its level allows.</summary>
    public bool IsReminderDue(DateTimeOffset now) =>
        Status == EscalationStatus.Open && ReminderSentAt is null && now - RaisedAt >= ReminderAfter;

    /// <summary>Minutes from the vital signs being recorded to the response.</summary>
    public int? MinutesToRespond => RespondedAt is { } at ? (int)Math.Max(0, (at - VitalsRecordedAt).TotalMinutes) : null;

    /// <summary>NEWS2 thresholds: emergency at 7+, urgent at 5–6 or any single parameter scoring 3, otherwise none.</summary>
    public static EscalationLevel? LevelFor(int score, bool anySingleParameterThree) =>
        score >= 7 ? EscalationLevel.Emergency
        : score >= 5 || anySingleParameterThree ? EscalationLevel.Urgent
        : null;

    public Result Respond(Guid? userId, string? userName, bool isDoctor, EscalationOutcome outcome, string response, DateTimeOffset now)
    {
        if (Status == EscalationStatus.Responded)
        {
            return Error.Rule("escalation.responded", $"{RespondedByName ?? "Someone"} already responded to this escalation.");
        }

        if (userId is not null && userId == VitalsRecordedBy)
        {
            return Error.Rule("escalation.self",
                "The escalation asks for a second clinician: the response is recorded by the doctor or nurse in charge who answered it, not by whoever took the vital signs.");
        }

        Status = EscalationStatus.Responded;
        RespondedAt = now;
        RespondedBy = userId;
        RespondedByName = userName;
        RespondedByDoctor = isDoctor;
        Outcome = outcome;
        Response = response.Trim();
        return Result.Success();
    }
}
