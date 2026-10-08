using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;

public enum CardiologyPacsStatus
{
    /// <summary>The PACS holds the file's object.</summary>
    Stored = 0,

    /// <summary>The PACS answered and refused it; the message says why.</summary>
    Refused,

    /// <summary>It did not reach the PACS (unreachable, timed out, a server error); sending again may work.</summary>
    Failed,

    /// <summary>Not a DICOM object a PACS can store, so it was not sent.</summary>
    NotDicom,
}

/// <summary>
/// A cardiology study's DICOM device file sent to the PACS (INT-PACS, STOW-RS), and what the PACS did with it: one row per
/// file, updated each time it is sent. The object goes under the patient's UHID and the study's UID, with the
/// machine's values kept in the object itself. An outcome holds only for the PACS it was sent to
/// (<see cref="Destination"/>): once another PACS is set up, the file counts as not sent there.
/// </summary>
public sealed class CardiologyPacsObject : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }

    public Guid CardiologyStudyId { get; set; }

    /// <summary>The device file in the encrypted platform file store.</summary>
    public Guid FileId { get; set; }

    /// <summary>The object's SOP instance UID, once HC360 could read it.</summary>
    public string? SopInstanceUid { get; set; }

    public CardiologyPacsStatus Status { get; set; }

    /// <summary>The PACS it was sent to: provider and STOW-RS address (<see cref="IPacsClient.StoreDestinationAsync"/>).</summary>
    public string Destination { get; set; } = string.Empty;

    /// <summary>Why it was not stored, or the PACS's warning about one it stored.</summary>
    public string? Message { get; set; }

    /// <summary>When it was last sent (<see cref="AuditableEntity.UpdatedBy"/> says by whom).</summary>
    public DateTimeOffset SentAt { get; set; }
}
