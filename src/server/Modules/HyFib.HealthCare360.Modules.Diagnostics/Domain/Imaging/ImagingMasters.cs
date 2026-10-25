namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;

/// <summary>
/// Imaging procedure master (IMG-01). It maps a billable radiology service to its modality, body part, slot duration,
/// contrast requirement and patient preparation, which are used for scheduling, the modality worklist and patient
/// instructions.
/// </summary>
public sealed class ImagingProcedure : AuditableEntity
{
    /// <summary>Same code as the service item (RAD-CT-HEAD).</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public Modality Modality { get; set; }
    public string BodyPart { get; set; } = string.Empty;
    public Guid? ServiceItemId { get; set; }
    public int DurationMinutes { get; set; } = 15;
    public bool RequiresContrast { get; set; }
    public string? PatientPreparation { get; set; }

    /// <summary>An ultrasound in pregnancy: each one needs a PCPNDT Form F (<see cref="FormF"/>).</summary>
    public bool RequiresFormF { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Structured starting text for a report, chosen by modality and body part (IMG-03).</summary>
public sealed class ReportTemplate : AuditableEntity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Modality Modality { get; set; }

    /// <summary>Body part the template fits; null means any body part for the modality.</summary>
    public string? BodyPart { get; set; }

    public string? Technique { get; set; }
    public string Findings { get; set; } = string.Empty;
    public string Impression { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
