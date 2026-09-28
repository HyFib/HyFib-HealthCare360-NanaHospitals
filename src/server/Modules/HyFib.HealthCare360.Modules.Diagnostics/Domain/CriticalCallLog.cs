namespace HyFib.HealthCare360.Modules.Diagnostics.Domain;

/// <summary>Details of a critical-value call as entered by lab, radiology or cardiology staff.</summary>
public sealed record CriticalCallDetails(
    string InformedPersonName,
    string? InformedPersonRole,
    Guid? InformedPractitionerId,
    CommunicationMethod Method,
    DateTimeOffset InformedAt,
    bool ReadBackConfirmed,
    string? Notes);

/// <summary>What the call was about: one critical lab result or one critical imaging/cardiology finding.</summary>
public sealed record CriticalFindingRef(
    DiagnosticKind Kind,
    Guid FacilityId,
    Guid PatientId,
    string Finding,
    Guid? LabResultId = null,
    Guid? ImagingStudyId = null,
    Guid? CardiologyStudyId = null);

/// <summary>
/// Critical call register (LIS-05, IMG-03; NABH AAC.7): who was told, when, how, and that the value was read back.
/// Verification of a critical result or finding is refused until a call is documented, so a critical value can never
/// reach the report silently.
/// </summary>
public sealed class CriticalCallLog : AuditableEntity, IFacilityScoped
{
    /// <summary>Clock skew tolerated between a workstation and the server when the call time is typed in.</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public Guid FacilityId { get; set; }
    public DiagnosticKind Kind { get; set; }
    public Guid PatientId { get; set; }
    public Guid? LabResultId { get; set; }
    public Guid? ImagingStudyId { get; set; }
    public Guid? CardiologyStudyId { get; set; }

    /// <summary>Human-readable finding, for example "Potassium 6.9 mmol/L (critical high)".</summary>
    public string Finding { get; set; } = string.Empty;

    public string InformedPersonName { get; set; } = string.Empty;
    public string? InformedPersonRole { get; set; }
    public Guid? InformedPractitionerId { get; set; }
    public CommunicationMethod Method { get; set; }
    public DateTimeOffset InformedAt { get; set; }

    /// <summary>The receiver repeated the value back (read-back is mandatory; records without it are refused).</summary>
    public bool ReadBackConfirmed { get; set; }

    public Guid? CalledByUserId { get; set; }
    public string? Notes { get; set; }

    public static Result<CriticalCallLog> Record(CriticalFindingRef finding, CriticalCallDetails call, Guid? calledBy, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(call.InformedPersonName))
        {
            return Error.Field("criticalCall.informedPersonName", "Enter the name of the person who was informed.");
        }

        if (!call.ReadBackConfirmed)
        {
            return Error.Rule("critical.read_back_required", "Ask the person informed to read the value back, then confirm the read-back.");
        }

        if (call.InformedAt > now + FutureTolerance)
        {
            return Error.Field("criticalCall.informedAt", "The call time cannot be in the future.");
        }

        return new CriticalCallLog
        {
            FacilityId = finding.FacilityId,
            Kind = finding.Kind,
            PatientId = finding.PatientId,
            LabResultId = finding.LabResultId,
            ImagingStudyId = finding.ImagingStudyId,
            CardiologyStudyId = finding.CardiologyStudyId,
            Finding = finding.Finding.Length > 300 ? finding.Finding[..300] : finding.Finding,
            InformedPersonName = call.InformedPersonName.Trim(),
            InformedPersonRole = call.InformedPersonRole?.Trim(),
            InformedPractitionerId = call.InformedPractitionerId,
            Method = call.Method,
            InformedAt = call.InformedAt,
            ReadBackConfirmed = true,
            CalledByUserId = calledBy,
            Notes = call.Notes?.Trim(),
        };
    }
}
