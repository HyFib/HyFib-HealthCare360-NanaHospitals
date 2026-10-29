namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

public enum DailyChargeKind
{
    Bed = 0,
    Nursing,
}

/// <summary>
/// Local ledger of per-day charges Billing has accepted, so the hourly job posts each (admission, day, kind) once instead
/// of re-sending the whole stay every hour. Billing's own idempotency on SourceModule + SourceReference stays the safety
/// net if a post succeeds but this row is lost. Cancelling an admission reverses exactly the charges listed here.
/// </summary>
[NotAudited]
public sealed class BedDayCharge : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public DateOnly ChargeDate { get; set; }
    public DailyChargeKind Kind { get; set; }
    public Guid ServiceItemId { get; set; }
    public Guid ChargeId { get; set; }
    public string SourceReference { get; set; } = string.Empty;
    public DateTimeOffset PostedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>
/// A consultant's ward-round visit, billed per visit (VISIT-DR, or VISIT-DR-ICU in critical-care wards). The charge is
/// posted with the visit id as its source reference; when Billing is unreachable the error is kept and the hourly job
/// retries, so a visit is never silently left unbilled.
/// </summary>
public sealed class ConsultantVisit : AuditableEntity, IFacilityScoped
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);

    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid PractitionerId { get; set; }
    public DateTimeOffset VisitedAt { get; set; }
    public Guid? WardId { get; set; }
    public bool IsCriticalCare { get; set; }
    public Guid? ServiceItemId { get; set; }
    public string? Notes { get; set; }
    public Guid? ChargeId { get; set; }
    public string? ChargeError { get; set; }

    public string ServiceCode => ServiceCodeFor(IsCriticalCare);

    /// <summary>The service a visit is charged as: VISIT-DR-ICU in a critical-care ward, VISIT-DR elsewhere.</summary>
    public static string ServiceCodeFor(bool isCriticalCare) => isCriticalCare ? "VISIT-DR-ICU" : "VISIT-DR";
}
