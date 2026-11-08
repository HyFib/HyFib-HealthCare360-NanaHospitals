using System.Text.Json;

namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum CarePlanStatus
{
    Active = 0,
    Resolved,
}

/// <summary>How the patient is doing against a problem's goal, judged once a shift.</summary>
public enum CarePlanProgress
{
    Improving = 0,
    Unchanged,
    Worsening,
    GoalMet,
}

/// <summary>One nursing intervention and how often it is done ("Reposition every 2 hours").</summary>
public sealed record CarePlanIntervention(string Text, string Frequency);

/// <summary>A nursing problem from the short list a ward starts from, with its usual goal and interventions.</summary>
public sealed record CarePlanProblemTemplate(string Code, string Title, string Goal, IReadOnlyList<CarePlanIntervention> Interventions);

/// <summary>
/// The problems a nursing care plan is made from. Deliberately short (the common problems of a general ward); a problem
/// not on it is written under "Other" with its own goal and interventions. The goal and interventions are the starting
/// point the nurse edits for the patient.
/// </summary>
public static class CarePlanProblems
{
    public const string Other = "OTHER";

    public static IReadOnlyList<CarePlanProblemTemplate> All { get; } =
    [
        new("FALL_RISK", "Risk of falls", "No fall during the stay",
            [new("Bed at the lowest height, side rails up, call bell in reach", "Every shift"), new("Help to the toilet; non-slip footwear", "As needed"),
             new("Reassess Morse fall score", "Daily and after any change")]),
        new("PRESSURE_INJURY", "Risk of pressure injury", "Skin intact; no new pressure injury",
            [new("Reposition", "Every 2 hours"), new("Inspect heels, sacrum and bony prominences", "Every shift"),
             new("Pressure-relieving mattress; keep skin clean and dry", "Continuous")]),
        new("PAIN", "Acute pain", "Pain score 3 or less at rest",
            [new("Assess and record pain score", "Every 4 hours and 30 minutes after analgesia"), new("Give analgesia as charted", "As charted"),
             new("Positioning and comfort measures", "As needed")]),
        new("INFECTION_RISK", "Risk of infection (lines, wounds)", "No sign of line or wound infection",
            [new("Inspect cannula and wound sites (VIP score)", "Every shift"), new("Hand hygiene and aseptic technique for every line access", "Every contact"),
             new("Record temperature", "Every 4 hours")]),
        new("IMPAIRED_MOBILITY", "Impaired mobility", "Walks with help to the bathroom",
            [new("Assist to sit out of bed and walk", "Twice a shift"), new("Range-of-movement exercises", "Every shift")]),
        new("NUTRITION", "Poor nutrition or intake", "Eats at least half of each meal; weight stable",
            [new("Record food intake", "Every meal"), new("Weigh", "Twice a week"), new("Refer to the dietitian", "Once")]),
        new("FLUID_BALANCE", "Fluid imbalance", "Balanced intake and output; urine at least 0.5 ml/kg/h",
            [new("Intake and output chart", "Continuous"), new("Check IV infusion rate and site", "Every hour")]),
        new("BREATHING", "Impaired breathing", "SpO₂ at the prescribed target; breathing comfortably",
            [new("Respiration rate and SpO₂", "Every 4 hours or as NEWS2 asks"), new("Sit upright; oxygen as prescribed", "Continuous"),
             new("Encourage deep breathing and coughing", "Every 2 hours")]),
        new("ANXIETY", "Anxiety", "Patient and family say they understand the plan",
            [new("Explain care and answer questions", "Every shift"), new("Involve the family", "Daily")]),
    ];

    public static CarePlanProblemTemplate? Find(string code) => All.FirstOrDefault(p => p.Code == code);
}

/// <summary>
/// One problem of a stay's nursing care plan: the goal and the interventions with their frequency. It is evaluated each
/// shift (<see cref="CarePlanEvaluation"/>) and resolved when its goal is met or it no longer applies. Shown on the
/// handover sheet and the downtime print.
/// </summary>
public sealed class CarePlanProblem : AuditableEntity, IFacilityScoped
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public string ProblemCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;

    /// <summary>The interventions as JSON (<see cref="CarePlanIntervention"/>[]).</summary>
    public string InterventionsJson { get; set; } = "[]";

    public CarePlanStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public Guid? StartedBy { get; set; }
    public string? StartedByName { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedByName { get; set; }
    public string? ResolutionNote { get; set; }

    public IReadOnlyList<CarePlanIntervention> Interventions
    {
        get => JsonSerializer.Deserialize<List<CarePlanIntervention>>(InterventionsJson, Json) ?? [];
        set => InterventionsJson = JsonSerializer.Serialize(value, Json);
    }

    public Result Resolve(string? userName, string note, DateTimeOffset now)
    {
        if (Status == CarePlanStatus.Resolved)
        {
            return Error.Rule("care_plan.resolved", "This problem is already resolved.");
        }

        Status = CarePlanStatus.Resolved;
        ResolvedAt = now;
        ResolvedByName = userName;
        ResolutionNote = note.Trim();
        return Result.Success();
    }
}

/// <summary>A shift's evaluation of one care-plan problem: at most one per problem, duty date and shift.</summary>
public sealed class CarePlanEvaluation : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid ProblemId { get; set; }
    public Guid AdmissionId { get; set; }
    public DateOnly DutyDate { get; set; }
    public NursingShift Shift { get; set; }
    public CarePlanProgress Progress { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset EvaluatedAt { get; set; }
    public Guid? EvaluatedBy { get; set; }
    public string? EvaluatedByName { get; set; }
}
