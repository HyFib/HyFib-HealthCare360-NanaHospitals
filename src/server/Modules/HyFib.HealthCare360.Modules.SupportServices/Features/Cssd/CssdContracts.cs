using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;

public sealed record InstrumentSetItemDto(string Name, int Quantity);

public sealed record InstrumentSetDto(
    Guid Id,
    string Code,
    string Name,
    string? Category,
    Guid? OwnerDepartmentId,
    SterilizationMethod Method,
    int ShelfLifeDays,
    bool IsActive,
    string? Notes,
    IReadOnlyList<InstrumentSetItemDto> Items,
    int InstrumentCount,
    int PacksAvailable,
    uint Version);

public sealed record SaveInstrumentSetRequest(
    string Code,
    string Name,
    string? Category,
    Guid? OwnerDepartmentId,
    SterilizationMethod Method,
    int ShelfLifeDays,
    bool IsActive,
    string? Notes,
    IReadOnlyList<InstrumentSetItemDto> Items,
    uint? Version);

public sealed record RequestLineInput(Guid InstrumentSetId, int Quantity);

public sealed record CreateCssdRequestRequest(Guid DepartmentId, DateTimeOffset? RequiredBy, CssdPriority Priority, string? Notes, IReadOnlyList<RequestLineInput> Lines);

public sealed record CancelCssdRequestRequest(string Reason);

public sealed record CssdRequestLineDto(Guid InstrumentSetId, string? SetName, int Quantity, int IssuedQuantity);

public sealed record CssdRequestDto(
    Guid Id,
    string RequestNumber,
    Guid DepartmentId,
    string? DepartmentName,
    string? RequestedByName,
    DateTimeOffset RequestedAt,
    DateTimeOffset? RequiredBy,
    CssdPriority Priority,
    CssdRequestStatus Status,
    string? Notes,
    string? CancelReason,
    IReadOnlyList<CssdRequestLineDto> Lines);

public sealed record CountedItemInput(string ItemName, int Received, int Damaged);

public sealed record SoiledReceiptLineInput(Guid InstrumentSetId, Guid? SetIssueId, IReadOnlyList<CountedItemInput> Counts);

public sealed record ReceiveSoiledRequest(Guid DepartmentId, string? HandedOverByName, string? Notes, IReadOnlyList<SoiledReceiptLineInput> Lines);

public sealed record SoiledReceiptLineDto(
    Guid InstrumentSetId,
    string? SetName,
    Guid? SetIssueId,
    int ExpectedCount,
    int ReceivedCount,
    int MissingCount,
    int ExtraCount,
    int DamagedCount,
    string? Discrepancies);

public sealed record SoiledReceiptDto(
    Guid Id,
    Guid DepartmentId,
    string? DepartmentName,
    DateTimeOffset ReceivedAt,
    string? ReceivedByName,
    string? HandedOverByName,
    bool HasDiscrepancy,
    string? Notes,
    IReadOnlyList<SoiledReceiptLineDto> Lines);

public sealed record IssuePacksRequest(Guid DepartmentId, Guid? CssdRequestId, string? IssuedToName, IReadOnlyList<Guid> PackIds);

public sealed record SetIssueDto(
    Guid Id,
    Guid PackId,
    string? PackNumber,
    Guid LoadId,
    string? CycleNumber,
    Guid InstrumentSetId,
    string? SetName,
    Guid DepartmentId,
    string? DepartmentName,
    DateTimeOffset IssuedAt,
    string? IssuedToName,
    DateOnly? SterileUntil,
    SetIssueStatus Status,
    Guid? PatientId,
    DateTimeOffset? UsedAt,
    DateTimeOffset? RecallFlaggedAt,
    string? RecallReason,
    DateTimeOffset? RecallAcknowledgedAt,
    string? RecallAction);

public sealed record RecordPackUseRequest(Guid PatientId, DateTimeOffset? UsedAt);

public sealed record AcknowledgeRecallRequest(string Action);

public sealed record MethodLoadCount(SterilizationMethod Method, int Loads, int Failed);

public sealed record SetStock(Guid InstrumentSetId, string Code, string Name, int Available, int ExpiringSoon);

/// <summary>CSSD dashboard and report for a period (loads, failures, indicators, issues, recalls, stock).</summary>
public sealed record CssdSummary(
    DateOnly From,
    DateOnly To,
    int Loads,
    int LoadsReleased,
    int LoadsFailed,
    decimal FailureRatePercent,
    int BiologicalPending,
    IReadOnlyList<MethodLoadCount> ByMethod,
    int PacksIssued,
    int OpenRequests,
    int ReceiptsWithDiscrepancy,
    int OpenRecalls,
    int PacksExpiringSoon,
    IReadOnlyList<SetStock> AvailableBySet);

public sealed class SaveInstrumentSetRequestValidator : AbstractValidator<SaveInstrumentSetRequest>
{
    public SaveInstrumentSetRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(24).Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits or dashes.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Category).MaximumLength(64);
        RuleFor(x => x.Method).IsInEnum();
        RuleFor(x => x.ShelfLifeDays).InclusiveBetween(1, 365);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.Items).NotEmpty().WithMessage("Add the instruments on the count sheet.");
        RuleForEach(x => x.Items).ChildRules(item =>
        {
            item.RuleFor(i => i.Name).NotEmpty().MaximumLength(128);
            item.RuleFor(i => i.Quantity).InclusiveBetween(1, 500);
        });
        RuleFor(x => x.Items).Must(items => items is null || items.Select(i => (i.Name ?? string.Empty).Trim().ToUpperInvariant()).Distinct().Count() == items.Count)
            .WithMessage("Each instrument should appear once on the count sheet.");
    }
}

public sealed class CreateCssdRequestRequestValidator : AbstractValidator<CreateCssdRequestRequest>
{
    public CreateCssdRequestRequestValidator()
    {
        RuleFor(x => x.DepartmentId).NotEmpty();
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Request at least one set.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.InstrumentSetId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 50);
        });
    }
}

public sealed class CancelCssdRequestRequestValidator : AbstractValidator<CancelCssdRequestRequest>
{
    public CancelCssdRequestRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}

public sealed class ReceiveSoiledRequestValidator : AbstractValidator<ReceiveSoiledRequest>
{
    public ReceiveSoiledRequestValidator()
    {
        RuleFor(x => x.DepartmentId).NotEmpty();
        RuleFor(x => x.HandedOverByName).MaximumLength(200);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one tray.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.InstrumentSetId).NotEmpty();
            line.RuleFor(l => l.Counts).NotNull();
            line.RuleForEach(l => l.Counts).ChildRules(count =>
            {
                count.RuleFor(c => c.ItemName).NotEmpty().MaximumLength(128);
                count.RuleFor(c => c.Received).InclusiveBetween(0, 500);
                count.RuleFor(c => c.Damaged).GreaterThanOrEqualTo(0);
                count.RuleFor(c => c).Must(c => c.Damaged <= c.Received).WithName("damaged").WithMessage("Damaged pieces are part of the received count.");
            });
        });
    }
}

public sealed class IssuePacksRequestValidator : AbstractValidator<IssuePacksRequest>
{
    public IssuePacksRequestValidator()
    {
        RuleFor(x => x.DepartmentId).NotEmpty();
        RuleFor(x => x.IssuedToName).MaximumLength(200);
        RuleFor(x => x.PackIds).NotEmpty().WithMessage("Scan at least one pack.");
        RuleFor(x => x.PackIds).Must(ids => ids is null || (ids.Count <= 100 && ids.Distinct().Count() == ids.Count))
            .WithMessage("Scan each pack once (at most 100 per issue).");
    }
}

public sealed class RecordPackUseRequestValidator : AbstractValidator<RecordPackUseRequest>
{
    public RecordPackUseRequestValidator(TimeProvider time)
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.UsedAt).Must(at => at is null || (at <= time.GetUtcNow().AddMinutes(5) && at >= time.GetUtcNow().AddDays(-7)))
            .WithMessage("The time of use must be within the last 7 days and not in the future.");
    }
}

public sealed class AcknowledgeRecallRequestValidator : AbstractValidator<AcknowledgeRecallRequest>
{
    public AcknowledgeRecallRequestValidator() =>
        RuleFor(x => x.Action).NotEmpty().SafeText(500).WithMessage("Describe the action taken (pack returned, patient informed, infection control notified…).");
}
