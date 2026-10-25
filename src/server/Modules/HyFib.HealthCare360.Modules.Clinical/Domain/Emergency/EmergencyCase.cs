namespace HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;

public enum ArrivalMode
{
    WalkIn = 0,
    GovernmentAmbulance,
    PrivateAmbulance,
    Police,
    ReferredIn,
}

/// <summary>How a patient leaves the emergency department.</summary>
public enum EmergencyDisposition
{
    Discharged = 0,
    Admitted,
    ReferredOut,

    /// <summary>Left against medical advice.</summary>
    Lama,
    Absconded,
    Died,
}

/// <summary>
/// A patient's stay in the emergency department, opened with their emergency visit: the arrival, each triage, when a
/// doctor first saw them and how they left. The times are the department's measures (door to triage, door to doctor,
/// length of stay).
/// </summary>
public sealed class EmergencyCase : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid VisitId { get; set; }
    public DateTimeOffset ArrivedAt { get; set; }
    public ArrivalMode ArrivalMode { get; set; }

    /// <summary>The level of the latest triage, 1 (resuscitation) to 5; null until triaged.</summary>
    public int? Level { get; private set; }

    public DateTimeOffset? TriagedAt { get; private set; }
    public DateTimeOffset? DoctorSeenAt { get; private set; }
    public string? DoctorName { get; private set; }
    public EmergencyDisposition? Disposition { get; private set; }
    public DateTimeOffset? DisposedAt { get; private set; }
    public string? DisposedByName { get; private set; }
    public string? DispositionNotes { get; private set; }

    /// <summary>The hospital a referred patient goes to.</summary>
    public string? ReferredTo { get; private set; }

    public bool IsOpen => Disposition is null;

    public Result Triage(EsiTriage triage, ArrivalMode arrivalMode)
    {
        if (!IsOpen)
        {
            return Error.Rule("er.closed", "The patient has left the emergency department.");
        }

        Level = triage.Level;
        TriagedAt ??= triage.AssessedAt;
        ArrivalMode = arrivalMode;
        return Result.Success();
    }

    /// <summary>The first doctor to see the patient; later doctors do not change the time.</summary>
    public Result SeenBy(string doctorName, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("er.closed", "The patient has left the emergency department.");
        }

        if (DoctorSeenAt is null)
        {
            DoctorSeenAt = now;
            DoctorName = doctorName;
        }

        return Result.Success();
    }

    public Result Dispose(EmergencyDisposition disposition, string? notes, string? referredTo, string byName, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Rule("er.closed", "The patient has already left the emergency department.");
        }

        if (Level is null && disposition is not (EmergencyDisposition.Absconded or EmergencyDisposition.Died))
        {
            return Error.Rule("er.not_triaged", "Triage the patient before deciding where they go.");
        }

        if (disposition == EmergencyDisposition.ReferredOut && string.IsNullOrWhiteSpace(referredTo))
        {
            return Error.Field("referredTo", "Name the hospital the patient is referred to.");
        }

        if (disposition is (EmergencyDisposition.Lama or EmergencyDisposition.Absconded or EmergencyDisposition.Died) && string.IsNullOrWhiteSpace(notes))
        {
            return Error.Field("notes", disposition switch
            {
                EmergencyDisposition.Lama => "Note the risks explained and who signed the LAMA form.",
                EmergencyDisposition.Absconded => "Note when the patient was last seen and who was informed.",
                _ => "Note the time and cause of death as declared.",
            });
        }

        Disposition = disposition;
        DisposedAt = now;
        DisposedByName = byName;
        DispositionNotes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        ReferredTo = disposition == EmergencyDisposition.ReferredOut ? referredTo!.Trim() : null;
        return Result.Success();
    }
}

/// <summary>One triage of an emergency patient: the answers, the vitals, the level the algorithm gave and the one assigned.</summary>
public sealed class EsiTriage : AuditableEntity
{
    public Guid CaseId { get; set; }
    public DateTimeOffset AssessedAt { get; set; }
    public string AssessedByName { get; set; } = string.Empty;
    public string ChiefComplaint { get; set; } = string.Empty;
    public bool NeedsLifeSavingIntervention { get; set; }
    public bool HighRiskSituation { get; set; }
    public bool ConfusedLethargicOrDisoriented { get; set; }
    public bool SevereDistress { get; set; }
    public int ExpectedResources { get; set; }
    public int? Pulse { get; set; }
    public int? RespiratoryRate { get; set; }
    public int? SpO2 { get; set; }
    public int? SystolicBp { get; set; }
    public int? DiastolicBp { get; set; }
    public decimal? TemperatureCelsius { get; set; }
    public int? Gcs { get; set; }
    public int? PainScore { get; set; }

    /// <summary>What the algorithm gave.</summary>
    public int ComputedLevel { get; set; }

    /// <summary>The algorithm's reasons, one per line.</summary>
    public string Reasons { get; set; } = string.Empty;

    /// <summary>The level the nurse assigned: the computed one, or another with <see cref="OverrideReason"/>.</summary>
    public int Level { get; set; }

    public string? OverrideReason { get; set; }

    /// <summary>The vitals the triage wrote to the patient's vitals chart.</summary>
    public Guid? VitalSignsId { get; set; }

    /// <summary>Assesses the answers and vitals and assigns the level; a level other than the computed one needs a reason.</summary>
    public static Result<EsiTriage> Assess(
        Guid caseId, string chiefComplaint, EsiAnswers answers, TriageVitals vitals, int? ageInDays, int? assignedLevel, string? overrideReason,
        string byName, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(chiefComplaint))
        {
            return Error.Field("chiefComplaint", "Enter the chief complaint.");
        }

        var assessment = EmergencySeverityIndex.Assess(answers, vitals, ageInDays);
        var level = assignedLevel ?? assessment.Level;
        if (level is < 1 or > 5)
        {
            return Error.Field("level", "The ESI level is 1 to 5.");
        }

        if (level != assessment.Level && string.IsNullOrWhiteSpace(overrideReason))
        {
            return Error.Field("overrideReason", $"The algorithm gives level {assessment.Level}. Say why the patient is level {level}.");
        }

        return new EsiTriage
        {
            CaseId = caseId,
            AssessedAt = now,
            AssessedByName = byName,
            ChiefComplaint = chiefComplaint.Trim(),
            NeedsLifeSavingIntervention = answers.NeedsLifeSavingIntervention,
            HighRiskSituation = answers.HighRiskSituation,
            ConfusedLethargicOrDisoriented = answers.ConfusedLethargicOrDisoriented,
            SevereDistress = answers.SevereDistress,
            ExpectedResources = Math.Clamp(answers.ExpectedResources, 0, 2),
            Pulse = vitals.Pulse,
            RespiratoryRate = vitals.RespiratoryRate,
            SpO2 = vitals.SpO2,
            SystolicBp = vitals.SystolicBp,
            DiastolicBp = vitals.DiastolicBp,
            TemperatureCelsius = vitals.TemperatureCelsius,
            Gcs = vitals.Gcs,
            PainScore = vitals.PainScore,
            ComputedLevel = assessment.Level,
            Reasons = string.Join('\n', assessment.Reasons),
            Level = level,
            OverrideReason = level == assessment.Level ? null : overrideReason!.Trim(),
        };
    }
}
