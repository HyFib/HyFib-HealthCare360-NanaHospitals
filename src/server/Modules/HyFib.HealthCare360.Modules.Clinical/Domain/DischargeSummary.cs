using System.Text.Json;

namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum DischargeSummaryStatus
{
    Draft = 0,
    Signed,
    Amended,
}

/// <summary>Editable content of a discharge summary.</summary>
public sealed record DischargeSummaryContent(
    string? Diagnoses,
    IReadOnlyList<string> DiagnosisCodes,
    string? PresentingComplaints,
    string? CourseInHospital,
    string? Procedures,
    string? InvestigationsSummary,
    string? ConditionAtDischarge,
    string? Medications,
    string? Advice,
    DateOnly? FollowUpDate,
    string? FollowUpInstructions);

/// <summary>
/// The discharge summary of one admission (keyed by <see cref="AdmissionId"/>; the admission itself belongs to the
/// Inpatient module). Signed summaries are shared with the patient on the portal, and with other providers through
/// the FHIR API and as ABDM health records that staff download (Interop module, with the patient's record-sharing
/// consent), so later changes are amendments with a reason and the previous text kept.
/// </summary>
public sealed class DischargeSummary : AuditableEntity, IFacilityScoped
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }

    /// <summary>Treating consultant.</summary>
    public Guid? PractitionerId { get; set; }

    public DateTimeOffset? AdmittedAt { get; set; }
    public DateTimeOffset? DischargedAt { get; set; }
    public string? Diagnoses { get; private set; }
    public List<string> DiagnosisCodes { get; private set; } = [];
    public string? PresentingComplaints { get; private set; }
    public string? CourseInHospital { get; private set; }
    public string? Procedures { get; private set; }
    public string? InvestigationsSummary { get; private set; }
    public string? ConditionAtDischarge { get; private set; }
    public string? Medications { get; private set; }
    public string? Advice { get; private set; }
    public DateOnly? FollowUpDate { get; private set; }
    public string? FollowUpInstructions { get; private set; }
    public DischargeSummaryStatus Status { get; private set; }
    public DateTimeOffset? SignedAt { get; private set; }
    public Guid? SignedByUserId { get; private set; }
    public Guid? SignedByPractitionerId { get; private set; }
    public string? AmendmentReason { get; private set; }
    public DateTimeOffset? AmendedAt { get; private set; }
    public List<DischargeSummaryAmendment> Amendments { get; set; } = [];

    public bool IsSigned => Status != DischargeSummaryStatus.Draft;

    public DischargeSummaryContent Content => new(
        Diagnoses, DiagnosisCodes, PresentingComplaints, CourseInHospital, Procedures, InvestigationsSummary, ConditionAtDischarge,
        Medications, Advice, FollowUpDate, FollowUpInstructions);

    public Result Update(DischargeSummaryContent content)
    {
        if (IsSigned)
        {
            return Error.Rule("discharge_summary.signed", "This discharge summary is signed. Record an amendment with a reason.");
        }

        Apply(content);
        return Result.Success();
    }

    /// <summary>The minimum a receiving doctor needs: what the patient had, what was done and how they left.</summary>
    public Result Sign(Guid? userId, Guid? practitionerId, DateTimeOffset now)
    {
        if (IsSigned)
        {
            return Error.Rule("discharge_summary.already_signed", "This discharge summary is already signed.");
        }

        if (string.IsNullOrWhiteSpace(Diagnoses) || string.IsNullOrWhiteSpace(CourseInHospital) || string.IsNullOrWhiteSpace(ConditionAtDischarge))
        {
            return Error.Rule("discharge_summary.incomplete", "Diagnoses, course in hospital and condition at discharge are required before signing.");
        }

        Status = DischargeSummaryStatus.Signed;
        SignedAt = now;
        SignedByUserId = userId;
        SignedByPractitionerId = practitionerId ?? PractitionerId;
        Raise(new DischargeSummarySigned(Id, AdmissionId, PatientId, FacilityId, now));
        return Result.Success();
    }

    public Result Amend(DischargeSummaryContent content, string reason, Guid? userId, DateTimeOffset now)
    {
        if (!IsSigned)
        {
            return Error.Rule("discharge_summary.not_signed", "This summary is still a draft; edit it directly.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Give a reason for the amendment.");
        }

        Amendments.Add(new DischargeSummaryAmendment
        {
            DischargeSummaryId = Id,
            AmendedAt = now,
            AmendedByUserId = userId,
            Reason = reason.Trim(),
            PreviousContent = JsonSerializer.Serialize(Content, JsonOptions),
        });
        Apply(content);
        Status = DischargeSummaryStatus.Amended;
        AmendmentReason = reason.Trim();
        AmendedAt = now;
        return Result.Success();
    }

    private void Apply(DischargeSummaryContent c)
    {
        Diagnoses = Clean(c.Diagnoses);
        DiagnosisCodes = c.DiagnosisCodes.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code.Trim().ToUpperInvariant()).Distinct().ToList();
        PresentingComplaints = Clean(c.PresentingComplaints);
        CourseInHospital = Clean(c.CourseInHospital);
        Procedures = Clean(c.Procedures);
        InvestigationsSummary = Clean(c.InvestigationsSummary);
        ConditionAtDischarge = Clean(c.ConditionAtDischarge);
        Medications = Clean(c.Medications);
        Advice = Clean(c.Advice);
        FollowUpDate = c.FollowUpDate;
        FollowUpInstructions = Clean(c.FollowUpInstructions);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class DischargeSummaryAmendment : Entity
{
    public Guid DischargeSummaryId { get; set; }
    public DateTimeOffset AmendedAt { get; set; }
    public Guid? AmendedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string PreviousContent { get; set; } = "{}";
}

/// <summary>Raised when a discharge summary is first signed (portal notification, MRD chart completion).</summary>
public sealed record DischargeSummarySigned(Guid DischargeSummaryId, Guid AdmissionId, Guid PatientId, Guid FacilityId, DateTimeOffset SignedAt) : DomainEvent;
