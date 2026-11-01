using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;

namespace HyFib.HealthCare360.Modules.Analytics.Features.Journeys;

public enum JourneyKind
{
    Outpatient = 0,
    Inpatient,
    Surgery,
    Lab,
}

/// <summary>
/// One journey as its pipeline shows it: the visit, stay, operation or lab accession it follows, its stages in order and
/// the stage the patient is in.
/// </summary>
/// <param name="Id">The visit's, admission's, booking's or lab order's id.</param>
/// <param name="Number">Its number as staff know it: the visit, admission, booking or accession number.</param>
/// <param name="Title">What it is: the department and doctor, the ward and bed, the operation, the tests.</param>
/// <param name="Current">The stage the patient is in, with the minutes spent in it; null once the journey is finished or cancelled.</param>
public sealed record JourneyDto(
    JourneyKind Kind,
    Guid Id,
    string Number,
    string Title,
    JourneyStatus Status,
    DateTimeOffset StartedAt,
    IReadOnlyList<JourneyStage> Stages,
    JourneyStage? Current);

/// <summary>
/// A patient's journeys (GET /api/v1/patients/{patientId}/journey), as of <paramref name="AsOf"/>: the current or latest
/// out-patient visit, the current stay (until the bed the patient left is clean), and the operations and lab orders of
/// those, or still open.
/// </summary>
/// <param name="SurgeriesWithheld">
/// The record is in a sensitive category the user may not open: operations are part of the clinical record it closes,
/// so they are left out (SECURITY.md, sensitive categories).
/// </param>
public sealed record PatientJourneys(
    Guid PatientId,
    DateTimeOffset AsOf,
    JourneyDto? Outpatient,
    JourneyDto? Inpatient,
    IReadOnlyList<JourneyDto> Surgeries,
    IReadOnlyList<JourneyDto> LabOrders,
    bool SurgeriesWithheld);

/// <summary>What the board of patients in the hospital shows: out-patients or in-patients, of a department, of a ward.</summary>
public sealed record InHospitalFilter(JourneyKind? Kind, Guid? DepartmentId, Guid? WardId);

/// <summary>
/// A patient in the hospital now (GET /api/v1/journeys/in-hospital): an out-patient visit of today that is still open, or
/// a stay, with its pipeline. A restricted (VIP) patient the user may not open is named by initials (R-3).
/// </summary>
/// <param name="Place">Where to find the patient: the token for an out-patient, the ward and bed for an in-patient.</param>
/// <param name="ArrivedAt">The check-in or the admission.</param>
public sealed record InHospitalPatient(
    Guid PatientId,
    string Uhid,
    string PatientName,
    bool IsRestricted,
    Gender Gender,
    string? Age,
    JourneyKind Kind,
    Guid? DepartmentId,
    string? DepartmentName,
    Guid? WardId,
    string? Place,
    DateTimeOffset ArrivedAt,
    JourneyDto Journey);
