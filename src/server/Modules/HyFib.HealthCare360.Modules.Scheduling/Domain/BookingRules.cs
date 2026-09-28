namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

/// <summary>An existing appointment of the patient, used for the overlap rule.</summary>
public sealed record PatientBooking(Guid Id, string Number, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>Online booking limits (OA-02): how late and how far ahead patients may book or change online.</summary>
public sealed record OnlineBookingPolicy(TimeSpan Cutoff, int WindowDays);

/// <summary>
/// Booking rules that do not need the database (PMS-02, OA-02). The booking service evaluates them inside the
/// booking transaction after taking the slot and patient locks, so the facts they are given are current.
/// </summary>
public static class BookingRules
{
    /// <summary>The chosen time must be an existing slot with a free seat (unless staff deliberately overbook).</summary>
    public static Result EnsureSlotCanTake(Slot? slot, bool overbook)
    {
        if (slot is null)
        {
            return Error.Rule("appointment.slot_unavailable", "This time is not available for booking. Choose another slot.");
        }

        return slot.IsFull && !overbook
            ? Error.Conflict("appointment.slot_full", "This slot has just been booked. Choose another slot.")
            : Result.Success();
    }

    /// <summary>A patient cannot hold two appointments whose times overlap (with any doctor, at any branch).</summary>
    public static Result EnsureNoOverlap(IEnumerable<PatientBooking> existing, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        var clash = existing.FirstOrDefault(a => a.StartsAt < endsAt && startsAt < a.EndsAt);
        return clash is null
            ? Result.Success()
            : Error.Conflict("appointment.patient_overlap", $"The patient already has appointment {clash.Number} at this time.", new { appointmentId = clash.Id });
    }

    /// <summary>
    /// The lowest free seat of the slot. Without overbooking only seats 1..capacity are used; with overbooking the
    /// next free seat above them is taken (the unique seat index still prevents two bookings sharing a seat).
    /// </summary>
    public static int? NextSeat(IEnumerable<int> takenSeats, int capacity, bool overbook)
    {
        var taken = takenSeats.ToHashSet();
        var limit = overbook ? Math.Max(capacity, taken.Count) + 1 : capacity;
        for (var seat = 1; seat <= limit; seat++)
        {
            if (!taken.Contains(seat))
            {
                return seat;
            }
        }

        return null;
    }

    /// <summary>Online bookings and online changes close <see cref="OnlineBookingPolicy.Cutoff"/> before the slot and open <see cref="OnlineBookingPolicy.WindowDays"/> ahead.</summary>
    public static Result EnsureOnlineBookable(DateTimeOffset startsAt, DateTimeOffset now, OnlineBookingPolicy policy)
    {
        var change = EnsureOnlineChangeAllowed(startsAt, now, policy.Cutoff);
        if (change.IsFailure)
        {
            return change;
        }

        return startsAt > now.AddDays(policy.WindowDays)
            ? Error.Rule("appointment.too_far_ahead", $"Online booking opens {policy.WindowDays} days ahead. Choose an earlier date.")
            : Result.Success();
    }

    public static Result EnsureOnlineChangeAllowed(DateTimeOffset startsAt, DateTimeOffset now, TimeSpan cutoff) =>
        startsAt - now >= cutoff
            ? Result.Success()
            : Error.Rule("appointment.online_cutoff", $"Online booking and changes close {(int)cutoff.TotalMinutes} minutes before the appointment. Please call the hospital.");

    /// <summary>Fee quoted at booking: follow-ups use the doctor's follow-up fee.</summary>
    public static decimal FeeFor(AppointmentVisitType visitType, decimal newVisitFee, decimal followUpFee) =>
        Money.Round(visitType == AppointmentVisitType.FollowUp ? followUpFee : newVisitFee);
}
