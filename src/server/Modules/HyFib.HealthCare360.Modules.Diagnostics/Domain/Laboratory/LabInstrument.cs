namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;

/// <summary>
/// An analyser connected through the lab-equipment gateway (INT-LAB, LIS-04). Results are accepted only from
/// registered, active instruments, so a mis-configured device on the network cannot write results.
/// </summary>
public sealed class LabInstrument : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    /// <summary>Identifier the gateway sends (HL7 MSH-3 sending application / ASTM header sender id).</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    public LabSection Section { get; set; }
    public InstrumentProtocol Protocol { get; set; }

    /// <summary>Supports host query: the analyser asks HC360 which tests to run for a scanned barcode.</summary>
    public bool IsBidirectional { get; set; }

    /// <summary>When false, every result from this analyser waits for manual verification.</summary>
    public bool AutoValidationEnabled { get; set; } = true;

    public string? Location { get; set; }
    public bool IsActive { get; set; } = true;
}
