using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Features.Availability;

public sealed record SlotDto(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly Date,
    TimeOnly LocalStart,
    TimeOnly LocalEnd,
    int Capacity,
    int Booked,
    int Available,
    ConsultationMode Mode,
    Guid FacilityId,
    Guid DepartmentId,
    string? Room)
{
    public static SlotDto From(Slot slot) => new(
        slot.StartsAt, slot.EndsAt, slot.Date, slot.LocalStart, slot.LocalEnd, slot.Capacity, slot.Booked, slot.Available,
        slot.Mode, slot.FacilityId, slot.DepartmentId, slot.Room);
}

/// <summary>A doctor's slots for the requested days (staff booking screen and calendar).</summary>
public sealed record PractitionerAvailability(
    Guid PractitionerId,
    string PractitionerName,
    Guid? DepartmentId,
    string? DepartmentName,
    bool OffersTelehealth,
    int FreeSlots,
    IReadOnlyList<SlotDto> Slots);
