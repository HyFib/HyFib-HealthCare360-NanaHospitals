using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

/// <summary>Fixtures for schedules and appointments. 28 Sep 2026 is a Monday; IST is UTC+05:30.</summary>
internal static class TestSchedules
{
    public static readonly DateOnly Monday = new(2026, 9, 28);
    public static readonly DateOnly Tuesday = Monday.AddDays(1);
    public static readonly DateOnly Sunday = Monday.AddDays(6);

    public static readonly Guid Facility = Guid.Parse("0199a000-0000-7000-8000-000000000001");
    public static readonly Guid OtherFacility = Guid.Parse("0199a000-0000-7000-8000-000000000002");
    public static readonly Guid Department = Guid.Parse("0199a000-0000-7000-8000-000000000010");
    public static readonly Guid Doctor = Guid.Parse("0199a000-0000-7000-8000-000000000100");
    public static readonly Guid Patient = Guid.Parse("0199a000-0000-7000-8000-000000001000");

    public static HospitalCalendar Calendar => HospitalCalendar.India;

    public static DoctorSchedule Session(
        TimeOnly start,
        TimeOnly end,
        Weekday day = Weekday.Monday,
        int slotMinutes = 15,
        int capacity = 1,
        ConsultationMode mode = ConsultationMode.InPerson,
        Guid? facility = null) => new()
    {
        FacilityId = facility ?? Facility,
        PractitionerId = Doctor,
        DepartmentId = Department,
        Weekday = day,
        StartTime = start,
        EndTime = end,
        SlotMinutes = slotMinutes,
        CapacityPerSlot = capacity,
        Mode = mode,
        Room = "OPD-01",
        EffectiveFrom = new DateOnly(2026, 1, 1),
    };

    /// <summary>Monday 09:00–13:00, 15-minute slots, one patient per slot (16 slots).</summary>
    public static DoctorSchedule MondayMorning(int capacity = 1, ConsultationMode mode = ConsultationMode.InPerson) =>
        Session(new TimeOnly(9, 0), new TimeOnly(13, 0), capacity: capacity, mode: mode);

    public static ScheduleException Absence(
        ScheduleExceptionType type,
        DateOnly from,
        DateOnly to,
        TimeOnly? start = null,
        TimeOnly? end = null,
        Guid? facility = null) => new()
    {
        PractitionerId = Doctor,
        FacilityId = facility,
        Type = type,
        FromDate = from,
        ToDate = to,
        StartTime = start,
        EndTime = end,
        Reason = type == ScheduleExceptionType.Leave ? "Medical conference" : "Surgery list",
    };

    public static ScheduleException ExtraSession(DateOnly date, TimeOnly start, TimeOnly end, int slotMinutes = 30, ConsultationMode mode = ConsultationMode.Both) => new()
    {
        PractitionerId = Doctor,
        FacilityId = Facility,
        Type = ScheduleExceptionType.ExtraSession,
        FromDate = date,
        ToDate = date,
        StartTime = start,
        EndTime = end,
        Reason = "Sunday health camp",
        DepartmentId = Department,
        SlotMinutes = slotMinutes,
        CapacityPerSlot = 1,
        Mode = mode,
    };

    /// <summary>The UTC instant of a wall-clock time in IST.</summary>
    public static DateTimeOffset Ist(DateOnly date, int hour, int minute = 0) => Calendar.ToUtc(date, new TimeOnly(hour, minute));

    /// <summary>A query that does not hide anything as "past" (NotBefore a week earlier).</summary>
    public static SlotQuery Query(DateOnly from, DateOnly? to = null) => new(from, to ?? from, Ist(from.AddDays(-7), 0));

    public static IReadOnlyList<Slot> Compute(
        IReadOnlyCollection<DoctorSchedule> schedules,
        SlotQuery query,
        IReadOnlyCollection<ScheduleException>? exceptions = null,
        IReadOnlyCollection<BusyInterval>? busy = null) =>
        SlotEngine.Compute(schedules, exceptions ?? [], busy ?? [], query, Calendar);

    public static Appointment Booked(
        DateTimeOffset startsAt,
        AppointmentChannel channel = AppointmentChannel.Phone,
        bool telehealth = false,
        string number = "NHAP260000001",
        DateTimeOffset? bookedAt = null) =>
        Appointment.Book(
            new AppointmentBooking(
                Facility, Patient, Doctor, Department, null, startsAt, startsAt.AddMinutes(15), 1, channel, AppointmentVisitType.New,
                telehealth, "OPD-01", 600m, "Fever", null, null),
            number,
            bookedAt ?? startsAt.AddDays(-2));
}
