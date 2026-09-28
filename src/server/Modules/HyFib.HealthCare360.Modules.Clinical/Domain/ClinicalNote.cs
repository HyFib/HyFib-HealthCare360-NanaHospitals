namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum ClinicalNoteType
{
    ProgressNote = 0,
    Procedure,
    Referral,
    Counselling,
    Other,
}

/// <summary>
/// A free-text clinical note (progress, procedure, referral letter, counselling). Once signed it is immutable;
/// corrections are written as an addendum note that points at the original (<see cref="AmendsNoteId"/>).
/// </summary>
public sealed class ClinicalNote : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? AdmissionId { get; set; }
    public ClinicalNoteType Type { get; private set; }
    public string? Title { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public Guid? AuthorUserId { get; set; }
    public Guid? AuthorPractitionerId { get; set; }
    public Guid? AmendsNoteId { get; set; }
    public bool IsSigned { get; private set; }
    public DateTimeOffset? SignedAt { get; private set; }
    public Guid? SignedByUserId { get; private set; }

    public static ClinicalNote Draft(Guid patientId, ClinicalNoteType type, string? title, string body) => new()
    {
        PatientId = patientId,
        Type = type,
        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
        Body = body.Trim(),
    };

    public Result Edit(ClinicalNoteType type, string? title, string body)
    {
        if (IsSigned)
        {
            return Error.Rule("note.signed", "This note is signed. Add an addendum instead of editing it.");
        }

        Type = type;
        Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Body = body.Trim();
        return Result.Success();
    }

    public Result Sign(Guid? userId, DateTimeOffset now)
    {
        if (IsSigned)
        {
            return Error.Rule("note.already_signed", "This note is already signed.");
        }

        if (string.IsNullOrWhiteSpace(Body))
        {
            return Error.Rule("note.empty", "An empty note cannot be signed.");
        }

        IsSigned = true;
        SignedAt = now;
        SignedByUserId = userId;
        return Result.Success();
    }
}
