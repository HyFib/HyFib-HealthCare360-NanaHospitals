namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum NursingSafetyEventKind
{
    Fall = 0,
    PressureInjury,
}

/// <summary>The injury a fall did, on the NDNQI levels nursing quality programmes use.</summary>
public enum FallInjury
{
    None = 0,

    /// <summary>Needed only a dressing, ice, cleaning or a limb raised.</summary>
    Minor,

    /// <summary>Needed sutures, steri-strips, skin glue or a splint.</summary>
    Moderate,

    /// <summary>Needed surgery, casting or traction; a fracture; neurological or internal injury.</summary>
    Major,
    Death,
}

/// <summary>Pressure injury stages (NPIAP 2016).</summary>
public enum PressureInjuryStage
{
    Stage1 = 0,
    Stage2,
    Stage3,
    Stage4,
    Unstageable,
    DeepTissue,
}

/// <summary>
/// A fall, or a pressure injury found, recorded on the nursing chart. A fall, and a pressure injury that developed in
/// hospital, is reported to the Quality incident register as it is recorded; a pressure injury present on admission is
/// documented and not reported (it is not the hospital's harm, and the incidence indicator leaves it out). Never edited:
/// a mistake is corrected with a nursing note.
/// </summary>
public sealed class NursingSafetyEvent : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? WardId { get; set; }
    public NursingSafetyEventKind Kind { get; set; }

    /// <summary>When the patient fell, or when the pressure injury was found.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public string Location { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? ImmediateAction { get; set; }

    public FallInjury? FallInjury { get; set; }

    /// <summary>A fall someone saw (an unwitnessed fall is often found later).</summary>
    public bool? Witnessed { get; set; }

    public PressureInjuryStage? Stage { get; set; }

    /// <summary>Sacrum, heel, occiput…</summary>
    public string? BodySite { get; set; }

    /// <summary>Present when the patient was admitted (documented at the admission skin check): not hospital-acquired.</summary>
    public bool PresentOnAdmission { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
    public Guid? RecordedBy { get; set; }
    public string? RecordedByName { get; set; }

    /// <summary>The incident the event raised in the Quality register, when it raised one.</summary>
    public string? IncidentNumber { get; set; }

    /// <summary>Whether the event goes to the incident register: every fall, and a pressure injury that developed in hospital.</summary>
    public bool IsReportable => Kind == NursingSafetyEventKind.Fall || !PresentOnAdmission;
}

/// <summary>The harm grade (NABH incident reporting) an event is reported with.</summary>
public static class NursingSafetyHarm
{
    /// <summary>Fall: by the injury. Pressure injury: stage 1 or 2 mild, stage 3 moderate, stage 4, unstageable and deep tissue severe.</summary>
    public static IncidentHarm Of(NursingSafetyEvent safetyEvent)
    {
        ArgumentNullException.ThrowIfNull(safetyEvent);
        return safetyEvent.Kind == NursingSafetyEventKind.Fall
            ? safetyEvent.FallInjury switch
            {
                Domain.FallInjury.Minor => IncidentHarm.Mild,
                Domain.FallInjury.Moderate => IncidentHarm.Moderate,
                Domain.FallInjury.Major => IncidentHarm.Severe,
                Domain.FallInjury.Death => IncidentHarm.Death,
                _ => IncidentHarm.NoHarm,
            }
            : safetyEvent.Stage switch
            {
                PressureInjuryStage.Stage1 or PressureInjuryStage.Stage2 => IncidentHarm.Mild,
                PressureInjuryStage.Stage3 => IncidentHarm.Moderate,
                _ => IncidentHarm.Severe,
            };
    }
}
