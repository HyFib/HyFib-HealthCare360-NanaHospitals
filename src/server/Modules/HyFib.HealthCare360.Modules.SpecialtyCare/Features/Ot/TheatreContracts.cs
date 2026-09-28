using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;

public sealed record TheatreDto(
    Guid Id, string Code, string Name, TheatreType Type, IReadOnlyList<string> Equipment, string? Location, int DisplayOrder, bool IsActive, uint Version);

public sealed record SaveTheatreRequest(
    string Code, string Name, TheatreType Type, IReadOnlyList<string>? Equipment, string? Location, int DisplayOrder, bool IsActive, uint? Version);

/// <summary>One booking on the OT calendar (theatre and surgeon views).</summary>
public sealed record OtCalendarBooking(
    Guid Id,
    string BookingNumber,
    Guid PatientId,
    string Uhid,
    string PatientName,
    Gender Gender,
    string? Age,
    string ProcedureName,
    Laterality Laterality,
    SurgeryPriority Priority,
    SurgeryStatus Status,
    Guid? TheatreId,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    Guid? SurgeonId,
    string? SurgeonName,
    string? AnaesthetistName,
    AnaesthesiaType AnaesthesiaType,
    bool SignInDone,
    bool TimeOutDone,
    bool SignOutDone);

public sealed record OtCalendarTheatre(Guid TheatreId, string Code, string Name, TheatreType Type, IReadOnlyList<OtCalendarBooking> Bookings);

/// <summary>Calendar for a day or week: bookings per theatre plus requests still waiting for a slot.</summary>
public sealed record OtCalendar(DateOnly From, DateOnly To, IReadOnlyList<OtCalendarTheatre> Theatres, IReadOnlyList<OtCalendarBooking> AwaitingSlot);

public sealed record OtUtilisationRow(Guid TheatreId, string Code, string Name, TheatreUtilisation Metrics);

public sealed record OtUtilisationReport(DateOnly From, DateOnly To, int MinutesPerDay, IReadOnlyList<OtUtilisationRow> Theatres);

public sealed class SaveTheatreRequestValidator : AbstractValidator<SaveTheatreRequest>
{
    public SaveTheatreRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16).Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits and hyphens (for example OT-1).");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Location).SafeText(200);
        RuleFor(x => x.Equipment).Must(e => e is null || e.Count <= 50).WithMessage("List at most 50 equipment items.");
        RuleForEach(x => x.Equipment).NotEmpty().MaximumLength(100);
        RuleFor(x => x.DisplayOrder).InclusiveBetween(0, 10_000);
    }
}
