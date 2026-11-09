using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Features.Downtime;

/// <summary>
/// A record entered from paper under a downtime number: the number written on the paper and the time on it. Sent with a
/// visit check-in, a bill's finalization or a receipt.
/// </summary>
public sealed record PaperNumber(string Number, DateTimeOffset At);

public sealed class PaperNumberValidator : AbstractValidator<PaperNumber>
{
    public PaperNumberValidator(TimeProvider time)
    {
        RuleFor(x => x.Number).NotEmpty().WithMessage("Enter the number written on the paper.").MaximumLength(24)
            .Matches("^[A-Za-z0-9/-]+$").WithMessage("Enter the number as it is printed.");
        RuleFor(x => x.At).Must(at => at <= time.GetUtcNow() + BackEntryServiceTolerance.Value)
            .WithMessage("The time on the paper cannot be in the future.");
    }
}

internal static class BackEntryServiceTolerance
{
    public static readonly TimeSpan Value = Services.BackEntryService.ClockTolerance;
}

public sealed record DowntimeNumberBlockSummary(
    Guid Id,
    DowntimeNumberKind Kind,
    string KindName,
    string Counter,
    int Count,
    string FirstNumber,
    string LastNumber,
    DateTimeOffset IssuedAt,
    string? IssuedByName,
    int Entered,
    int Voided,
    int Open,
    DateTimeOffset? ReturnedAt,
    string? ReturnedByName,
    string? ReturnNote,
    uint Version);

public sealed record DowntimeNumberDto(
    int Position,
    string Number,
    DowntimeUhidStatus Status,
    string? EntityType,
    Guid? EntityId,
    Guid? PatientId,
    string? PatientName,
    DateTimeOffset? PaperAt,
    DateTimeOffset? EnteredAt,
    string? EnteredByName,
    bool LateBackEntry,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    string? VoidedByName,
    uint Version);

public sealed record DowntimeNumberBlockDetail(DowntimeNumberBlockSummary Block, string FacilityName, IReadOnlyList<DowntimeNumberDto> Numbers);

/// <param name="Counter">The counter that keeps the block, for example "OPD billing counter 1".</param>
/// <param name="Count">How many numbers: about a day's visits, bills or receipts at that counter.</param>
public sealed record IssueDowntimeNumbersRequest(DowntimeNumberKind Kind, string Counter, int Count);

public sealed class IssueDowntimeNumbersRequestValidator : AbstractValidator<IssueDowntimeNumbersRequest>
{
    public const int MaxCount = 500;

    public IssueDowntimeNumbersRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Counter).NotEmpty().WithMessage("Say which counter keeps the block.").SafeText(100);
        RuleFor(x => x.Count).InclusiveBetween(1, MaxCount).WithMessage($"Issue between 1 and {MaxCount} numbers.");
    }
}

public sealed record VoidDowntimeNumberRequest(string Number, string Reason, uint Version);

public sealed class VoidDowntimeNumberRequestValidator : AbstractValidator<VoidDowntimeNumberRequest>
{
    public VoidDowntimeNumberRequestValidator()
    {
        RuleFor(x => x.Number).NotEmpty().MaximumLength(24);
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why the number will not be used.").SafeText(300);
        RuleFor(x => x.Version).SeenVersion();
    }
}

/// <param name="Note">What came back with the block: "slips 41–50 spoilt in the rain".</param>
public sealed record ReturnDowntimeBlockRequest(string? Note, uint Version);

public sealed class ReturnDowntimeBlockRequestValidator : AbstractValidator<ReturnDowntimeBlockRequest>
{
    public ReturnDowntimeBlockRequestValidator()
    {
        RuleFor(x => x.Note).SafeText(300);
        RuleFor(x => x.Version).SeenVersion();
    }
}

/// <summary>A record type's back-entry window, in hours, as in force at the facility, with its default and maximum.</summary>
public sealed record BackEntryWindowDto(BackEntryRecord Record, string Name, string SettingKey, int Hours, int DefaultHours, int MaxHours);

/// <param name="PaperFrom">The earliest time on the papers to be entered.</param>
/// <param name="PaperTo">The latest.</param>
/// <param name="Records">How many, from the back-entry tally.</param>
public sealed record RequestLateBackEntry(BackEntryRecord Record, DateTimeOffset PaperFrom, DateTimeOffset PaperTo, int Records, string Reason);

public sealed class RequestLateBackEntryValidator : AbstractValidator<RequestLateBackEntry>
{
    public RequestLateBackEntryValidator()
    {
        RuleFor(x => x.Record).IsInEnum();
        RuleFor(x => x.PaperTo).GreaterThanOrEqualTo(x => x.PaperFrom).WithMessage("The span ends before it starts.");
        RuleFor(x => x).Must(x => x.PaperTo - x.PaperFrom <= Services.BackEntryService.LongestSpan)
            .WithName("paperFrom").WithMessage("Ask for at most 90 days of paper at a time.");
        RuleFor(x => x.Records).InclusiveBetween(1, 5000).WithMessage("Say how many records, from 1 to 5000.");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Say why the records are entered late.").SafeText(500);
    }
}

public sealed record LateBackEntryGrantDto(
    Guid Id,
    BackEntryRecord Record,
    DateTimeOffset PaperFrom,
    DateTimeOffset PaperTo,
    int MaxRecords,
    int Used,
    string RequestedByName,
    string Reason,
    string? ApprovedByName,
    DateTimeOffset ApprovedAt,
    DateTimeOffset ExpiresAt,
    bool Usable);
