using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.OnlineBooking;

public sealed record PublicFacility(Guid Id, string Name, string? City);

/// <summary>A doctor card in the public directory (OA-01): no internal identifiers beyond ids, no contact details.</summary>
public sealed record PublicDoctor(
    Guid Id,
    string Name,
    Gender Gender,
    string? Qualification,
    Guid? SpecialtyId,
    string? SpecialtyName,
    Guid? DepartmentId,
    string? DepartmentName,
    IReadOnlyList<string> Languages,
    decimal FeeNew,
    decimal FeeFollowUp,
    string Currency,
    bool OffersTelehealth,
    IReadOnlyList<PublicFacility> Facilities,
    DateTimeOffset? NextAvailableAt,
    DateTimeOffset? NextTelehealthAt);

public sealed record PublicSession(Weekday Weekday, TimeOnly StartTime, TimeOnly EndTime, ConsultationMode Mode, Guid FacilityId);

public sealed record PublicDoctorProfile(PublicDoctor Doctor, string? Bio, int FollowUpValidityDays, IReadOnlyList<PublicSession> WeeklySessions);

public sealed record PublicSlot(DateTimeOffset StartsAt, DateTimeOffset EndsAt, TimeOnly LocalTime, ConsultationMode Mode, Guid FacilityId, int SeatsLeft);

public sealed record PublicSlotDay(DateOnly Date, IReadOnlyList<PublicSlot> Slots);

public sealed record DirectoryOption(Guid Id, string Name, int Doctors);

/// <summary>Everything the public booking page needs for its filter bar, plus the online booking rules to show patients.</summary>
public sealed record DirectoryFilters(
    IReadOnlyList<DirectoryOption> Specialties,
    IReadOnlyList<DirectoryOption> Departments,
    IReadOnlyList<string> Languages,
    IReadOnlyList<PublicFacility> Facilities,
    int CutoffMinutes,
    int WindowDays);
