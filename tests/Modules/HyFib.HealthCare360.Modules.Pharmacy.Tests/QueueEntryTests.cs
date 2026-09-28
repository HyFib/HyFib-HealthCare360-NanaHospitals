namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

public sealed class QueueEntryTests
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    internal static PrescriptionSigned Prescription(bool inpatient = false, bool controlled = false) => new(
        Guid.CreateVersion7(), "NHRX26000042", Guid.CreateVersion7(), Guid.CreateVersion7(), inpatient ? Guid.CreateVersion7() : null,
        Guid.CreateVersion7(), Guid.CreateVersion7(), inpatient,
        [
            new PrescriptionLine(Guid.CreateVersion7(), Guid.CreateVersion7(), "Metformin 500 mg", "Metformin", "500 mg", "Tablet", "Oral", "1 tablet", "BD", 30, 60, null, false),
            new PrescriptionLine(Guid.CreateVersion7(), null, controlled ? "Tramadol 50 mg" : "Telmisartan 40 mg", null, null, "Tablet", "Oral", "1 tablet", "OD", 30, 30, null, controlled),
        ],
        Now.AddMinutes(-15));

    [Fact]
    public void A_signed_prescription_becomes_a_pending_queue_entry()
    {
        var signed = Prescription(controlled: true);

        var entry = PharmacyQueueEntry.FromPrescription(signed, Now);

        entry.Status.ShouldBe(QueueEntryStatus.Pending);
        entry.PrescriptionId.ShouldBe(signed.PrescriptionId);
        entry.FacilityId.ShouldBe(signed.FacilityId);
        entry.HasControlledDrugs.ShouldBeTrue();
        entry.Lines.Count.ShouldBe(2);
        entry.Lines[0].RequestedQuantity.ShouldBe(60);
        entry.Lines.ShouldAllBe(l => l.QueueEntryId == entry.Id);
    }

    [Fact]
    public void Partial_dispensing_keeps_the_entry_open_until_every_line_is_done()
    {
        var entry = PharmacyQueueEntry.FromPrescription(Prescription(), Now);
        var (metformin, telmisartan) = (entry.Lines[0], entry.Lines[1]);

        entry.RecordDispensed(metformin.Id, 30, Now).IsSuccess.ShouldBeTrue();
        entry.Status.ShouldBe(QueueEntryStatus.PartiallyDispensed);
        metformin.RemainingQuantity.ShouldBe(30);

        entry.RecordDispensed(metformin.Id, 31, Now).Error!.Code.ShouldBe("queue.over_dispense");
        entry.RecordDispensed(metformin.Id, 30, Now).IsSuccess.ShouldBeTrue();
        entry.RecordDispensed(telmisartan.Id, 30, Now).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(QueueEntryStatus.Dispensed);
        entry.RecordDispensed(telmisartan.Id, 1, Now).Error!.Code.ShouldBe("queue.closed");
    }

    [Fact]
    public void Closing_counts_partly_dispensed_lines_as_dispensed_and_untouched_lines_as_cancelled()
    {
        var entry = PharmacyQueueEntry.FromPrescription(Prescription(), Now);
        entry.RecordDispensed(entry.Lines[0].Id, 10, Now);

        entry.Close("Patient will buy the rest later elsewhere", Now).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(QueueEntryStatus.Dispensed);
        entry.Lines[0].Status.ShouldBe(QueueLineStatus.Dispensed);
        entry.Lines[0].RemainingQuantity.ShouldBe(0);
        entry.Lines[1].Status.ShouldBe(QueueLineStatus.Cancelled);
    }

    [Fact]
    public void An_untouched_prescription_closes_as_cancelled()
    {
        var entry = PharmacyQueueEntry.FromPrescription(Prescription(), Now);

        entry.Close("Patient left", Now).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(QueueEntryStatus.Cancelled);
        entry.Close("again", Now).Error!.Code.ShouldBe("queue.closed");
    }

    [Fact]
    public void Prescriber_cancellation_stops_further_dispensing()
    {
        var entry = PharmacyQueueEntry.FromPrescription(Prescription(), Now);
        entry.RecordDispensed(entry.Lines[0].Id, 10, Now);

        entry.CancelByPrescriber("Allergy reported", Now).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(QueueEntryStatus.Cancelled);
        entry.CloseReason.ShouldBe("Allergy reported");
        entry.Lines[0].DispensedQuantity.ShouldBe(10);
    }

    [Fact]
    public void Cancelling_the_sale_puts_the_quantity_back_and_reopens_the_entry()
    {
        var entry = PharmacyQueueEntry.FromPrescription(Prescription(), Now);
        entry.RecordDispensed(entry.Lines[0].Id, 60, Now);
        entry.RecordDispensed(entry.Lines[1].Id, 30, Now);
        entry.Status.ShouldBe(QueueEntryStatus.Dispensed);

        entry.ReverseDispensed(entry.Lines[1].Id, 30, Now).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(QueueEntryStatus.PartiallyDispensed);
        entry.Lines[1].Status.ShouldBe(QueueLineStatus.Pending);
        entry.Lines[1].RemainingQuantity.ShouldBe(30);
    }
}
