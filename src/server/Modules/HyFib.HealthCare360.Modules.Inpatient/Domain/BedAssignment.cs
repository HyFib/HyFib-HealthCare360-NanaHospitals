namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum BedAssignmentReason
{
    Admission = 0,
    Transfer,
}

/// <summary>
/// One continuous stay in one bed. The history drives transfer reports, the census and bed-day charging (the bed occupied
/// at each census point decides the bed class), so rows are only ever closed, never edited. The bed tariff and the
/// critical-care flag are snapshotted from the ward when the stay starts.
/// </summary>
public sealed class BedAssignment : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid BedId { get; set; }
    public Guid WardId { get; set; }
    public Guid? BedServiceItemId { get; set; }
    public bool IsCriticalCare { get; set; }
    public BedAssignmentReason Reason { get; set; }
    public string? TransferReason { get; set; }
    public DateTimeOffset FromAt { get; set; }
    public DateTimeOffset? ToAt { get; set; }

    public void Close(DateTimeOffset at) => ToAt ??= at < FromAt ? FromAt : at;
}
