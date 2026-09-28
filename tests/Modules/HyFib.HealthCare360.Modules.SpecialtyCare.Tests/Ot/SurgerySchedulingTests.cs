using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Ot;

public sealed class SurgerySchedulingTests
{
    private static readonly DateTimeOffset Nine = TestData.Now.AddHours(-1);

    private static SurgerySlot Slot(Guid? theatre = null, Guid? surgeon = null, Guid? patient = null, Guid? anaesthetist = null, int startHour = 0, int hours = 2) =>
        new(Guid.CreateVersion7(), theatre ?? Guid.CreateVersion7(), surgeon ?? Guid.CreateVersion7(), patient ?? Guid.CreateVersion7(), anaesthetist,
            Nine.AddHours(startHour), Nine.AddHours(startHour + hours));

    [Theory]
    [InlineData(0, 2, 1, 2, true)]   // 09–11 vs 10–12
    [InlineData(0, 2, 2, 2, false)]  // back-to-back 09–11 then 11–13 is allowed
    [InlineData(2, 2, 0, 2, false)]  // back-to-back the other way round
    [InlineData(0, 4, 1, 1, true)]   // fully contained
    [InlineData(0, 1, 3, 1, false)]  // far apart
    public void Overlap_uses_half_open_intervals(int aStart, int aHours, int bStart, int bHours, bool expected) =>
        SurgeryScheduling.Overlaps(Nine.AddHours(aStart), Nine.AddHours(aStart + aHours), Nine.AddHours(bStart), Nine.AddHours(bStart + bHours))
            .ShouldBe(expected);

    [Fact]
    public void Same_theatre_at_overlapping_time_is_a_theatre_conflict()
    {
        var theatre = Guid.CreateVersion7();
        var existing = Slot(theatre: theatre);
        var conflicts = SurgeryScheduling.FindConflicts(Slot(theatre: theatre, startHour: 1), [existing]);
        conflicts.ShouldHaveSingleItem().Kind.ShouldBe(ConflictKind.Theatre);
        conflicts[0].BookingId.ShouldBe(existing.BookingId);
    }

    [Fact]
    public void Same_surgeon_in_another_theatre_at_overlapping_time_is_a_surgeon_conflict()
    {
        var surgeon = Guid.CreateVersion7();
        SurgeryScheduling.FindConflicts(Slot(surgeon: surgeon, startHour: 1), [Slot(surgeon: surgeon)])
            .ShouldHaveSingleItem().Kind.ShouldBe(ConflictKind.Surgeon);
    }

    [Fact]
    public void Same_anaesthetist_and_same_patient_are_conflicts_too()
    {
        var anaesthetist = Guid.CreateVersion7();
        var patient = Guid.CreateVersion7();
        var conflicts = SurgeryScheduling.FindConflicts(Slot(patient: patient, anaesthetist: anaesthetist), [Slot(patient: patient, anaesthetist: anaesthetist, startHour: 1)]);
        conflicts.Select(c => c.Kind).ShouldBe([ConflictKind.Anaesthetist, ConflictKind.Patient], ignoreOrder: true);
    }

    [Fact]
    public void Back_to_back_cases_in_the_same_theatre_by_the_same_surgeon_do_not_conflict()
    {
        var theatre = Guid.CreateVersion7();
        var surgeon = Guid.CreateVersion7();
        SurgeryScheduling.FindConflicts(Slot(theatre, surgeon, startHour: 2), [Slot(theatre, surgeon)]).ShouldBeEmpty();
    }

    [Fact]
    public void Rescheduling_a_booking_ignores_its_own_slot()
    {
        var existing = Slot();
        var moved = existing with { Start = existing.Start.AddMinutes(30), End = existing.End.AddMinutes(30) };
        SurgeryScheduling.FindConflicts(moved, [existing]).ShouldBeEmpty();
    }

    [Fact]
    public void Unrelated_bookings_at_the_same_time_do_not_conflict() =>
        SurgeryScheduling.FindConflicts(Slot(), [Slot(), Slot(startHour: 1)]).ShouldBeEmpty();

    [Fact]
    public void Only_confirmed_slots_block_the_theatre()
    {
        SurgeryScheduling.IsBlocking(SurgeryStatus.Scheduled).ShouldBeTrue();
        SurgeryScheduling.IsBlocking(SurgeryStatus.InPreOp).ShouldBeTrue();
        SurgeryScheduling.IsBlocking(SurgeryStatus.InProgress).ShouldBeTrue();
        SurgeryScheduling.IsBlocking(SurgeryStatus.Requested).ShouldBeFalse();
        SurgeryScheduling.IsBlocking(SurgeryStatus.Postponed).ShouldBeFalse();
        SurgeryScheduling.IsBlocking(SurgeryStatus.Cancelled).ShouldBeFalse();
    }

    [Fact]
    public void Slot_must_end_after_it_starts_and_last_at_most_a_day()
    {
        SurgeryScheduling.ValidateWindow(Nine, Nine).IsFailure.ShouldBeTrue();
        SurgeryScheduling.ValidateWindow(Nine, Nine.AddHours(-1)).IsFailure.ShouldBeTrue();
        SurgeryScheduling.ValidateWindow(Nine, Nine.AddHours(25)).IsFailure.ShouldBeTrue();
        SurgeryScheduling.ValidateWindow(Nine, Nine.AddHours(3)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Conflict_description_names_every_clash()
    {
        var text = SurgeryScheduling.Describe(
        [
            new SchedulingConflict(Guid.CreateVersion7(), ConflictKind.Theatre, Nine, Nine.AddHours(1)),
            new SchedulingConflict(Guid.CreateVersion7(), ConflictKind.Surgeon, Nine, Nine.AddHours(1)),
        ]);
        text.ShouldContain("theatre");
        text.ShouldContain("surgeon");
    }
}
