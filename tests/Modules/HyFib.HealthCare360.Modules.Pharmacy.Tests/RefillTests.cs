using HyFib.HealthCare360.Modules.Pharmacy.Services;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

public sealed class RefillTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly DateTimeOffset Now = QueueEntryTests.Now;

    private static PharmacyQueueEntry DispensedEntry(bool controlled = false)
    {
        var entry = PharmacyQueueEntry.FromPrescription(QueueEntryTests.Prescription(controlled: controlled), Now);
        foreach (var line in entry.Lines)
        {
            entry.RecordDispensed(line.Id, line.RequestedQuantity, Now).IsSuccess.ShouldBeTrue();
        }

        return entry;
    }

    [Fact]
    public void Each_approved_refill_uses_one_authorisation_and_queues_the_full_prescription()
    {
        var entry = DispensedEntry();
        entry.AuthorizeRefills(2, Today.AddMonths(3), "Dr. Mehta, OPD review", Today).IsSuccess.ShouldBeTrue();

        var first = entry.CreateRefill(Today, Now).Value;
        var second = entry.CreateRefill(Today, Now).Value;

        entry.RefillsUsed.ShouldBe(2);
        first.RefillOfEntryId.ShouldBe(entry.Id);
        first.PrescriptionNumber.ShouldBe("NHRX26000042/R1");
        second.PrescriptionNumber.ShouldBe("NHRX26000042/R2");
        first.Status.ShouldBe(QueueEntryStatus.Pending);
        first.Lines.Select(l => l.RequestedQuantity).ShouldBe([60m, 30m]);
        first.Lines.ShouldAllBe(l => l.QueueEntryId == first.Id && l.DispensedQuantity == 0);
        entry.CreateRefill(Today, Now).Error!.Code.ShouldBe("refill.none_left");
    }

    [Fact]
    public void Controlled_or_in_patient_prescriptions_cannot_be_refilled()
    {
        DispensedEntry(controlled: true).AuthorizeRefills(1, Today.AddMonths(1), "note", Today).Error!.Code.ShouldBe("refill.controlled");

        var inpatient = PharmacyQueueEntry.FromPrescription(QueueEntryTests.Prescription(inpatient: true), Now);
        inpatient.AuthorizeRefills(1, Today.AddMonths(1), "note", Today).Error!.Code.ShouldBe("refill.not_refillable");
    }

    [Fact]
    public void Refills_need_a_dispensed_prescription_and_a_live_authorisation()
    {
        var pending = PharmacyQueueEntry.FromPrescription(QueueEntryTests.Prescription(), Now);
        pending.AuthorizeRefills(1, Today.AddMonths(1), "note", Today).IsSuccess.ShouldBeTrue();
        pending.EnsureRefillable(Today).Error!.Code.ShouldBe("refill.not_dispensed");

        var expired = DispensedEntry();
        expired.AuthorizeRefills(1, Today.AddDays(10), "note", Today).IsSuccess.ShouldBeTrue();
        expired.EnsureRefillable(Today.AddDays(11)).Error!.Code.ShouldBe("refill.expired");

        expired.AuthorizeRefills(1, Today, "note", Today).Error!.Code.ShouldBe("validation");
        expired.AuthorizeRefills(13, Today.AddDays(30), "note", Today).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_cancelled_prescription_loses_its_remaining_refills()
    {
        var entry = DispensedEntry();
        entry.AuthorizeRefills(3, Today.AddMonths(3), "note", Today);
        entry.CreateRefill(Today, Now);

        entry.RevokeRefills();

        entry.RefillsRemaining.ShouldBe(0);
        entry.EnsureRefillable(Today).Error!.Code.ShouldBe("refill.none_left");
    }

    [Fact]
    public void Refill_requests_are_decided_once()
    {
        var request = new RefillRequest { Number = "NHRF26000001" };

        request.Approve(Guid.CreateVersion7(), Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        request.Reject(Guid.CreateVersion7(), "late", Now).Error!.Code.ShouldBe("refill.decided");
        request.Lapse("prescription cancelled", Now);
        request.Status.ShouldBe(RefillStatus.Approved);

        var pending = new RefillRequest();
        pending.Lapse("prescription cancelled", Now);
        pending.Status.ShouldBe(RefillStatus.Cancelled);
    }

    [Fact]
    public void Supply_runs_out_after_the_longest_course_from_the_local_dispense_date()
    {
        // 20:00 UTC on 1 September is already 2 September in India.
        RefillReminderJob.SupplyEndsOn(new DateTimeOffset(2026, 9, 1, 20, 0, 0, TimeSpan.Zero), 30).ShouldBe(new DateOnly(2026, 10, 2));
        RefillReminderJob.SupplyEndsOn(new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero), 0).ShouldBe(new DateOnly(2026, 9, 2));
    }
}
