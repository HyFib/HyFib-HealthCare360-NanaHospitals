using HyFib.HealthCare360.Modules.Quality.Domain;

namespace HyFib.HealthCare360.Modules.Quality.Features.Downtime;

public sealed record DowntimeEventDto(
    Guid Id,
    string Number,
    DowntimeKind Kind,
    DowntimeCause Cause,
    string Affected,
    string? Description,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    int Minutes,
    DowntimeStatus Status,
    string? RecordedByName,
    string? ActionsTaken,
    string? EndedByName,
    DateTimeOffset? BackEntryCompletedAt,
    int? PaperRecords,
    string? BackEntryNote,
    string? BackEntrySignedByName,
    uint Version);

/// <summary>
/// The downtime of a period, as NABH asks it to be monitored: how often and how long the hospital worked without HC360,
/// how quickly unplanned downtime was put right, how quickly the paper records were entered afterwards, and the drills.
/// </summary>
/// <param name="UnplannedAvailabilityPercent">Share of the period's time HC360 was not down unexpectedly (planned downtime and drills excluded).</param>
/// <param name="MeanMinutesToRestore">Mean length of the unplanned downtime that has ended.</param>
/// <param name="MeanHoursToBackEntry">Mean time from the end of a downtime to the sign-off of its back-entry (drills included).</param>
public sealed record DowntimeSummary(
    DateOnly From,
    DateOnly To,
    int Unplanned,
    int Planned,
    int Drills,
    int UnplannedMinutes,
    int PlannedMinutes,
    int LongestUnplannedMinutes,
    decimal UnplannedAvailabilityPercent,
    int? MeanMinutesToRestore,
    decimal? MeanHoursToBackEntry,
    int Ongoing,
    int BackEntryPending,
    DateTimeOffset? LastDrillAt);

/// <param name="EndedAt">When it ended, if it already has (it is usually recorded after the fact).</param>
public sealed record RecordDowntimeRequest(
    DowntimeKind Kind,
    DowntimeCause Cause,
    string Affected,
    string? Description,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? ActionsTaken);

public sealed class RecordDowntimeRequestValidator : AbstractValidator<RecordDowntimeRequest>
{
    public RecordDowntimeRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Cause).IsInEnum();
        RuleFor(x => x.Affected).NotEmpty().WithMessage("Say what could not be used.").SafeText(300);
        RuleFor(x => x.Description).SafeText(2000);
        RuleFor(x => x.ActionsTaken).SafeText(2000);
    }
}

public sealed record EndDowntimeRequest(DateTimeOffset EndedAt, string? ActionsTaken, uint Version);

public sealed class EndDowntimeRequestValidator : AbstractValidator<EndDowntimeRequest>
{
    public EndDowntimeRequestValidator() => RuleFor(x => x.ActionsTaken).SafeText(2000);
}

/// <param name="PaperRecords">How many paper records were entered: registrations, orders, results, notes, bills.</param>
public sealed record SignOffBackEntryRequest(int PaperRecords, string? Note, uint Version);

public sealed class SignOffBackEntryRequestValidator : AbstractValidator<SignOffBackEntryRequest>
{
    public SignOffBackEntryRequestValidator()
    {
        RuleFor(x => x.PaperRecords).InclusiveBetween(0, 100_000).WithMessage("Enter how many paper records were entered, 0 or more.");
        RuleFor(x => x.Note).SafeText(1000);
    }
}
