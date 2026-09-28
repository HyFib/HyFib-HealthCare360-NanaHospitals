using System.Text.Json;

namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum EncounterType
{
    OutPatient = 0,
    InPatient,
    Emergency,
    Telehealth,
    HealthCheckup,
}

/// <summary>
/// Encounter lifecycle: notes are editable while <see cref="InProgress"/>; signing locks them; later corrections
/// are recorded as attested amendments (with a reason and the previous text) and leave the encounter <see cref="Amended"/>.
/// </summary>
public enum EncounterStatus
{
    InProgress = 0,
    Signed,
    Amended,
}

/// <summary>The editable clinical content of an encounter (SOAP, follow-up and referral).</summary>
public sealed record EncounterContent(
    string? ChiefComplaint,
    string? HistoryOfPresentIllness,
    string? Examination,
    string? Assessment,
    string? Plan,
    DateOnly? FollowUpDate,
    string? FollowUpAdvice,
    Guid? ReferralDepartmentId,
    Guid? ReferralPractitionerId,
    string? ReferralExternal,
    string? ReferralReason)
{
    public bool HasReferral => ReferralDepartmentId is not null || ReferralPractitionerId is not null || !string.IsNullOrWhiteSpace(ReferralExternal);
}

/// <summary>
/// A clinician's documented contact with a patient (EMR-02..07): SOAP note, follow-up and referral. It links to the
/// front-office visit (OP/ER/telehealth) or to the admission (IP) so orders, prescriptions and charges share one anchor.
/// </summary>
public sealed class Encounter : AuditableEntity, IFacilityScoped
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid PractitionerId { get; set; }
    public Guid? DepartmentId { get; set; }
    public EncounterType Type { get; set; }
    public EncounterStatus Status { get; private set; }
    public DateTimeOffset StartedAt { get; set; }

    public string? ChiefComplaint { get; private set; }
    public string? HistoryOfPresentIllness { get; private set; }
    public string? Examination { get; private set; }
    public string? Assessment { get; private set; }
    public string? Plan { get; private set; }
    public DateOnly? FollowUpDate { get; private set; }
    public string? FollowUpAdvice { get; private set; }
    public Guid? ReferralDepartmentId { get; private set; }
    public Guid? ReferralPractitionerId { get; private set; }
    public string? ReferralExternal { get; private set; }
    public string? ReferralReason { get; private set; }

    /// <summary>Template the note was started from (speciality defaults).</summary>
    public Guid? TemplateId { get; set; }

    public DateTimeOffset? SignedAt { get; private set; }
    public Guid? SignedByUserId { get; private set; }
    public Guid? SignedByPractitionerId { get; private set; }

    /// <summary>Reason of the latest amendment; the full history is in <see cref="Amendments"/>.</summary>
    public string? AmendmentReason { get; private set; }

    public DateTimeOffset? AmendedAt { get; private set; }

    public List<EncounterAmendment> Amendments { get; set; } = [];

    public bool IsLocked => Status != EncounterStatus.InProgress;

    public EncounterContent Content => new(
        ChiefComplaint, HistoryOfPresentIllness, Examination, Assessment, Plan, FollowUpDate, FollowUpAdvice,
        ReferralDepartmentId, ReferralPractitionerId, ReferralExternal, ReferralReason);

    /// <summary>Edits the note while it is still in progress. Signed notes change only through <see cref="Amend"/>.</summary>
    public Result Update(EncounterContent content)
    {
        if (IsLocked)
        {
            return Error.Rule("encounter.signed", "This encounter is signed. Record an amendment with a reason to change it.");
        }

        Apply(content);
        return Result.Success();
    }

    /// <summary>
    /// Attests the note. An empty note cannot be signed: a signed encounter is a medico-legal record and must at
    /// least say why the patient came or what the clinician concluded.
    /// </summary>
    public Result Sign(Guid? userId, Guid? practitionerId, DateTimeOffset now)
    {
        if (IsLocked)
        {
            return Error.Rule("encounter.already_signed", "This encounter is already signed.");
        }

        if (string.IsNullOrWhiteSpace(ChiefComplaint) && string.IsNullOrWhiteSpace(Assessment))
        {
            return Error.Rule("encounter.empty", "Record at least the chief complaint or the assessment before signing.");
        }

        Status = EncounterStatus.Signed;
        SignedAt = now;
        SignedByUserId = userId;
        SignedByPractitionerId = practitionerId ?? PractitionerId;
        Raise(new EncounterSigned(Id, PatientId, FacilityId, VisitId, AdmissionId, PractitionerId, Type, FollowUpDate,
            ReferralDepartmentId, ReferralPractitionerId, now));
        return Result.Success();
    }

    /// <summary>
    /// Corrects a signed note. The previous content is kept verbatim in the amendment history so the original
    /// record can always be reproduced (NABH / medico-legal requirement); the amending clinician attests the change.
    /// </summary>
    public Result Amend(EncounterContent content, string reason, Guid? userId, DateTimeOffset now)
    {
        if (!IsLocked)
        {
            return Error.Rule("encounter.not_signed", "This encounter is not signed yet; edit it directly.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Field("reason", "Give a reason for the amendment.");
        }

        Amendments.Add(new EncounterAmendment
        {
            EncounterId = Id,
            AmendedAt = now,
            AmendedByUserId = userId,
            Reason = reason.Trim(),
            PreviousContent = JsonSerializer.Serialize(Content, JsonOptions),
        });

        Apply(content);
        Status = EncounterStatus.Amended;
        AmendmentReason = reason.Trim();
        AmendedAt = now;
        Raise(new EncounterAmended(Id, PatientId, reason.Trim(), now));
        return Result.Success();
    }

    private void Apply(EncounterContent c)
    {
        ChiefComplaint = Clean(c.ChiefComplaint);
        HistoryOfPresentIllness = Clean(c.HistoryOfPresentIllness);
        Examination = Clean(c.Examination);
        Assessment = Clean(c.Assessment);
        Plan = Clean(c.Plan);
        FollowUpDate = c.FollowUpDate;
        FollowUpAdvice = Clean(c.FollowUpAdvice);
        ReferralDepartmentId = c.ReferralDepartmentId;
        ReferralPractitionerId = c.ReferralPractitionerId;
        ReferralExternal = Clean(c.ReferralExternal);
        ReferralReason = c.HasReferral ? Clean(c.ReferralReason) : null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Append-only amendment history of a signed encounter (previous content stored as JSON).</summary>
public sealed class EncounterAmendment : Entity
{
    public Guid EncounterId { get; set; }
    public DateTimeOffset AmendedAt { get; set; }
    public Guid? AmendedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string PreviousContent { get; set; } = "{}";
}

/// <summary>Raised when an encounter is first signed (follow-up reminders, referral notification, doctor-fee triggers).</summary>
public sealed record EncounterSigned(
    Guid EncounterId,
    Guid PatientId,
    Guid FacilityId,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid PractitionerId,
    EncounterType Type,
    DateOnly? FollowUpDate,
    Guid? ReferralDepartmentId,
    Guid? ReferralPractitionerId,
    DateTimeOffset SignedAt) : DomainEvent;

public sealed record EncounterAmended(Guid EncounterId, Guid PatientId, string Reason, DateTimeOffset AmendedAt) : DomainEvent;
