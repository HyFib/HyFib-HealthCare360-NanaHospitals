using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

public sealed class BedTests
{
    private static readonly DateTimeOffset Now = IndiaClock.AtLocal(new DateOnly(2026, 9, 25), new TimeOnly(10, 0));

    private static Bed NewBed(BedStatus status = BedStatus.Vacant) =>
        new() { FacilityId = Guid.NewGuid(), WardId = Guid.NewGuid(), Label = "GWM-01", Status = status };

    [Fact]
    public void Occupying_a_vacant_bed_links_the_admission()
    {
        var bed = NewBed();
        var admissionId = Guid.NewGuid();

        bed.Occupy(admissionId, Guid.NewGuid(), Now).IsSuccess.ShouldBeTrue();

        bed.Status.ShouldBe(BedStatus.Occupied);
        bed.CurrentAdmissionId.ShouldBe(admissionId);
        bed.StatusChangedAt.ShouldBe(Now);
    }

    [Fact]
    public void Releasing_sends_the_bed_for_cleaning_and_unlinks_the_admission()
    {
        var bed = NewBed();
        bed.Occupy(Guid.NewGuid(), Guid.NewGuid(), Now);

        bed.Release("Cleaning after discharge", Now.AddDays(2)).IsSuccess.ShouldBeTrue();

        bed.Status.ShouldBe(BedStatus.Cleaning);
        bed.CurrentAdmissionId.ShouldBeNull();
        bed.IsAvailableFor(Guid.NewGuid(), Now.AddDays(2)).ShouldBeFalse();
    }

    [Fact]
    public void A_bed_that_is_not_occupied_cannot_be_released() =>
        NewBed().Release("x", Now).Error!.Code.ShouldBe("bed.not_occupied");

    [Fact]
    public void Completing_cleaning_returns_the_bed_to_service()
    {
        var bed = NewBed(BedStatus.Cleaning);

        bed.CompleteCleaning(Now).IsSuccess.ShouldBeTrue();

        bed.Status.ShouldBe(BedStatus.Vacant);
        bed.IsAvailableFor(Guid.NewGuid(), Now).ShouldBeTrue();
    }

    [Fact]
    public void A_reserved_bed_is_held_for_its_patient_until_the_reservation_lapses()
    {
        var bed = NewBed();
        var reservedFor = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();
        bed.Reserve(reservedFor, Now.AddHours(4), "Elective admission", Now).IsSuccess.ShouldBeTrue();

        bed.IsAvailableFor(reservedFor, Now.AddHours(1)).ShouldBeTrue();
        bed.IsAvailableFor(someoneElse, Now.AddHours(1)).ShouldBeFalse();
        bed.Occupy(Guid.NewGuid(), someoneElse, Now.AddHours(1)).Error!.Code.ShouldBe("bed.reserved");

        bed.IsReservationExpired(Now.AddHours(5)).ShouldBeTrue();
        bed.Occupy(Guid.NewGuid(), someoneElse, Now.AddHours(5)).IsSuccess.ShouldBeTrue();
        bed.ReservedForPatientId.ShouldBeNull();
    }

    [Fact]
    public void A_reservation_must_end_in_the_future() =>
        NewBed().Reserve(Guid.NewGuid(), Now.AddMinutes(-1), null, Now).Error!.Code.ShouldBe("bed.reservation_past");

    [Fact]
    public void Cancelling_a_reservation_frees_the_bed()
    {
        var bed = NewBed();
        bed.Reserve(Guid.NewGuid(), Now.AddHours(2), null, Now);

        bed.CancelReservation(Now).IsSuccess.ShouldBeTrue();

        bed.Status.ShouldBe(BedStatus.Vacant);
        bed.ReservedUntil.ShouldBeNull();
    }

    [Fact]
    public void A_bed_out_of_service_cannot_be_occupied()
    {
        var bed = NewBed();
        bed.IsActive = false;

        bed.Occupy(Guid.NewGuid(), Guid.NewGuid(), Now).Error!.Code.ShouldBe("bed.inactive");
    }

    [Fact]
    public void Blocking_records_the_reason_shown_on_the_board()
    {
        var bed = NewBed();

        bed.Block("  Ceiling leak  ", Now).IsSuccess.ShouldBeTrue();

        bed.Status.ShouldBe(BedStatus.Blocked);
        bed.StatusReason.ShouldBe("Ceiling leak");
        bed.Unblock(Now).IsSuccess.ShouldBeTrue();
        bed.StatusReason.ShouldBeNull();
    }

    [Fact]
    public void A_repaired_bed_goes_for_cleaning_before_use()
    {
        var bed = NewBed();
        bed.StartMaintenance("Side rail broken", Now);

        bed.EndMaintenance(Now.AddHours(3)).IsSuccess.ShouldBeTrue();

        bed.Status.ShouldBe(BedStatus.Cleaning);
    }

    [Fact]
    public void Housekeeping_turnaround_is_measured_from_request_to_completion()
    {
        var task = new HousekeepingTask { RequestedAt = Now, Status = HousekeepingStatus.Pending };

        task.Start(Guid.NewGuid(), Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();
        task.Complete(Guid.NewGuid(), "Linen changed", Now.AddMinutes(45)).IsSuccess.ShouldBeTrue();

        task.Status.ShouldBe(HousekeepingStatus.Completed);
        task.TurnaroundMinutes.ShouldBe(45);
        task.StartedAt.ShouldBe(Now.AddMinutes(10));
    }

    [Fact]
    public void Housekeeping_can_be_completed_without_an_explicit_start()
    {
        var task = new HousekeepingTask { RequestedAt = Now, Status = HousekeepingStatus.Pending };

        task.Complete(null, null, Now.AddMinutes(30)).IsSuccess.ShouldBeTrue();

        task.StartedAt.ShouldBe(Now.AddMinutes(30));
        task.TurnaroundMinutes.ShouldBe(30);
    }

    [Fact]
    public void A_closed_housekeeping_task_cannot_be_completed_again()
    {
        var task = new HousekeepingTask { RequestedAt = Now, Status = HousekeepingStatus.Pending };
        task.Cancel("Bed sent for maintenance");

        task.Complete(null, null, Now).Error!.Code.ShouldBe("housekeeping.closed");
        task.Start(null, Now).Error!.Code.ShouldBe("housekeeping.not_pending");
    }
}
