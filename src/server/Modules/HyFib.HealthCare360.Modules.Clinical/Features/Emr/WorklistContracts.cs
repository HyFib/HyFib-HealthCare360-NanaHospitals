using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Emr;

/// <summary>A queue token on the doctor's worklist, with banner flags so risky patients stand out in the list.</summary>
public sealed record WorklistQueueEntry(
    Guid TokenId,
    string TokenLabel,
    TokenStatus TokenStatus,
    TokenPriority Priority,
    Guid VisitId,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string? ChiefComplaint,
    bool IsFollowUp,
    DateTimeOffset IssuedAt,
    int WaitingMinutes,
    Guid? EncounterId,
    EncounterStatus? EncounterStatus,
    bool HasActiveAllergies,
    bool HasCriticalAlerts);

/// <summary>
/// A booked consultation on the doctor's day (cancelled and rescheduled bookings are left out). Once the patient has
/// checked in, it carries the visit, the queue token and the encounter, as the queue does.
/// </summary>
public sealed record WorklistAppointment(
    Guid Id,
    string Number,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    AppointmentStatus Status,
    AppointmentVisitType VisitType,
    bool IsTelehealth,
    string? Reason,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    Guid PractitionerId,
    string? PractitionerName,
    Guid? VisitId,
    DateTimeOffset? CheckedInAt,
    string? TokenLabel,
    Guid? EncounterId,
    EncounterStatus? EncounterStatus,
    bool HasActiveAllergies,
    bool HasCriticalAlerts);

public sealed record WorklistEncounter(Guid Id, Guid PatientId, string Uhid, string PatientName, EncounterType Type, DateTimeOffset StartedAt, string? ChiefComplaint);

public sealed record WorklistNote(Guid Id, Guid PatientId, string Uhid, string PatientName, ClinicalNoteType Type, string? Title, DateTimeOffset CreatedAt);

public sealed record WorklistPrescription(Guid Id, string PrescriptionNumber, Guid PatientId, string Uhid, string PatientName, DateTimeOffset PrescribedAt, int ItemCount);

public sealed record WorklistResult(
    Guid Id,
    Guid ReportId,
    DiagnosticKind Kind,
    string Title,
    bool HasCriticalFindings,
    DateTimeOffset ReleasedAt,
    Guid PatientId,
    string Uhid,
    string PatientName);

public sealed record WorklistDeficiency(
    Guid Id,
    Guid MedicalRecordId,
    DeficiencyType Type,
    string Description,
    DateOnly? DueDate,
    Guid PatientId,
    string Uhid,
    string PatientName);

/// <summary>
/// The physician desktop home (EMR-01): today's queue for the doctor's department, the doctor's appointments,
/// encounters and notes still to sign, draft prescriptions, released results to review (critical first) and chart
/// deficiencies assigned by MRD. The ward round comes from the in-patient module (<c>GET /api/v1/inpatient/rounds</c>).
/// </summary>
public sealed record EmrWorklist(
    DateOnly Date,
    Guid? PractitionerId,
    Guid? DepartmentId,
    IReadOnlyList<WorklistQueueEntry> Queue,
    IReadOnlyList<WorklistAppointment> Appointments,
    IReadOnlyList<WorklistEncounter> OpenEncounters,
    IReadOnlyList<WorklistNote> UnsignedNotes,
    IReadOnlyList<WorklistPrescription> DraftPrescriptions,
    IReadOnlyList<WorklistResult> ResultsToReview,
    IReadOnlyList<WorklistDeficiency> ChartDeficiencies);
