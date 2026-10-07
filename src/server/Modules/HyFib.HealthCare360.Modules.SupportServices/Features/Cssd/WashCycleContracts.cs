using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Cssd;

public sealed record WashTrayInput(Guid InstrumentSetId, int Quantity);

public sealed record StartWashCycleRequest(
    string WasherName,
    string? Program,
    string? OperatorName,
    string? Notes,
    IReadOnlyList<WashTrayInput> Trays);

/// <summary>The washer's printout (the temperature held and for how long) and the inspection of the trays afterwards.</summary>
public sealed record CompleteWashCycleRequest(decimal TemperatureC, decimal HoldMinutes, bool InspectedClean);

public sealed record AbortWashCycleRequest(string Reason);

public sealed record WashCycleListItem(
    Guid Id,
    string CycleNumber,
    string WasherName,
    string? Program,
    WashCycleStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    int? A0,
    int Trays,
    string? FailureReason);

public sealed record WashTrayDto(Guid InstrumentSetId, string? SetName, int Quantity);

/// <summary>A pack made from the cycle's trays, and the sterilizer load it went into.</summary>
public sealed record WashPackDto(Guid PackId, string PackNumber, Guid LoadId, string? LoadCycleNumber, string? SetName, PackStatus Status);

public sealed record WashCycleDto(
    Guid Id,
    string CycleNumber,
    string WasherName,
    string? Program,
    WashCycleStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    decimal? DisinfectionTemperatureC,
    decimal? HoldMinutes,
    int? A0,
    int RequiredA0,
    bool? InspectedClean,
    string? FailureReason,
    string? OperatorName,
    string? Notes,
    IReadOnlyList<WashTrayDto> Trays,
    IReadOnlyList<WashPackDto> Packs,
    uint Version);

public sealed class StartWashCycleRequestValidator : AbstractValidator<StartWashCycleRequest>
{
    public StartWashCycleRequestValidator()
    {
        RuleFor(x => x.WasherName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Program).MaximumLength(100);
        RuleFor(x => x.OperatorName).MaximumLength(200);
        RuleFor(x => x.Notes).SafeText(500);
        RuleFor(x => x.Trays).NotEmpty().WithMessage("Add at least one tray to the cycle.");
        RuleForEach(x => x.Trays).ChildRules(tray =>
        {
            tray.RuleFor(t => t.InstrumentSetId).NotEmpty();
            tray.RuleFor(t => t.Quantity).InclusiveBetween(1, 50);
        });
        RuleFor(x => x.Trays).Must(trays => trays is null || trays.Sum(t => t.Quantity) <= 200)
            .WithMessage("A cycle can hold at most 200 trays.");
    }
}

public sealed class CompleteWashCycleRequestValidator : AbstractValidator<CompleteWashCycleRequest>
{
    public CompleteWashCycleRequestValidator()
    {
        RuleFor(x => x.TemperatureC).InclusiveBetween(20m, 100m);
        RuleFor(x => x.HoldMinutes).InclusiveBetween(0m, 120m);
    }
}

public sealed class AbortWashCycleRequestValidator : AbstractValidator<AbortWashCycleRequest>
{
    public AbortWashCycleRequestValidator() => RuleFor(x => x.Reason).NotEmpty().SafeText(300);
}
