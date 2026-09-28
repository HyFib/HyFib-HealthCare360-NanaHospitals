namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

public enum SpecimenDestination
{
    Histopathology = 0,
    Microbiology,
    Cytology,
    FrozenSection,
    Other,
}

public sealed record SurgicalSpecimen(string Description, SpecimenDestination SentTo, string? Label);

/// <summary>
/// Intra-operative (operation) note and post-operative orders written by the operating surgeon (OT-05).
/// Implants are kept as <see cref="SurgeryMaterial"/> rows so they can be traced by lot/serial in a recall.
/// </summary>
public sealed class OperationNote : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid SurgeryBookingId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? SurgeonId { get; set; }

    public string? PreOperativeDiagnosis { get; set; }
    public string? PostOperativeDiagnosis { get; set; }
    public string ProcedurePerformed { get; set; } = string.Empty;
    public string? Findings { get; set; }

    /// <summary>Incision, steps, closure.</summary>
    public string? ProcedureDetails { get; set; }

    public List<SurgicalSpecimen> Specimens { get; set; } = [];
    public string? Drains { get; set; }
    public int? EstimatedBloodLossMl { get; set; }
    public string? Complications { get; set; }

    /// <summary>Post-op orders handed to recovery/ward nursing (monitoring, fluids, analgesia, diet, drains, positioning).</summary>
    public string? PostOpOrders { get; set; }

    public DateTimeOffset? SignedAt { get; set; }
    public string? SignedByName { get; set; }

    public bool IsSigned => SignedAt is not null;

    public Result EnsureEditable() =>
        IsSigned ? Error.Rule("operation_note.signed", "This operation note is signed and can no longer be changed.") : Result.Success();

    public Result Sign(string signedBy, DateTimeOffset now)
    {
        var editable = EnsureEditable();
        if (editable.IsFailure)
        {
            return editable;
        }

        if (string.IsNullOrWhiteSpace(ProcedurePerformed))
        {
            return Error.Field("procedurePerformed", "Record the procedure performed before signing.");
        }

        SignedAt = now;
        SignedByName = signedBy;
        return Result.Success();
    }
}
