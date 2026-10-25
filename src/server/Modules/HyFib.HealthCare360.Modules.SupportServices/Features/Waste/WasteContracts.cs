using HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Waste;

public sealed record WasteBagDto(
    Guid Id,
    string Barcode,
    WasteCategory Category,
    Guid? DepartmentId,
    string Source,
    decimal WeightKg,
    DateTimeOffset CollectedAt,
    string CollectedByName,
    WasteBagStatus Status,
    bool Overdue,
    Guid? HandoverId,
    string? VoidReason,
    uint Version);

public sealed record WasteHandoverDto(
    Guid Id,
    string Number,
    DateTimeOffset HandedOverAt,
    string Cbwtf,
    string VehicleNumber,
    string ReceivedBy,
    string? ReceiptNumber,
    string HandedOverByName,
    int BagCount,
    decimal TotalKg,
    int LateBags,
    IReadOnlyList<WasteCategoryTotal> ByCategory);

public sealed record WasteCategoryTotal(WasteCategory Category, int Bags, decimal Kg);

/// <summary>The bags waiting in the storage area, the oldest first, with those kept beyond 48 hours.</summary>
public sealed record PendingWaste(IReadOnlyList<WasteBagDto> Bags, IReadOnlyList<WasteCategoryTotal> ByCategory, int Overdue);

public sealed record WasteDay(DateOnly Date, IReadOnlyList<WasteCategoryTotal> ByCategory, decimal TotalKg);

/// <summary>The month's register, day by day and category by category: what the hospital publishes on its website (Rule 4).</summary>
public sealed record WasteMonth(int Year, int Month, IReadOnlyList<WasteDay> Days, IReadOnlyList<WasteCategoryTotal> Totals, int Handovers, int LateBags);

/// <summary>The year's totals for the annual report to the State Pollution Control Board (Form IV, by 30 June).</summary>
public sealed record WasteYear(
    int Year,
    string? AuthorisationNumber,
    string? Cbwtf,
    IReadOnlyList<WasteCategoryTotal> Totals,
    decimal TotalKg,
    decimal AverageKgPerDay,
    int Handovers,
    int LateBags,
    int VoidedBags);

public sealed record RecordWasteBagRequest(WasteCategory Category, Guid? DepartmentId, string? Source, decimal WeightKg, DateTimeOffset? CollectedAt);

public sealed record VoidWasteBagRequest(string Reason, uint Version);

public sealed record HandOverWasteRequest(IReadOnlyList<string> Barcodes, string? Cbwtf, string VehicleNumber, string ReceivedBy, string? ReceiptNumber);

internal sealed class RecordWasteBagRequestValidator : AbstractValidator<RecordWasteBagRequest>
{
    public RecordWasteBagRequestValidator()
    {
        RuleFor(x => x.Category).IsInEnum();
        RuleFor(x => x.Source).MaximumLength(120);
        RuleFor(x => x).Must(x => x.DepartmentId is not null || !string.IsNullOrWhiteSpace(x.Source))
            .WithName("source").WithMessage("Say where the waste came from: the department or the place.");
        RuleFor(x => x.WeightKg).GreaterThan(0).LessThanOrEqualTo(WasteBag.MaxWeightKg);
    }
}

internal sealed class VoidWasteBagRequestValidator : AbstractValidator<VoidWasteBagRequest>
{
    public VoidWasteBagRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(300);
    }
}

internal sealed class HandOverWasteRequestValidator : AbstractValidator<HandOverWasteRequest>
{
    public HandOverWasteRequestValidator()
    {
        RuleFor(x => x.Barcodes).NotEmpty().Must(b => b.Count <= 500).WithMessage("Hand over at most 500 bags at a time.");
        RuleForEach(x => x.Barcodes).NotEmpty().MaximumLength(32);
        RuleFor(x => x.Cbwtf).MaximumLength(200);
        RuleFor(x => x.VehicleNumber).NotEmpty().MaximumLength(20);
        RuleFor(x => x.ReceivedBy).NotEmpty().MaximumLength(120);
        RuleFor(x => x.ReceiptNumber).MaximumLength(40);
    }
}
