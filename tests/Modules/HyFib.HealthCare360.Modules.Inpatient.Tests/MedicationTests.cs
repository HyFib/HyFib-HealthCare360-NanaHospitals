using System.Globalization;
using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class MedicationTests
{
    private static DateTimeOffset At(int day, int hour, int minute = 0) => IndiaClock.AtLocal(new DateOnly(2026, 9, day), new TimeOnly(hour, minute));

    [Theory]
    [InlineData("OD", "09:00")]
    [InlineData("BD", "09:00,21:00")]
    [InlineData("b.d.", "09:00,21:00")]
    [InlineData("TDS", "08:00,14:00,20:00")]
    [InlineData("tid", "08:00,14:00,20:00")]
    [InlineData("QID", "06:00,12:00,18:00,22:00")]
    [InlineData("HS", "22:00")]
    [InlineData("Once daily", "09:00")]
    [InlineData("1-0-1", "08:00,20:00")]
    [InlineData("0-0-1", "20:00")]
    [InlineData("1-1-1", "08:00,14:00,20:00")]
    [InlineData("½-0-½", "08:00,20:00")]
    [InlineData("1-1-1-1", "06:00,12:00,18:00,22:00")]
    [InlineData("Q8H", "06:00,14:00,22:00")]
    [InlineData("q6h", "00:00,06:00,12:00,18:00")]
    [InlineData("Q4H", "02:00,06:00,10:00,14:00,18:00,22:00")]
    [InlineData("12 hourly", "08:00,20:00")]
    [InlineData("SOS", "")]
    [InlineData("PRN", "")]
    [InlineData("As directed", "")]
    [InlineData("STAT", "")]
    [InlineData("weekly", "")]
    [InlineData("", "")]
    public void Frequencies_map_to_ward_medication_rounds(string frequency, string expected) =>
        string.Join(',', DoseSchedule.TimesOfDay(frequency).Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture))).ShouldBe(expected);

    [Fact]
    public void A_schedule_runs_across_midnight()
    {
        var doses = DoseSchedule.Between("BD", At(25, 10), At(26, 10));

        doses.ShouldBe([At(25, 21), At(26, 9)]);
    }

    [Fact]
    public void A_schedule_stops_at_the_end_of_the_order() =>
        DoseSchedule.Between("TDS", At(25, 7), At(26, 7), orderEnd: At(25, 15)).ShouldBe([At(25, 8), At(25, 14)]);

    [Fact]
    public void A_schedule_never_starts_before_the_order() =>
        DoseSchedule.Between("QID", At(25, 0), At(26, 0), orderStart: At(25, 13)).ShouldBe([At(25, 18), At(25, 22)]);

    [Fact]
    public void Stat_is_a_single_dose_now()
    {
        DoseSchedule.IsImmediate("stat").ShouldBeTrue();
        DoseSchedule.Between("STAT", At(25, 10, 17), At(26, 10, 17)).ShouldBe([At(25, 10, 17)]);
    }

    [Fact]
    public void As_needed_medicines_are_never_scheduled()
    {
        DoseSchedule.IsAsNeeded("s.o.s").ShouldBeTrue();
        DoseSchedule.Between("SOS", At(25, 10), At(26, 10)).ShouldBeEmpty();
        new MedicationOrder { Frequency = "PRN" }.IsAsNeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_given_dose_records_the_time_and_the_nurse()
    {
        var dose = Due(At(25, 9));
        var nurse = Guid.NewGuid();

        dose.Record(Given(), nurse, "Sr. Anitha", requiresWitness: false, At(25, 9, 10)).IsSuccess.ShouldBeTrue();

        dose.Status.ShouldBe(MarStatus.Given);
        dose.AdministeredAt.ShouldBe(At(25, 9, 10));
        dose.RecordedBy.ShouldBe(nurse);
        dose.RecordedByName.ShouldBe("Sr. Anitha");
    }

    [Theory]
    [InlineData(MarStatus.Held)]
    [InlineData(MarStatus.Refused)]
    [InlineData(MarStatus.Missed)]
    public void A_dose_not_given_needs_a_reason(MarStatus outcome)
    {
        var dose = Due(At(25, 9));

        dose.Record(new MarOutcome(outcome, null, " ", null, null, null), null, null, false, At(25, 9)).Error!.Code.ShouldBe("mar.reason_required");
        dose.Record(new MarOutcome(outcome, null, "NPO for surgery", null, null, null), null, null, false, At(25, 9)).IsSuccess.ShouldBeTrue();
        dose.AdministeredAt.ShouldBeNull();
        dose.Reason.ShouldBe("NPO for surgery");
    }

    [Fact]
    public void Controlled_drugs_need_a_witness()
    {
        var dose = Due(At(25, 9));

        dose.Record(Given(), null, null, requiresWitness: true, At(25, 9)).Error!.Code.ShouldBe("mar.witness_required");
        dose.Record(Given() with { WitnessName = "Sr. Mary" }, null, null, requiresWitness: true, At(25, 9)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_dose_is_recorded_only_once()
    {
        var dose = Due(At(25, 9));
        dose.Record(Given(), null, null, false, At(25, 9));

        dose.Record(Given(), null, null, false, At(25, 9, 5)).Error!.Code.ShouldBe("mar.already_recorded");
    }

    [Fact]
    public void Administration_cannot_be_documented_in_the_future() =>
        Due(At(25, 9)).Record(Given() with { AdministeredAt = At(25, 11) }, null, null, false, At(25, 9)).Error!.Code.ShouldBe("mar.future");

    [Fact]
    public void Due_and_cancelled_are_not_outcomes() =>
        Due(At(25, 9)).Record(new MarOutcome(MarStatus.Cancelled, null, "x", null, null, null), null, null, false, At(25, 9)).Error!.Code.ShouldBe("mar.invalid_outcome");

    [Fact]
    public void A_due_dose_is_overdue_after_the_grace_period()
    {
        var dose = Due(At(25, 9));
        var grace = TimeSpan.FromMinutes(30);

        dose.IsOverdue(At(25, 9, 30), grace).ShouldBeFalse();
        dose.IsOverdue(At(25, 9, 31), grace).ShouldBeTrue();
        dose.Record(Given(), null, null, false, At(25, 9, 45));
        dose.IsOverdue(At(25, 12), grace).ShouldBeFalse();
    }

    [Fact]
    public void Stopping_an_order_cancels_only_doses_still_due()
    {
        var order = new MedicationOrder { Status = MedicationOrderStatus.Active, Frequency = "BD" };
        var given = Due(At(25, 9));
        given.Record(Given(), null, null, false, At(25, 9));
        var pending = Due(At(25, 21));

        order.Stop(null, "Course completed", At(25, 12)).IsSuccess.ShouldBeTrue();
        given.CancelIfDue("Medication order stopped");
        pending.CancelIfDue("Medication order stopped");

        given.Status.ShouldBe(MarStatus.Given);
        pending.Status.ShouldBe(MarStatus.Cancelled);
        order.Stop(null, "again", At(25, 13)).Error!.Code.ShouldBe("medication.not_active");
    }

    [Theory]
    [InlineData(8, 0, NursingShift.Morning)]
    [InlineData(13, 59, NursingShift.Morning)]
    [InlineData(14, 0, NursingShift.Evening)]
    [InlineData(19, 59, NursingShift.Evening)]
    [InlineData(20, 0, NursingShift.Night)]
    [InlineData(2, 0, NursingShift.Night)]
    [InlineData(7, 59, NursingShift.Night)]
    public void Nursing_shifts_follow_ist_wall_clock(int hour, int minute, NursingShift expected) =>
        NursingShifts.At(At(25, hour, minute)).ShouldBe(expected);

    [Fact]
    public void Work_after_midnight_belongs_to_the_previous_night_shift()
    {
        NursingShifts.DutyDate(At(26, 2)).ShouldBe(new DateOnly(2026, 9, 25));
        NursingShifts.DutyDate(At(26, 8)).ShouldBe(new DateOnly(2026, 9, 26));
    }

    [Fact]
    public void A_note_written_in_a_later_shift_is_a_late_entry()
    {
        NursingNote.IsLate(At(25, 10), At(25, 11)).ShouldBeFalse();
        NursingNote.IsLate(At(25, 10), At(25, 15)).ShouldBeTrue();
        NursingNote.IsLate(At(25, 23), At(26, 3)).ShouldBeFalse();
    }

    [Fact]
    public void A_handover_is_acknowledged_by_the_receiving_nurse_only_once()
    {
        var outgoing = Guid.NewGuid();
        var handover = new ShiftHandover { HandedOverBy = outgoing, Situation = "Post-op day 1, stable" };

        handover.Acknowledge(outgoing, "Sr. A", At(25, 14)).Error!.Code.ShouldBe("handover.self");
        handover.Acknowledge(Guid.NewGuid(), "Sr. B", At(25, 14)).IsSuccess.ShouldBeTrue();
        handover.Acknowledge(Guid.NewGuid(), "Sr. C", At(25, 14, 5)).Error!.Code.ShouldBe("handover.acknowledged");
    }

    private static MedicationAdministration Due(DateTimeOffset scheduledAt) =>
        new() { DrugName = "Inj. Ceftriaxone", Dose = "1 g", Route = "IV", ScheduledAt = scheduledAt, Status = MarStatus.Due };

    private static MarOutcome Given() => new(MarStatus.Given, null, null, "B12345", null, null);
}
