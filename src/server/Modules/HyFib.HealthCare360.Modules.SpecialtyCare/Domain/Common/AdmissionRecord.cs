namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

/// <summary>
/// Local projection of an in-patient admission, maintained from <c>PatientAdmitted</c> / <c>PatientDischarged</c>.
/// SpecialtyCare may not reference the Inpatient module, yet OT implants, blood and IP dialysis must be charged to
/// the running IP bill (OT-06); this projection lets services find the patient's current admission by id only.
/// </summary>
public sealed class AdmissionRecord : Entity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid VisitId { get; set; }
    public string AdmissionNumber { get; set; } = string.Empty;
    public DateTimeOffset AdmittedAt { get; set; }
    public DateTimeOffset? DischargedAt { get; set; }

    public bool IsActive => DischargedAt is null;
}
