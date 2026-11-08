using HyFib.HealthCare360.Modules.Clinical.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Domain;

/// <summary>
/// What a stay that ended other than routinely says about how it ended (maker-checker, slice 8), one per stay:
/// <list type="bullet">
/// <item>LAMA or DAMA: who signed the form (a relative named, with their relationship) or that nobody would, the doctor who
/// explained the risks and a witness, each from their own sign-in or confirmation, and the scanned form.</item>
/// <item>Absconded: when the patient was last seen, who searched, whether the police and the relatives were told, and the
/// second person (the nurse in charge) who approved closing the stay.</item>
/// <item>Expired: the death certificate the stay was closed with, or, for a medico-legal death whose certificate waits for
/// the post-mortem, that it does, and the second doctor who approved closing the stay without it.</item>
/// </list>
/// Absconded and Expired skip billing clearance (a body is not held for dues; an absconded patient is gone): what was
/// still due is recorded.
/// </summary>
public sealed class DischargeDeclaration : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid AdmissionId { get; set; }
    public DischargeType Type { get; set; }

    public AgainstAdviceSignatory? SignedBy { get; set; }
    public string? SignatoryName { get; set; }
    public string? SignatoryRelationship { get; set; }
    public Guid? RisksExplainedByUserId { get; set; }
    public string? RisksExplainedByName { get; set; }
    public Guid? WitnessUserId { get; set; }
    public string? WitnessName { get; set; }

    /// <summary>The scanned form signed by the patient or relative, a file of the document store.</summary>
    public Guid? SignedFormFileId { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
    public string? SearchedBy { get; set; }
    public bool PoliceInformed { get; set; }
    public string? PoliceStation { get; set; }
    public string? PoliceReference { get; set; }
    public bool RelativesInformed { get; set; }
    public string? RelativeInformed { get; set; }

    public Guid? DeathCertificateId { get; set; }
    public string? DeathCertificateNumber { get; set; }

    /// <summary>A medico-legal death closed before its certificate, which waits for the post-mortem.</summary>
    public bool CertificatePendingPostMortem { get; set; }

    /// <summary>What was still due on the stay when it closed without billing clearance (Absconded, Expired); null otherwise.</summary>
    public decimal? DuesOutstanding { get; set; }

    public Guid? RecordedByUserId { get; set; }
    public string RecordedByName { get; set; } = string.Empty;

    /// <summary>The second person's approval, for the types that wait for one (Absconded, Expired pending post-mortem).</summary>
    public Guid? ApprovalId { get; set; }

    public Guid? ApprovedByUserId { get; set; }
    public string? ApprovedByName { get; set; }

    public void SetAgainstAdvice(AgainstAdviceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        (SignedBy, SignatoryName, SignatoryRelationship) = (record.SignedBy, record.SignatoryName, record.Relationship);
        (RisksExplainedByUserId, RisksExplainedByName) = (record.DoctorUserId, record.DoctorName);
        (WitnessUserId, WitnessName, SignedFormFileId) = (record.WitnessUserId, record.WitnessName, record.SignedFormFileId);
    }
}

/// <summary>An absconded patient as the ward reports it: when last seen, who searched, and whether the police and relatives were told.</summary>
public sealed record AbscondedReport(
    DateTimeOffset LastSeenAt,
    string SearchedBy,
    bool PoliceInformed,
    string? PoliceStation,
    string? PoliceReference,
    bool RelativesInformed,
    string? RelativeInformed)
{
    /// <summary>
    /// What the report must say: who searched; and for a medico-legal stay, that the police were told, at which station and
    /// under what reference (the patient in a police case has gone). Null when it says enough.
    /// </summary>
    public Error? Check(bool medicoLegal, DateTimeOffset now)
    {
        if (LastSeenAt > now.AddMinutes(5))
        {
            return Error.Field("absconded.lastSeenAt", "The time the patient was last seen cannot be in the future.");
        }

        if (string.IsNullOrWhiteSpace(SearchedBy))
        {
            return Error.Field("absconded.searchedBy", "Say who searched for the patient.");
        }

        if (PoliceInformed && string.IsNullOrWhiteSpace(PoliceStation))
        {
            return Error.Field("absconded.policeStation", "Name the police station that was told.");
        }

        if (RelativesInformed && string.IsNullOrWhiteSpace(RelativeInformed))
        {
            return Error.Field("absconded.relativeInformed", "Name the relative who was told.");
        }

        return medicoLegal && (!PoliceInformed || string.IsNullOrWhiteSpace(PoliceReference))
            ? Error.Rule("discharge.police_intimation_required",
                "This is a medico-legal case: tell the police the patient has gone, and record the station and the reference (diary or GD entry) before the stay is closed.")
            : null;
    }

    public void Apply(DischargeDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        declaration.LastSeenAt = LastSeenAt;
        declaration.SearchedBy = SearchedBy.Trim();
        (declaration.PoliceInformed, declaration.PoliceStation, declaration.PoliceReference) = (PoliceInformed, Clean(PoliceStation), Clean(PoliceReference));
        (declaration.RelativesInformed, declaration.RelativeInformed) = (RelativesInformed, Clean(RelativeInformed));
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
