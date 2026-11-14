using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Downtime;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>
/// Pharmacy sales written on paper during a downtime: their own numbers, the number and time carried by a back-entered
/// sale, and the prescriptions dispensed on paper that must not be offered again.
/// </summary>
public sealed class PaperSaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Clock = new FixedTime(Now);

    private static CounterSaleRequest CounterSale(PaperNumber? paper) => new(
        Guid.CreateVersion7(), null, "Walk-in", null, null, [new CounterSaleLine(Guid.CreateVersion7(), 2m, null)], 0m,
        [new SalePayment(PaymentMode.Cash, 50m, null)], false, null, paper);

    private static DispenseRequest Dispense(PaperNumber? paper) => new(
        Guid.CreateVersion7(), [new DispenseLineRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), 10m, null, null, false)], null, 0m,
        [new SalePayment(PaymentMode.Cash, 50m, null)], false, paper);

    [Fact]
    public void The_pharmacy_registers_its_invoice_series_for_downtime_sale_numbers_and_back_entry()
    {
        var services = new ServiceCollection();
        new PharmacyModule().AddServices(services, new ConfigurationBuilder().Build());

        var series = services.Select(d => d.ImplementationInstance).OfType<DowntimeNumberSeries>().Single(s => s.Kind == DowntimeNumberKind.PharmacySale);

        series.Series.ShouldBeSameAs(PharmacyNumberSeries.Invoice);
        series.Record.ShouldBe(BackEntryRecord.PharmacySale);
        BackEntryWindows.For(BackEntryRecord.PharmacySale).DefaultHours.ShouldBe(72);
    }

    [Fact]
    public void A_counter_sale_entered_from_paper_carries_a_number_and_a_time_that_are_not_in_the_future()
    {
        var validator = new CounterSaleRequestValidator(Clock);

        validator.Validate(CounterSale(null)).IsValid.ShouldBeTrue();
        validator.Validate(CounterSale(new PaperNumber("NHPH260000007", Now.AddHours(-9)))).IsValid.ShouldBeTrue();
        validator.Validate(CounterSale(new PaperNumber("NHPH260000007", Now.AddHours(2)))).Errors
            .ShouldContain(e => e.PropertyName == "paper.At" && e.ErrorMessage == "The time on the paper cannot be in the future.");
        validator.Validate(CounterSale(new PaperNumber("", Now.AddHours(-9)))).IsValid.ShouldBeFalse();
        validator.Validate(CounterSale(new PaperNumber("NH PH 7", Now.AddHours(-9)))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_dispensing_entered_from_paper_carries_the_same_number_and_time()
    {
        var validator = new DispenseRequestValidator(Clock);

        validator.Validate(Dispense(new PaperNumber("NHPH260000008", Now.AddHours(-3)))).IsValid.ShouldBeTrue();
        validator.Validate(Dispense(new PaperNumber("NHPH260000008", Now.AddDays(1)))).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_sale_entered_from_paper_is_marked_and_a_sale_made_now_is_not()
    {
        new PharmacySale { InvoiceNumber = "NHPH260000009", SoldAt = Now }.IsBackEntered.ShouldBeFalse();

        var paper = new PharmacySale { InvoiceNumber = "NHPH260000010", SoldAt = Now.AddHours(-9), EnteredFromPaperAt = Now };

        paper.IsBackEntered.ShouldBeTrue();
        paper.SoldAt.ShouldBe(Now.AddHours(-9));
    }

    [Fact]
    public void A_prescription_dispensed_on_paper_is_closed_as_dispensed_and_leaves_the_open_queue()
    {
        var entry = PharmacyQueueEntry.FromPrescription(QueueEntryTests.Prescription(), Now.AddHours(-1));
        var paperAt = Now.AddHours(-8);

        entry.MarkDispensedOnPaper("NHPH260000011", paperAt, null, Now).IsSuccess.ShouldBeTrue();

        entry.IsOpen.ShouldBeFalse("the queue lists only pending and partly dispensed entries");
        entry.Status.ShouldBe(QueueEntryStatus.Dispensed);
        entry.Lines.ShouldAllBe(l => l.Status == QueueLineStatus.Dispensed && l.RemainingQuantity == 0);
        entry.DispensedOnPaperAt.ShouldBe(paperAt);
        entry.PaperSaleNumber.ShouldBe("NHPH260000011");
        entry.CloseReason.ShouldNotBeNull().ShouldContain("NHPH260000011");
        entry.RecordDispensed(entry.Lines[0].Id, 1, Now).Error!.Code.ShouldBe("queue.closed");
    }

    [Fact]
    public void A_prescription_dispensed_on_paper_without_a_sale_number_keeps_the_note_and_cannot_be_marked_twice()
    {
        var entry = PharmacyQueueEntry.FromPrescription(QueueEntryTests.Prescription(), Now.AddHours(-1));
        entry.RecordDispensed(entry.Lines[0].Id, 10, Now);

        entry.MarkDispensedOnPaper(null, Now.AddHours(-8), "Sale book 3, page 12", Now).IsSuccess.ShouldBeTrue();

        entry.Status.ShouldBe(QueueEntryStatus.Dispensed);
        entry.PaperSaleNumber.ShouldBeNull();
        entry.CloseReason.ShouldBe("Dispensed on paper during a downtime: Sale book 3, page 12");
        entry.MarkDispensedOnPaper(null, Now.AddHours(-8), "again", Now).Error!.Code.ShouldBe("queue.closed");
    }

    [Fact]
    public void A_cancelled_prescription_is_not_marked_dispensed_on_paper()
    {
        var entry = PharmacyQueueEntry.FromPrescription(QueueEntryTests.Prescription(), Now.AddHours(-1));
        entry.CancelByPrescriber("Wrong patient", Now);

        entry.MarkDispensedOnPaper(null, Now.AddHours(-2), "paper", Now).Error!.Code.ShouldBe("queue.closed");
        entry.Status.ShouldBe(QueueEntryStatus.Cancelled);
    }

    [Fact]
    public void The_dispensed_on_paper_request_needs_a_time_that_is_not_in_the_future_and_a_sale_number_or_a_note()
    {
        var validator = new DispensedOnPaperRequestValidator(Clock);

        validator.Validate(new DispensedOnPaperRequest(Now.AddHours(-5), "NHPH260000012", null)).IsValid.ShouldBeTrue();
        validator.Validate(new DispensedOnPaperRequest(Now.AddHours(-5), null, "Sale book 3, page 12")).IsValid.ShouldBeTrue();
        validator.Validate(new DispensedOnPaperRequest(Now.AddHours(-5), null, null)).IsValid.ShouldBeFalse();
        validator.Validate(new DispensedOnPaperRequest(Now.AddHours(3), "NHPH260000012", null)).Errors
            .ShouldContain(e => e.ErrorMessage == "The time on the paper cannot be in the future.");
        validator.Validate(new DispensedOnPaperRequest(Now.AddHours(-5), "NH PH 12", null)).IsValid.ShouldBeFalse();
    }
}
