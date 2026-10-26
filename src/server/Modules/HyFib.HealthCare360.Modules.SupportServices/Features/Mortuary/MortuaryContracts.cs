using HyFib.HealthCare360.Modules.SupportServices.Domain.Mortuary;

namespace HyFib.HealthCare360.Modules.SupportServices.Features.Mortuary;

/// <summary>A slot and who is in it.</summary>
public sealed record MortuarySlotDto(
    Guid Id, string Code, string? Chamber, bool IsActive, Guid? BodyId, string? BodyNumber, string? Name, DateTimeOffset? ReceivedAt, bool Unclaimed, uint Version);

public sealed record SlotInput(Guid? Id, string Code, string? Chamber, bool IsActive, uint? Version);

/// <summary>
/// The slots as the administrator sets them: new ones without an id, changed ones with the version they were loaded at; a slot
/// in use cannot be switched off.
/// </summary>
public sealed record SaveSlotsRequest(IReadOnlyList<SlotInput> Slots);

/// <summary>A body received. <paramref name="PatientId"/> for an in-hospital death (the name, sex and time of death are then the patient's).</summary>
public sealed record ReceiveBodyRequest(
    BodySource Source,
    Guid? PatientId,
    string? Name,
    bool Identified,
    string? Sex,
    int? AgeYears,
    DateTimeOffset? DiedAt,
    string? BroughtBy,
    bool IsMlc,
    string? MlcNumber,
    string? PoliceStation,
    string? IdentificationMarks,
    string? Belongings,
    Guid SlotId);

public sealed record ReleaseBodyRequest(
    ReleaseTo To, string ReceivedBy, string? Relationship, string? IdProof, string? PoliceClearance, bool BelongingsHandedOver, string? Notes, uint Version);

public sealed record BodyDto(
    Guid Id,
    string Number,
    Guid? PatientId,
    string? Uhid,
    BodySource Source,
    string Name,
    bool Identified,
    string? Sex,
    int? AgeYears,
    DateTimeOffset? DiedAt,
    DateTimeOffset ReceivedAt,
    string ReceivedByName,
    string? BroughtBy,
    bool IsMlc,
    string? MlcNumber,
    string? PoliceStation,
    string? IdentificationMarks,
    string? Belongings,
    string? SlotCode,
    BodyStatus Status,
    bool Unclaimed,
    DateTimeOffset? ReleasedAt,
    ReleaseTo? ReleasedTo,
    string? ReceivedBy,
    string? Relationship,
    string? IdProof,
    string? PoliceClearance,
    bool BelongingsHandedOver,
    string? ReleaseNotes,
    string? ReleasedByName,
    uint Version);

public sealed class SaveSlotsRequestValidator : AbstractValidator<SaveSlotsRequest>
{
    public SaveSlotsRequestValidator()
    {
        RuleFor(x => x.Slots).NotEmpty().Must(s => s.Count <= 200).WithMessage("At most 200 slots.");
        RuleForEach(x => x.Slots).ChildRules(s =>
        {
            s.RuleFor(x => x.Code).NotEmpty().SafeText(16);
            s.RuleFor(x => x.Chamber).SafeText(100);
            s.RuleFor(x => x.Version).NotNull().When(x => x.Id is not null).WithMessage("Send the version of the slot you changed.");
        });
        RuleFor(x => x.Slots).Must(s => s.Select(x => x.Code.Trim().ToUpperInvariant()).Distinct().Count() == s.Count)
            .WithMessage("Each slot needs its own label.");
    }
}

public sealed class ReceiveBodyRequestValidator : AbstractValidator<ReceiveBodyRequest>
{
    public ReceiveBodyRequestValidator()
    {
        RuleFor(x => x.Source).IsInEnum();
        RuleFor(x => x.PatientId).NotNull().When(x => x.Source == BodySource.InHospitalDeath)
            .WithMessage("Choose the patient who died in the hospital.");
        RuleFor(x => x.Name).NotEmpty().SafeText(200).When(x => x.PatientId is null);
        RuleFor(x => x.Sex).SafeText(16);
        RuleFor(x => x.AgeYears).InclusiveBetween(0, 130);
        RuleFor(x => x.BroughtBy).SafeText(200);
        RuleFor(x => x.MlcNumber).SafeText(64);
        RuleFor(x => x.PoliceStation).NotEmpty().SafeText(200).When(x => x.IsMlc || x.Source == BodySource.Police)
            .WithMessage("Name the police station of a medico-legal or police body.");
        RuleFor(x => x.IdentificationMarks).SafeText(1000);
        RuleFor(x => x.Belongings).SafeText(2000);
    }
}

public sealed class ReleaseBodyRequestValidator : AbstractValidator<ReleaseBodyRequest>
{
    public ReleaseBodyRequestValidator()
    {
        RuleFor(x => x.To).IsInEnum();
        RuleFor(x => x.ReceivedBy).NotEmpty().SafeText(200);
        RuleFor(x => x.Relationship).SafeText(64);
        RuleFor(x => x.IdProof).SafeText(200);
        RuleFor(x => x.PoliceClearance).SafeText(200);
        RuleFor(x => x.Notes).SafeText(1000);
    }
}
