namespace HyFib.HealthCare360.Modules.Clinical.Domain;

public enum CodingStatus
{
    Pending = 0,
    Coded,
    Verified,
}

public enum CodingEntryKind
{
    Diagnosis = 0,
    Procedure,
}

/// <summary>A coded diagnosis or procedure line (input to <see cref="EncounterCoding.Code"/>).</summary>
public sealed record CodingLine(CodingEntryKind Kind, string Code, string Title, bool IsPrimary);

/// <summary>
/// Clinical coding of an admission or encounter by the MRD coder (MRD-02, MB-03): exactly one primary diagnosis,
/// secondary diagnoses and procedures. A second person verifies it (four-eyes rule) before claims and the
/// morbidity index use it.
/// </summary>
public sealed class EncounterCoding : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? EncounterId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? VisitId { get; set; }

    /// <summary>Date the coded episode ended (discharge or encounter date) — the morbidity index period key.</summary>
    public DateOnly? EpisodeDate { get; set; }

    public CodingStatus Status { get; private set; }
    public Guid? CoderUserId { get; private set; }
    public DateTimeOffset? CodedAt { get; private set; }
    public Guid? VerifiedByUserId { get; private set; }
    public DateTimeOffset? VerifiedAt { get; private set; }
    public string? Notes { get; set; }
    public List<CodingEntry> Entries { get; set; } = [];

    public Result Code(IReadOnlyList<CodingLine> lines, Guid? coderUserId, DateTimeOffset now)
    {
        var diagnoses = lines.Where(l => l.Kind == CodingEntryKind.Diagnosis).ToList();
        if (diagnoses.Count(l => l.IsPrimary) != 1)
        {
            return Error.Field("entries", "Mark exactly one diagnosis as the primary (principal) diagnosis.");
        }

        if (lines.Any(l => l.Kind == CodingEntryKind.Procedure && l.IsPrimary))
        {
            return Error.Field("entries", "Only a diagnosis can be primary.");
        }

        var duplicate = lines.GroupBy(l => (l.Kind, Code: l.Code.Trim().ToUpperInvariant())).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Error.Field("entries", $"Code {duplicate.Key.Code} is listed more than once.");
        }

        Entries.Clear();
        var sequence = 0;
        foreach (var line in lines.OrderByDescending(l => l.IsPrimary).ThenBy(l => l.Kind))
        {
            Entries.Add(new CodingEntry
            {
                CodingId = Id,
                Kind = line.Kind,
                Code = line.Code.Trim().ToUpperInvariant(),
                Title = line.Title.Trim(),
                IsPrimary = line.IsPrimary,
                Sequence = ++sequence,
            });
        }

        // Re-coding after verification invalidates the verification.
        Status = CodingStatus.Coded;
        CoderUserId = coderUserId;
        CodedAt = now;
        VerifiedByUserId = null;
        VerifiedAt = null;
        return Result.Success();
    }

    public Result Verify(Guid? userId, DateTimeOffset now)
    {
        if (Status != CodingStatus.Coded)
        {
            return Error.Rule("coding.not_coded", Status == CodingStatus.Verified ? "This coding is already verified." : "Code the record before verifying it.");
        }

        if (userId is not null && userId == CoderUserId)
        {
            return Error.Rule("coding.self_verification", "Coding must be verified by a different coder.");
        }

        Status = CodingStatus.Verified;
        VerifiedByUserId = userId;
        VerifiedAt = now;
        return Result.Success();
    }
}

public sealed class CodingEntry : Entity
{
    public Guid CodingId { get; set; }
    public CodingEntryKind Kind { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public int Sequence { get; set; }
}
