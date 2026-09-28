using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Campaigns;

public sealed record SegmentInput(
    int? MinAge,
    int? MaxAge,
    Gender? Gender,
    string? City,
    DateOnly? LastVisitFrom,
    DateOnly? LastVisitTo,
    IReadOnlyList<Guid>? DepartmentIds)
{
    public CampaignSegment ToSegment() => new(MinAge, MaxAge, Gender, City, LastVisitFrom, LastVisitTo, DepartmentIds ?? []);
}

public sealed record SaveCampaignRequest(string Name, CampaignChannel Channel, string TemplateKey, string Message, SegmentInput Segment, uint? Version);

public sealed record ScheduleCampaignRequest(DateTimeOffset ScheduledAt);

public sealed record CancelCampaignRequest(string Reason);

public sealed record AudienceSampleItem(string Uhid, string MaskedName, string? Age, string? City);

public sealed record AudiencePreview(int Count, IReadOnlyList<AudienceSampleItem> Sample);

public sealed record CampaignListItem(
    Guid Id,
    string Name,
    CampaignChannel Channel,
    CampaignStatus Status,
    DateTimeOffset? ScheduledAt,
    int AudienceCount,
    int QueuedCount,
    int SkippedCount,
    DateTimeOffset CreatedAt);

public sealed record CampaignDetail(
    Guid Id,
    string Name,
    CampaignChannel Channel,
    string TemplateKey,
    string Message,
    CampaignStatus Status,
    SegmentInput Segment,
    DateTimeOffset? ScheduledAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? CancelReason,
    int AudienceCount,
    int QueuedCount,
    int SkippedCount,
    uint Version);

/// <summary>Delivery tracking from the notification logs of the campaign's messages.</summary>
public sealed record CampaignDelivery(int Recipients, int Queued, int Sent, int Failed, int Skipped, int OptedOut);

public sealed record OptOutRequest(string Reason);

public sealed class SegmentInputValidator : AbstractValidator<SegmentInput>
{
    public SegmentInputValidator()
    {
        RuleFor(x => x.MinAge).InclusiveBetween(0, 120);
        RuleFor(x => x.MaxAge).InclusiveBetween(0, 120);
        RuleFor(x => x).Must(s => s.MinAge is null || s.MaxAge is null || s.MinAge <= s.MaxAge).WithName("maxAge").WithMessage("Maximum age is below the minimum age.");
        RuleFor(x => x).Must(s => s.LastVisitFrom is null || s.LastVisitTo is null || s.LastVisitFrom <= s.LastVisitTo)
            .WithName("lastVisitTo").WithMessage("The visit range ends before it starts.");
        RuleFor(x => x.City).SafeText(100);
        RuleFor(x => x.DepartmentIds).Must(d => d is null || d.Count <= 50).WithMessage("At most 50 departments.");
    }
}

public sealed class SaveCampaignRequestValidator : AbstractValidator<SaveCampaignRequest>
{
    public SaveCampaignRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().SafeText(150);
        RuleFor(x => x.TemplateKey).NotEmpty().MaximumLength(64);
        RuleFor(x => x.Message).NotEmpty().SafeText(1000);
        RuleFor(x => x.Message).MaximumLength(480).When(x => x.Channel == CampaignChannel.Sms).WithMessage("Keep SMS campaign text within 480 characters (3 SMS parts).");
        RuleFor(x => x.Segment).NotNull().SetValidator(new SegmentInputValidator());
    }
}

public sealed class CancelCampaignRequestValidator : AbstractValidator<CancelCampaignRequest>
{
    public CancelCampaignRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class OptOutRequestValidator : AbstractValidator<OptOutRequest>
{
    public OptOutRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}
