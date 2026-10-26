using HyFib.HealthCare360.Modules.Clinical.Domain.Stewardship;

namespace HyFib.HealthCare360.Modules.Clinical.Features.Stewardship;

/// <summary>Which approvals to list: waiting for a decision, due for review, or every one of the last month.</summary>
public enum ApprovalQueue
{
    Pending = 0,
    ReviewDue,
    All,
}

public sealed record AntimicrobialApprovalDto(
    Guid Id,
    Guid PatientId,
    string PatientName,
    string Uhid,
    string PrescriptionNumber,
    bool Inpatient,
    string DrugName,
    string Antimicrobial,
    AwareGroup Group,
    string? Dose,
    string? Route,
    int DurationDays,
    string PrescribedByName,
    DateTimeOffset PrescribedAt,
    InfectionSite? Site,
    string? Indication,
    bool? Empirical,
    bool? CultureSent,
    ApprovalStatus Status,
    DateTimeOffset ApprovalDueBy,
    bool ApprovalOverdue,
    string? DecidedByName,
    DateTimeOffset? DecidedAt,
    int? ApprovedDays,
    string? DecisionNotes,
    string? Organism,
    string? Susceptibility,
    DateTimeOffset? CultureReleasedAt,
    bool ReviewDue,
    StewardshipDecision? ReviewDecision,
    string? ReviewedByName,
    DateTimeOffset? ReviewedAt,
    string? ReviewNotes,
    bool IsMine,
    uint Version);

public sealed record JustifyRequest(InfectionSite Site, string Indication, bool Empirical, bool CultureSent, uint Version);

public sealed record DecideRequest(bool Approve, int? Days, string? Notes, uint Version);

public sealed record ReviewRequest(StewardshipDecision Decision, string? Notes, uint Version);

/// <summary>Restricted antibiotics prescribed in the period, by AWaRe group.</summary>
public sealed record StewardshipGroupCount(AwareGroup Group, int Prescribed, int Approved, int Rejected, int Pending);

/// <summary>
/// The stewardship programme's measures for a period: what was asked for, how much was approved, how fast the team
/// decided, and what the reviews did with the courses.
/// </summary>
/// <param name="MedianHoursToDecision">Prescribing to decision, for the decided ones; null when none was decided.</param>
/// <param name="DecidedLate">Decided after the 24 hours, or still waiting past them.</param>
/// <param name="CultureSentPercent">Of the justified ones, how many had a culture sent before the antibiotic.</param>
public sealed record StewardshipReport(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<StewardshipGroupCount> ByGroup,
    double? MedianHoursToDecision,
    int DecidedLate,
    int? CultureSentPercent,
    int Reviewed,
    IReadOnlyDictionary<StewardshipDecision, int> ReviewDecisions);

public sealed class JustifyRequestValidator : AbstractValidator<JustifyRequest>
{
    public JustifyRequestValidator()
    {
        RuleFor(x => x.Site).IsInEnum();
        RuleFor(x => x.Indication).NotEmpty().SafeText(500);
    }
}

public sealed class DecideRequestValidator : AbstractValidator<DecideRequest>
{
    public DecideRequestValidator()
    {
        RuleFor(x => x.Days).InclusiveBetween(1, 42);
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class ReviewRequestValidator : AbstractValidator<ReviewRequest>
{
    public ReviewRequestValidator()
    {
        RuleFor(x => x.Decision).IsInEnum();
        RuleFor(x => x.Notes).SafeText(500);
    }
}
