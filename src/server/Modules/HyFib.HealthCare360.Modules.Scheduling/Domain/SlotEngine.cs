namespace HyFib.HealthCare360.Modules.Scheduling.Domain;

/// <summary>An interval already taken by an active appointment of the practitioner.</summary>
public readonly record struct BusyInterval(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>What the slot engine should produce.</summary>
/// <param name="From">First local (IST) calendar date.</param>
/// <param name="To">Last local calendar date (inclusive).</param>
/// <param name="NotBefore">Slots starting before this instant are dropped (now, or now + online cut-off).</param>
public sealed record SlotQuery(DateOnly From, DateOnly To, DateTimeOffset NotBefore)
{
    /// <summary>Only sessions offering this mode (InPerson or Telehealth); null = any.</summary>
    public ConsultationMode? Mode { get; init; }

    public Guid? FacilityId { get; init; }

    /// <summary>Also return full slots (staff calendars show them with their bookings).</summary>
    public bool IncludeFull { get; init; }
}

/// <summary>One bookable (or, with <see cref="SlotQuery.IncludeFull"/>, fully booked) slot.</summary>
public sealed record Slot(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateOnly Date,
    TimeOnly LocalStart,
    TimeOnly LocalEnd,
    int Capacity,
    int Booked,
    ConsultationMode Mode,
    Guid FacilityId,
    Guid DepartmentId,
    string? Room,
    Guid? ScheduleId)
{
    public int Available => Math.Max(0, Capacity - Booked);

    public bool IsFull => Booked >= Capacity;
}

/// <summary>
/// Pure availability calculation for ONE practitioner (PMS-01, OA-02): weekly sessions plus extra sessions, minus
/// leave and blocks, minus seats already taken by active appointments — on the hospital's local calendar, never in
/// the past and never inside the online cut-off. Everything it needs is passed in, so the same code serves the staff
/// calendar, the public directory, the portal and the booking transaction (which re-runs it under a lock).
/// </summary>
public static class SlotEngine
{
    /// <summary>Upper bound on the calendar range of one calculation (protects the server from huge requests).</summary>
    public const int MaxRangeDays = 62;

    private const int MinutesPerDay = 24 * 60;

    public static IReadOnlyList<Slot> Compute(
        IReadOnlyCollection<DoctorSchedule> schedules,
        IReadOnlyCollection<ScheduleException> exceptions,
        IReadOnlyCollection<BusyInterval> busy,
        SlotQuery query,
        HospitalCalendar calendar)
    {
        if (query.To < query.From)
        {
            return [];
        }

        var last = query.To.DayNumber - query.From.DayNumber >= MaxRangeDays ? query.From.AddDays(MaxRangeDays - 1) : query.To;
        var slots = new List<Slot>();
        var taken = new HashSet<DateTimeOffset>();

        for (var date = query.From; date <= last; date = date.AddDays(1))
        {
            var blocks = exceptions.Where(e => e.RemovesSlots && e.Covers(date)).ToList();
            foreach (var session in SessionsOn(date, schedules, exceptions))
            {
                if ((query.FacilityId is { } facility && session.FacilityId != facility) || !Offers(session.Mode, query.Mode))
                {
                    continue;
                }

                var startMinute = MinuteOfDay(session.Start);
                var endMinute = MinuteOfDay(session.End);
                for (var minute = startMinute; minute + session.SlotMinutes <= endMinute; minute += session.SlotMinutes)
                {
                    var localStart = TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute));
                    var localEnd = minute + session.SlotMinutes >= MinutesPerDay
                        ? TimeOnly.MaxValue
                        : TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(minute + session.SlotMinutes));
                    if (blocks.Any(b => b.Blocks(date, localStart, localEnd, session.FacilityId)))
                    {
                        continue;
                    }

                    var startsAt = calendar.ToUtc(date, localStart);
                    var endsAt = startsAt.AddMinutes(session.SlotMinutes);
                    if (startsAt < query.NotBefore || !taken.Add(startsAt))
                    {
                        continue;
                    }

                    var booked = busy.Count(b => b.StartsAt < endsAt && startsAt < b.EndsAt);
                    if (booked >= session.Capacity && !query.IncludeFull)
                    {
                        continue;
                    }

                    slots.Add(new Slot(
                        startsAt, endsAt, date, localStart, localEnd, session.Capacity, booked, session.Mode,
                        session.FacilityId, session.DepartmentId, session.Room, session.ScheduleId));
                }
            }
        }

        slots.Sort(static (a, b) => a.StartsAt.CompareTo(b.StartsAt));
        return slots;
    }

    /// <summary>Does a session in <paramref name="offered"/> mode serve a request for <paramref name="requested"/>?</summary>
    public static bool Offers(ConsultationMode offered, ConsultationMode? requested) =>
        requested is null || requested == ConsultationMode.Both || offered == ConsultationMode.Both || offered == requested;

    private static IEnumerable<Session> SessionsOn(DateOnly date, IEnumerable<DoctorSchedule> schedules, IEnumerable<ScheduleException> exceptions)
    {
        foreach (var s in schedules.Where(s => s.AppliesOn(date)).OrderBy(s => s.StartTime))
        {
            yield return new Session(s.StartTime, s.EndTime, s.SlotMinutes, s.CapacityPerSlot, s.Mode, s.FacilityId, s.DepartmentId, s.Room, s.Id);
        }

        foreach (var e in exceptions.Where(e => e.IsCompleteExtraSession && e.Covers(date)).OrderBy(e => e.StartTime))
        {
            yield return new Session(
                e.StartTime!.Value, e.EndTime!.Value, e.SlotMinutes!.Value, e.CapacityPerSlot!.Value, e.Mode ?? ConsultationMode.InPerson,
                e.FacilityId!.Value, e.DepartmentId!.Value, e.Room, null);
        }
    }

    private static int MinuteOfDay(TimeOnly time) => (time.Hour * 60) + time.Minute;

    private sealed record Session(
        TimeOnly Start, TimeOnly End, int SlotMinutes, int Capacity, ConsultationMode Mode, Guid FacilityId, Guid DepartmentId, string? Room, Guid? ScheduleId);
}
