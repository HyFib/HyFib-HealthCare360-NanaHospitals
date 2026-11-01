namespace HyFib.HealthCare360.Modules.Clinical.Domain;

/// <summary>
/// Speciality template for the physician desktop (EMR-02): SOAP defaults, the complaints doctors pick most often
/// in that speciality, default investigations and default medicines. Applying a template only pre-fills the
/// draft — the doctor reviews and edits everything before signing.
/// </summary>
public sealed class ClinicalTemplate : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Speciality code from the organisation master (GM, PED, ORTH, OBG, CARD, ENT…).</summary>
    public string? SpecialtyCode { get; set; }

    /// <summary>Personal template of one practitioner; null = shared with everyone in the speciality.</summary>
    public Guid? OwnerPractitionerId { get; set; }

    public string? ChiefComplaint { get; set; }
    public string? HistoryOfPresentIllness { get; set; }
    public string? Examination { get; set; }
    public string? Assessment { get; set; }
    public string? Plan { get; set; }
    public string? FollowUpAdvice { get; set; }
    public int? FollowUpDays { get; set; }
    public List<string> CommonComplaints { get; set; } = [];

    /// <summary>Default investigations/procedures (org.service_items ids).</summary>
    public List<Guid> DefaultServiceItemIds { get; set; } = [];

    public List<TemplateMedication> Medications { get; set; } = [];
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// A default medicine of a template. <see cref="DrugItemId"/> may be empty until mapped to the drug master. Saving the
/// template replaces them, so its audit event lists them as they were and as they are (<c>medications</c>).
/// </summary>
[AuditedWithParent]
public sealed class TemplateMedication : Entity
{
    public Guid TemplateId { get; set; }
    public int Sequence { get; set; }
    public Guid? DrugItemId { get; set; }
    public string DrugName { get; set; } = string.Empty;
    public string? GenericName { get; set; }
    public string? Strength { get; set; }
    public string? DosageForm { get; set; }
    public string? Route { get; set; }
    public string Dose { get; set; } = string.Empty;
    public DoseFrequency Frequency { get; set; }
    public string? CustomFrequency { get; set; }
    public int DurationDays { get; set; }
    public string? Instructions { get; set; }
}

/// <summary>A named bundle of orders placed together (for example "Chest pain work-up": ECG, troponin, CBC).</summary>
public sealed class OrderSet : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? SpecialtyCode { get; set; }
    public string? Description { get; set; }
    public List<Guid> ServiceItemIds { get; set; } = [];
    public bool IsActive { get; set; } = true;
}

public enum FavouriteKind
{
    Order = 0,
    Drug,
}

/// <summary>
/// A practitioner's favourite order or medicine (ERX-01) with the defaults they usually use, so frequent
/// prescriptions take one click.
/// </summary>
public sealed class PractitionerFavourite : AuditableEntity
{
    public Guid PractitionerId { get; set; }
    public FavouriteKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;
    public Guid? ServiceItemId { get; set; }
    public Guid? DrugItemId { get; set; }
    public string? GenericName { get; set; }
    public string? Strength { get; set; }
    public string? DosageForm { get; set; }
    public string? Route { get; set; }
    public string? Dose { get; set; }
    public DoseFrequency? Frequency { get; set; }
    public string? CustomFrequency { get; set; }
    public int? DurationDays { get; set; }
    public string? Instructions { get; set; }
    public bool IsControlledDrug { get; set; }
    public int DisplayOrder { get; set; }
}
