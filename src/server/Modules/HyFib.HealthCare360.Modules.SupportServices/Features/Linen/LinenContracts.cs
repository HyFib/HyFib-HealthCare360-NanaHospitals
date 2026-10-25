using HyFib.HealthCare360.Modules.SupportServices.Domain.Linen;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Linen;

/// <param name="AverageWashes">Washes so far per piece in circulation, against <paramref name="LifeWashes"/>.</param>
public sealed record LinenItemDto(Guid Id, string Code, string Name, int LifeWashes, double? AverageWashes, bool IsActive, uint Version);

public sealed record SaveLinenItemRequest(Guid? Id, string Code, string Name, int LifeWashes, bool IsActive, uint? Version);

public sealed record LinenWard(Guid Id, string Name);

/// <summary>A ward's linen: what it holds of each item against its par level.</summary>
public sealed record WardLinen(Guid WardId, string WardName, IReadOnlyList<WardLinenLine> Lines);

public sealed record WardLinenLine(Guid LinenItemId, string ItemName, int Held, int Par, int Short);

/// <summary>Each item: clean in the store, on the wards, soiled at the laundry (infected apart), and in circulation.</summary>
public sealed record LinenItemStock(Guid LinenItemId, string Code, string Name, int Store, int Wards, int Soiled, int Infected, int InCirculation);

public sealed record LinenStock(IReadOnlyList<LinenItemStock> Items, IReadOnlyList<WardLinen> Wards);

public sealed record LinenLineInput(Guid LinenItemId, int Quantity);

/// <summary>
/// A movement of linen. <paramref name="WardId"/> for an issue, a soiled return, a loss or par levels;
/// <paramref name="Infected"/> for soiled linen kept apart; the wash process with its temperature and hold time for a
/// wash batch; the reason, and whether from the store or the laundry, for condemned linen.
/// </summary>
public sealed record RecordLinenRequest(
    LinenMovementKind Kind,
    Guid? WardId,
    bool Infected,
    WashProcess? Process,
    int? TemperatureC,
    int? HoldMinutes,
    CondemnReason? CondemnReason,
    LinenLocation? From,
    string? Notes,
    IReadOnlyList<LinenLineInput> Lines);

public sealed record SetParRequest(Guid WardId, IReadOnlyList<LinenLineInput> Lines);

public sealed record LinenMovementDto(
    Guid Id,
    string Number,
    LinenMovementKind Kind,
    string? WardName,
    bool Infected,
    WashProcess? Process,
    int? TemperatureC,
    int? HoldMinutes,
    CondemnReason? CondemnReason,
    string? Notes,
    DateTimeOffset At,
    string ByName,
    IReadOnlyList<LinenMovementLineDto> Lines,
    int Pieces);

public sealed record LinenMovementLineDto(Guid LinenItemId, string ItemName, int Quantity);

public sealed class SaveLinenItemRequestValidator : AbstractValidator<SaveLinenItemRequest>
{
    public SaveLinenItemRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().SafeText(24);
        RuleFor(x => x.Name).NotEmpty().SafeText(100);
        RuleFor(x => x.LifeWashes).InclusiveBetween(1, 1000);
        RuleFor(x => x.Version).NotNull().When(x => x.Id is not null).WithMessage("Send the version of the item you changed.");
    }
}

public sealed class RecordLinenRequestValidator : AbstractValidator<RecordLinenRequest>
{
    public RecordLinenRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.WardId).NotNull()
            .When(x => x.Kind is LinenMovementKind.Issued or LinenMovementKind.SoiledReturn or LinenMovementKind.Lost)
            .WithMessage("Choose the ward.");
        RuleFor(x => x.Process).NotNull().When(x => x.Kind == LinenMovementKind.Washed).WithMessage("Choose the wash process.");
        RuleFor(x => x.TemperatureC).InclusiveBetween(20, 100);
        RuleFor(x => x.HoldMinutes).InclusiveBetween(1, 240);
        RuleFor(x => x.CondemnReason).NotNull().When(x => x.Kind == LinenMovementKind.Condemned).WithMessage("Say why the linen is condemned.");
        RuleFor(x => x.From).Must(f => f is LinenLocation.Store or LinenLocation.Laundry).When(x => x.Kind == LinenMovementKind.Condemned)
            .WithMessage("Condemn linen from the store or the laundry.");
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.Lines).NotEmpty().Must(l => l.Count <= 50).WithMessage("Count at least one item, at most 50.");
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).InclusiveBetween(1, 10_000));
        RuleFor(x => x.Lines).Must(l => l.Select(x => x.LinenItemId).Distinct().Count() == l.Count).WithMessage("Count each item once.");
    }
}

public sealed class SetParRequestValidator : AbstractValidator<SetParRequest>
{
    public SetParRequestValidator()
    {
        RuleFor(x => x.WardId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty();
        RuleForEach(x => x.Lines).ChildRules(l => l.RuleFor(x => x.Quantity).InclusiveBetween(0, 10_000));
    }
}
