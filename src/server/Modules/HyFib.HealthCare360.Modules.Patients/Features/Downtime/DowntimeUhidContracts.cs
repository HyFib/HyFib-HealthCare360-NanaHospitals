using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Features.Downtime;

/// <summary>A block of downtime UHIDs with how many have been entered, voided or are still open.</summary>
public sealed record DowntimeUhidBlockSummary(
    Guid Id,
    string Label,
    int Count,
    string FirstUhid,
    string LastUhid,
    DateTimeOffset IssuedAt,
    string? IssuedByName,
    int Entered,
    int Voided,
    int Open);

/// <summary>One UHID of a block, with the patient entered under it.</summary>
public sealed record DowntimeUhidDto(
    int Position,
    string Uhid,
    DowntimeUhidStatus Status,
    Guid? PatientId,
    string? PatientName,
    DateTimeOffset? PaperRegisteredAt,
    DateTimeOffset? EnteredAt,
    string? EnteredByName,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    string? VoidedByName,
    uint Version);

public sealed record DowntimeUhidBlockDetail(DowntimeUhidBlockSummary Block, string FacilityName, IReadOnlyList<DowntimeUhidDto> Numbers);

/// <param name="Label">Where the kit is kept, for example "Main reception, kit 2".</param>
/// <param name="Count">How many UHIDs: a day's registrations at that desk is a good size.</param>
public sealed record IssueDowntimeUhidsRequest(string Label, int Count);

public sealed class IssueDowntimeUhidsRequestValidator : AbstractValidator<IssueDowntimeUhidsRequest>
{
    public const int MaxCount = 200;

    public IssueDowntimeUhidsRequestValidator()
    {
        RuleFor(x => x.Label).NotEmpty().WithMessage("Say where the kit is kept.").SafeText(100);
        RuleFor(x => x.Count).InclusiveBetween(1, MaxCount).WithMessage($"Issue between 1 and {MaxCount} UHIDs.");
    }
}

/// <param name="Reason">Why the number will not be used: "label spoilt", "kit withdrawn".</param>
/// <param name="Version">The version of the number as it was listed.</param>
public sealed record VoidDowntimeUhidRequest(string Uhid, string Reason, uint Version);

public sealed class VoidDowntimeUhidRequestValidator : AbstractValidator<VoidDowntimeUhidRequest>
{
    public VoidDowntimeUhidRequestValidator()
    {
        RuleFor(x => x.Uhid).NotEmpty().MaximumLength(20);
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why the UHID will not be used.").SafeText(300);
    }
}
