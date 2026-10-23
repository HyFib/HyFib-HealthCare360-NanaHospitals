using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Adjustments;

/// <param name="QuantityChange">Signed base units: negative writes off, positive books found stock.</param>
public sealed record AdjustmentLineInput(Guid BatchId, decimal QuantityChange);

public sealed record CreateAdjustmentRequest(Guid StoreId, AdjustmentReason Reason, string? Remarks, IReadOnlyList<AdjustmentLineInput> Lines);

public sealed record AdjustmentListItem(
    Guid Id,
    string Number,
    Guid StoreId,
    string StoreName,
    AdjustmentReason Reason,
    AdjustmentStatus Status,
    decimal NetValue,
    DateTimeOffset RequestedAt,
    string? RequestedBy,
    int LineCount);

public sealed record AdjustmentLineDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    Guid BatchId,
    string BatchNumber,
    DateOnly? ExpiryDate,
    decimal SystemQuantity,
    decimal QuantityChange,
    decimal UnitCost,
    decimal Value);

public sealed record AdjustmentDetail(
    Guid Id,
    string Number,
    Guid StoreId,
    string StoreName,
    AdjustmentReason Reason,
    AdjustmentStatus Status,
    string? Remarks,
    decimal NetValue,
    DateTimeOffset RequestedAt,
    string? RequestedBy,
    DateTimeOffset? DecidedAt,
    string? DecidedBy,
    string? RejectReason,
    Guid? PhysicalCountId,
    IReadOnlyList<AdjustmentLineDto> Lines,
    uint Version);

/// <param name="ItemType">Count only one kind of item (cycle count), e.g. drugs this week, surgicals next.</param>
public sealed record StartCountRequest(Guid StoreId, ItemType? ItemType, Guid? CategoryId, string? Remarks);

public sealed record CountEntry(Guid LineId, decimal CountedQuantity, string? Remarks);

/// <summary>Counted lines, with the version of the count sheet they were counted on (REL-04).</summary>
public sealed record RecordCountsRequest(IReadOnlyList<CountEntry> Lines, uint Version);

/// <param name="Version">The version of the count sheet as it was last read (REL-04): 409 when counts were saved since.</param>
public sealed record SubmitCountRequest(uint Version);

public sealed class SubmitCountRequestValidator : AbstractValidator<SubmitCountRequest>
{
    public SubmitCountRequestValidator() => RuleFor(x => x.Version).SeenVersion();
}

public sealed record CountListItem(
    Guid Id,
    string Number,
    Guid StoreId,
    string StoreName,
    StockCountStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? SubmittedAt,
    int LineCount,
    int CountedLines,
    Guid? AdjustmentId);

public sealed record CountLineDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    Guid BatchId,
    string BatchNumber,
    DateOnly? ExpiryDate,
    decimal SystemQuantity,
    decimal? CountedQuantity,
    decimal? VarianceQuantity,
    decimal UnitCost,
    string? Remarks);

public sealed record CountDetail(
    Guid Id,
    string Number,
    Guid StoreId,
    string StoreName,
    StockCountStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? SubmittedAt,
    ItemType? ItemTypeFilter,
    Guid? CategoryFilterId,
    string? Remarks,
    Guid? AdjustmentId,
    IReadOnlyList<CountLineDto> Lines,
    uint Version);

public sealed class CreateAdjustmentRequestValidator : AbstractValidator<CreateAdjustmentRequest>
{
    public CreateAdjustmentRequestValidator()
    {
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.Remarks).NotEmpty().When(x => x.Reason is AdjustmentReason.Other or AdjustmentReason.DataCorrection or AdjustmentReason.Theft or AdjustmentReason.Loss)
            .WithMessage("Explain the adjustment.");
        RuleFor(x => x.Remarks).SafeText(1000);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one batch.")
            .Must(l => l.Count <= 300).WithMessage("An adjustment can have at most 300 lines.")
            .Must(l => l.Select(x => x.BatchId).Distinct().Count() == l.Count).WithMessage("Each batch can appear only once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.BatchId).NotEmpty();
            line.RuleFor(l => l.QuantityChange).NotEqual(0);
        });
    }
}

public sealed class StartCountRequestValidator : AbstractValidator<StartCountRequest>
{
    public StartCountRequestValidator()
    {
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.Remarks).SafeText(1000);
    }
}

public sealed class RecordCountsRequestValidator : AbstractValidator<RecordCountsRequest>
{
    public RecordCountsRequestValidator()
    {
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.LineId).NotEmpty();
            line.RuleFor(l => l.CountedQuantity).GreaterThanOrEqualTo(0);
            line.RuleFor(l => l.Remarks).SafeText(300);
        });
    }
}
