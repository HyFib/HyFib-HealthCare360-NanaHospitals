using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Features.Masters;

public sealed record WardDto(
    Guid Id,
    string Code,
    string Name,
    WardType Type,
    string? Floor,
    string? Block,
    WardGenderPolicy GenderPolicy,
    string? NurseStation,
    string? Phone,
    Guid? DepartmentId,
    Guid? BedServiceItemId,
    string? BedServiceCode,
    int DisplayOrder,
    bool IsActive,
    string? Notes,
    int TotalBeds,
    int OccupiedBeds,
    uint Version);

public sealed record SaveWardRequest(
    string Code,
    string Name,
    WardType Type,
    string? Floor,
    string? Block,
    WardGenderPolicy GenderPolicy,
    string? NurseStation,
    string? Phone,
    Guid? DepartmentId,
    Guid? BedServiceItemId,
    int DisplayOrder,
    bool IsActive,
    string? Notes,
    uint? Version);

public sealed record RoomDto(Guid Id, Guid WardId, string Number, string? Description, bool IsIsolation, bool IsActive, int Beds, uint Version);

public sealed record SaveRoomRequest(Guid WardId, string Number, string? Description, bool IsIsolation, bool IsActive, uint? Version);

public sealed class SaveWardRequestValidator : AbstractValidator<SaveWardRequest>
{
    public SaveWardRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits or dashes.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.GenderPolicy).IsInEnum();
        RuleFor(x => x.Floor).MaximumLength(32);
        RuleFor(x => x.Block).MaximumLength(64);
        RuleFor(x => x.NurseStation).MaximumLength(64);
        RuleFor(x => x.Phone).PhoneNumber();
        RuleFor(x => x.DisplayOrder).InclusiveBetween(0, 10_000);
        RuleFor(x => x.Notes).SafeText(500);
    }
}

public sealed class SaveRoomRequestValidator : AbstractValidator<SaveRoomRequest>
{
    public SaveRoomRequestValidator()
    {
        RuleFor(x => x.WardId).NotEmpty();
        RuleFor(x => x.Number).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9-/ ]+$").WithMessage("Use letters, digits, spaces, '/' or '-'.");
        RuleFor(x => x.Description).SafeText(200);
    }
}
