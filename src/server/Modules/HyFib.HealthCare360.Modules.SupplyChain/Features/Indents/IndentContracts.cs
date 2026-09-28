using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Features.Indents;

/// <param name="Quantity">Base units (tablets, pieces, ml).</param>
public sealed record IndentLineInput(Guid ItemId, decimal Quantity, string? Remarks);

/// <param name="FromStoreId">Store that will issue (central store, pharmacy).</param>
/// <param name="ToStoreId">Receiving sub-store; omit for direct departmental consumption.</param>
public sealed record CreateIndentRequest(
    Guid FromStoreId,
    Guid? ToStoreId,
    Guid? DepartmentId,
    IndentPriority Priority,
    string? Remarks,
    IReadOnlyList<IndentLineInput> Lines);

public sealed record IndentApprovalLine(Guid LineId, decimal ApprovedQuantity);

/// <param name="Lines">Reduced quantities; lines not listed are approved as requested.</param>
public sealed record ApproveIndentRequest(IReadOnlyList<IndentApprovalLine>? Lines);

/// <param name="BatchId">Issue from a scanned batch instead of FEFO.</param>
public sealed record IndentIssueLineInput(Guid LineId, decimal Quantity, Guid? BatchId);

public sealed record IssueIndentRequest(IReadOnlyList<IndentIssueLineInput> Lines, string? ReceivedByName, string? Remarks);

public sealed record IndentListItem(
    Guid Id,
    string Number,
    Guid FromStoreId,
    string FromStoreName,
    Guid? ToStoreId,
    string? ToStoreName,
    Guid? DepartmentId,
    string? DepartmentName,
    IndentPriority Priority,
    IndentStatus Status,
    DateTimeOffset RequestedAt,
    string? RequestedBy,
    int LineCount);

public sealed record IndentLineDto(
    Guid Id,
    Guid ItemId,
    string ItemName,
    decimal RequestedQuantity,
    decimal? ApprovedQuantity,
    decimal IssuedQuantity,
    decimal PendingQuantity,
    decimal Reserved,
    decimal AvailableInStore,
    string? Remarks);

public sealed record IndentDetail(
    Guid Id,
    string Number,
    Guid FromStoreId,
    string FromStoreName,
    Guid? ToStoreId,
    string? ToStoreName,
    Guid? DepartmentId,
    string? DepartmentName,
    IndentPriority Priority,
    IndentStatus Status,
    DateTimeOffset RequestedAt,
    string? RequestedBy,
    string? Remarks,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    IReadOnlyList<IndentLineDto> Lines,
    uint Version);

public sealed record IndentIssueLineDto(Guid ItemId, string ItemName, string BatchNumber, DateOnly? ExpiryDate, decimal Quantity, decimal UnitCost, decimal Value);

public sealed record IndentIssueDto(
    Guid Id,
    string Number,
    Guid IndentId,
    DateTimeOffset IssuedAt,
    string? IssuedBy,
    string? ReceivedByName,
    string? Remarks,
    decimal TotalValue,
    IReadOnlyList<IndentIssueLineDto> Lines);

public sealed class CreateIndentRequestValidator : AbstractValidator<CreateIndentRequest>
{
    public CreateIndentRequestValidator()
    {
        RuleFor(x => x.FromStoreId).NotEmpty();
        RuleFor(x => x).Must(x => x.ToStoreId is not null || x.DepartmentId is not null)
            .WithName("toStoreId").WithMessage("Choose the receiving store or the consuming department.");
        RuleFor(x => x).Must(x => x.ToStoreId != x.FromStoreId).WithName("toStoreId").WithMessage("A store cannot indent from itself.");
        RuleFor(x => x.Remarks).SafeText(1000);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one item.")
            .Must(l => l.Count <= 200).WithMessage("An indent can have at most 200 lines.")
            .Must(l => l.Select(x => x.ItemId).Distinct().Count() == l.Count).WithMessage("Each item can appear only once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
            line.RuleFor(l => l.Remarks).SafeText(300);
        });
    }
}

public sealed class ApproveIndentRequestValidator : AbstractValidator<ApproveIndentRequest>
{
    public ApproveIndentRequestValidator() =>
        RuleForEach(x => x.Lines).ChildRules(line => line.RuleFor(l => l.ApprovedQuantity).GreaterThanOrEqualTo(0));
}

public sealed class IssueIndentRequestValidator : AbstractValidator<IssueIndentRequest>
{
    public IssueIndentRequestValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Enter the quantity to issue.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.LineId).NotEmpty();
            line.RuleFor(l => l.Quantity).GreaterThan(0);
        });
        RuleFor(x => x.ReceivedByName).SafeText(120);
        RuleFor(x => x.Remarks).SafeText(500);
    }
}
