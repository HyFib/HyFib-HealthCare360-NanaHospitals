using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Features.Ndps;

/// <summary>One line of a drug's day: a receipt, a dispense to a patient, a transfer or a write-off.</summary>
public sealed record NdpsMovement(
    DateTimeOffset At,
    StockMovementType Type,
    decimal In,
    decimal Out,
    decimal BalanceAfter,
    string? Reference,
    string? PatientUhid,
    string? PatientName,
    string? PrescriberName,
    string? PrescriptionNumber);

/// <summary>
/// A drug's day in a store (Form 3H): the opening stock, what came in, what was dispensed to patients and what else
/// went out, and the closing stock. <paramref name="Balanced"/> says the closing stock agrees with the register's
/// running balance.
/// </summary>
public sealed record NdpsDailyDrug(
    Guid ItemId,
    string ItemName,
    decimal Opening,
    decimal Received,
    decimal Dispensed,
    decimal OtherIn,
    decimal OtherOut,
    decimal Closing,
    bool Balanced,
    IReadOnlyList<NdpsMovement> Movements);

public sealed record NdpsDailyAccount(DateOnly Date, Guid StoreId, string StoreName, IReadOnlyList<NdpsDailyDrug> Drugs);

/// <summary>A drug's year in the institution, all stores together: the annual return, and next year's estimate (Form 3J).</summary>
public sealed record NdpsAnnualDrug(
    Guid ItemId,
    string ItemName,
    decimal Opening,
    decimal Received,
    decimal Consumed,
    decimal WrittenOff,
    decimal Closing,
    bool Balanced,
    decimal EstimateNextYear);

public sealed record NdpsAnnualReturn(int Year, IReadOnlyList<NdpsAnnualDrug> Drugs);

public sealed record NdpsPatientLine(
    DateTimeOffset At,
    string ItemName,
    string BatchNumber,
    decimal Quantity,
    StockMovementType Type,
    string? PrescriberName,
    string? PrescriberRegistrationNumber,
    string? PrescriptionNumber,
    string? Reference);

/// <summary>The patient's record of essential narcotic drugs dispensed to them (Form 3E).</summary>
public sealed record NdpsPatientRecord(
    Guid PatientId,
    string PatientName,
    string Uhid,
    string? Sex,
    int? AgeYears,
    string? Address,
    IReadOnlyList<NdpsPatientLine> Lines);

public sealed record NarcoticCountLineDto(Guid ItemId, string ItemName, decimal RegisterBalance, decimal Counted, decimal Difference);

public sealed record NarcoticCountDto(
    Guid Id,
    Guid StoreId,
    string StoreName,
    DateTimeOffset CountedAt,
    string CountedByName,
    NarcoticCountStatus Status,
    DateTimeOffset? WitnessedAt,
    string? WitnessedByName,
    string? Explanation,
    bool HasDifference,
    IReadOnlyList<NarcoticCountLineDto> Lines,
    uint Version);

public sealed record NdpsStore(Guid Id, string Name);

/// <summary>The drugs to count in a store: names only, so the count is blind to the register's balance.</summary>
public sealed record NarcoticCountSheet(Guid StoreId, string StoreName, IReadOnlyList<NarcoticCountSheetItem> Items);

public sealed record NarcoticCountSheetItem(Guid ItemId, string ItemName);

public sealed record CountedDrug(Guid ItemId, decimal Counted);

public sealed record RecordNarcoticCountRequest(Guid StoreId, IReadOnlyList<CountedDrug> Lines, string? Explanation);

public sealed record WitnessNarcoticCountRequest(string? Explanation, uint Version);

internal sealed class RecordNarcoticCountRequestValidator : AbstractValidator<RecordNarcoticCountRequest>
{
    public RecordNarcoticCountRequestValidator()
    {
        RuleFor(x => x.StoreId).NotEmpty();
        RuleFor(x => x.Lines).NotEmpty().Must(l => l.Select(x => x.ItemId).Distinct().Count() == l.Count)
            .WithMessage("Count each drug once.");
        RuleForEach(x => x.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ItemId).NotEmpty();
            line.RuleFor(l => l.Counted).GreaterThanOrEqualTo(0).LessThan(1_000_000);
        });
        RuleFor(x => x.Explanation).MaximumLength(1000);
    }
}

internal sealed class WitnessNarcoticCountRequestValidator : AbstractValidator<WitnessNarcoticCountRequest>
{
    public WitnessNarcoticCountRequestValidator()
    {
        RuleFor(x => x.Explanation).MaximumLength(1000);
    }
}
