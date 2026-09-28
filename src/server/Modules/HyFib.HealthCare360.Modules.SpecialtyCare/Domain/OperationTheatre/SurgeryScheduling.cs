namespace HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

/// <summary>A booked (or proposed) theatre slot and the people it ties up.</summary>
public sealed record SurgerySlot(
    Guid BookingId,
    Guid TheatreId,
    Guid SurgeonId,
    Guid PatientId,
    Guid? AnaesthetistId,
    DateTimeOffset Start,
    DateTimeOffset End);

public enum ConflictKind
{
    Theatre = 0,
    Surgeon,
    Anaesthetist,
    Patient,
}

public sealed record SchedulingConflict(Guid BookingId, ConflictKind Kind, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Double-booking prevention (OT-01/02). Pure rules so they can be unit-tested; the service loads candidate
/// bookings from the database under an advisory lock and asks this class which ones actually collide.
/// </summary>
public static class SurgeryScheduling
{
    /// <summary>Longest slot we accept; anything longer is almost certainly a date-entry mistake.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    /// <summary>Statuses whose slot is protected. Requested/Postponed/Cancelled bookings do not hold a theatre.</summary>
    public static readonly IReadOnlyList<SurgeryStatus> BlockingStatuses =
        [SurgeryStatus.Scheduled, SurgeryStatus.InPreOp, SurgeryStatus.InProgress];

    public static bool IsBlocking(SurgeryStatus status) => BlockingStatuses.Contains(status);

    /// <summary>
    /// Half-open interval overlap: [start, end) — a case ending at 10:00 and the next starting at 10:00 do not clash,
    /// which allows back-to-back lists in the same theatre.
    /// </summary>
    public static bool Overlaps(DateTimeOffset aStart, DateTimeOffset aEnd, DateTimeOffset bStart, DateTimeOffset bEnd) =>
        aStart < bEnd && bStart < aEnd;

    public static Result ValidateWindow(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            return Error.Field("scheduledEnd", "The end time must be after the start time.");
        }

        return end - start > MaxDuration
            ? Error.Field("scheduledEnd", "A surgery slot cannot be longer than 24 hours.")
            : Result.Success();
    }

    /// <summary>
    /// Every way <paramref name="candidate"/> collides with <paramref name="existing"/> slots: the same theatre, surgeon,
    /// anaesthetist or patient at an overlapping time. The candidate's own booking is ignored (rescheduling).
    /// </summary>
    public static IReadOnlyList<SchedulingConflict> FindConflicts(SurgerySlot candidate, IEnumerable<SurgerySlot> existing)
    {
        var conflicts = new List<SchedulingConflict>();
        foreach (var other in existing)
        {
            if (other.BookingId == candidate.BookingId || !Overlaps(candidate.Start, candidate.End, other.Start, other.End))
            {
                continue;
            }

            if (other.TheatreId == candidate.TheatreId)
            {
                conflicts.Add(new SchedulingConflict(other.BookingId, ConflictKind.Theatre, other.Start, other.End));
            }

            if (other.SurgeonId == candidate.SurgeonId)
            {
                conflicts.Add(new SchedulingConflict(other.BookingId, ConflictKind.Surgeon, other.Start, other.End));
            }

            if (candidate.AnaesthetistId is { } anaesthetist && other.AnaesthetistId == anaesthetist)
            {
                conflicts.Add(new SchedulingConflict(other.BookingId, ConflictKind.Anaesthetist, other.Start, other.End));
            }

            if (other.PatientId == candidate.PatientId)
            {
                conflicts.Add(new SchedulingConflict(other.BookingId, ConflictKind.Patient, other.Start, other.End));
            }
        }

        return conflicts;
    }

    /// <summary>Plain-language summary for the 409 response.</summary>
    public static string Describe(IReadOnlyList<SchedulingConflict> conflicts)
    {
        var kinds = conflicts.Select(c => c.Kind).Distinct().Select(k => k switch
        {
            ConflictKind.Theatre => "the theatre is already booked",
            ConflictKind.Surgeon => "the surgeon is already operating",
            ConflictKind.Anaesthetist => "the anaesthetist is already allocated",
            _ => "the patient already has a surgery",
        });
        return $"This slot clashes with another booking: {string.Join("; ", kinds)}.";
    }
}
