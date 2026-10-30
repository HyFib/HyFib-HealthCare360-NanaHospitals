using HyFib.HealthCare360.Modules.Pharmacy.Features.Registers;
using HyFib.HealthCare360.Modules.Pharmacy.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Services;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

public sealed class InpatientPharmacyTests
{
    private static readonly DateTimeOffset Now = QueueEntryTests.Now;

    [Fact]
    public void Ward_indents_track_issued_quantities_per_line()
    {
        var indent = new WardIndent { Number = "NHWI26000001" };
        var ns = new WardIndentLine { ItemName = "Normal saline 500 ml", RequestedQuantity = 4 };
        var cannula = new WardIndentLine { ItemName = "IV cannula 20G", RequestedQuantity = 2 };
        indent.Lines.AddRange([ns, cannula]);

        indent.RecordIssued(ns.Id, 4, Now).IsSuccess.ShouldBeTrue();
        indent.Status.ShouldBe(WardIndentStatus.PartiallyIssued);
        indent.RecordIssued(cannula.Id, 3, Now).Error!.Code.ShouldBe("ward_indent.over_issue");
        indent.RecordIssued(cannula.Id, 2, Now).IsSuccess.ShouldBeTrue();

        indent.Status.ShouldBe(WardIndentStatus.Issued);
        indent.Cancel("discharged").Error!.Code.ShouldBe("ward_indent.closed");
    }

    [Fact]
    public void Cancelling_a_partly_issued_indent_keeps_what_was_issued()
    {
        var indent = new WardIndent();
        var line = new WardIndentLine { ItemName = "Ceftriaxone 1 g", RequestedQuantity = 6 };
        indent.Lines.Add(line);
        indent.RecordIssued(line.Id, 2, Now);

        indent.Cancel("Patient discharged").IsSuccess.ShouldBeTrue();

        indent.Status.ShouldBe(WardIndentStatus.Issued);
        indent.CancelReason.ShouldBe("Patient discharged");
    }

    [Fact]
    public void Ward_returns_are_credited_at_the_price_charged_and_never_exceed_what_was_issued()
    {
        var issue = new IpIssue { Number = "NHIPI26000001", PatientId = Guid.CreateVersion7(), AdmissionId = Guid.CreateVersion7(), IssuedAt = Now };
        var line = new IpIssueLine { ItemName = "Pantoprazole 40 mg", BatchNumber = "P1", Quantity = 10, UnitMrp = 10.6m, UnitPrice = 10.6m };
        issue.Lines.Add(line);

        issue.ReturnLine(line.Id, 3, Now).Value.ShouldBe(31.80m);
        issue.ReturnLine(line.Id, 8, Now).Error!.Code.ShouldBe("ward_return.exceeds_issued");
        line.ReturnedQuantity.ShouldBe(3);
        issue.LastReturnedAt.ShouldBe(Now);

        // An implant charged at its NPPA ceiling, below the MRP on its older pack, is credited at the ceiling.
        var knee = new IpIssueLine { ItemName = "Knee system", BatchNumber = "K1", Quantity = 2, UnitMrp = 60_000m, UnitPrice = 57_456m };
        issue.Lines.Add(knee);
        issue.ReturnLine(knee.Id, 1, Now).Value.ShouldBe(57_456m);
    }

    [Fact]
    public void An_implant_is_charged_at_its_ceiling_when_its_pack_says_more_and_a_device_carries_its_bill_details()
    {
        var knee = Summary(ItemType.Implant, ceiling: 57_456m);
        (InpatientPharmacyService.PriceOf(knee, 60_000m), InpatientPharmacyService.PriceOf(knee, 50_000m)).ShouldBe((57_456m, 50_000m));
        InpatientPharmacyService.PriceOf(Summary(ItemType.Consumable, ceiling: 100m), 150m).ShouldBe(150m, "Only an implant has an NPPA ceiling here.");

        var line = new IpIssueLine { ItemName = "Knee system", BatchNumber = "K1", Quantity = 1, UnitMrp = 60_000m, UnitPrice = 57_456m };
        InpatientPharmacyService.DeviceOf(knee, line).ShouldBe(new DeviceDetails("Attune", "DePuy Synthes", "Primary total knee", "K1", null, 60_000m, 57_456m));
        InpatientPharmacyService.DeviceOf(Summary(ItemType.Consumable, ceiling: null), line)!.CeilingPrice.ShouldBeNull();
        InpatientPharmacyService.DeviceOf(Summary(ItemType.Drug, ceiling: null), line).ShouldBeNull("A medicine's batch stays in its name.");
    }

    private static ItemSummary Summary(ItemType type, decimal? ceiling) => new(
        Guid.CreateVersion7(), "IMP-1", "Knee system", null, "Attune", null, null, type, DrugSchedule.None, false, false, "PCS", "9021", 5m, true,
        "DePuy Synthes", "Primary total knee", ceiling);

    [Fact]
    public void Issuing_to_the_ward_feeds_the_medication_administration_record()
    {
        var issue = new IpIssue { PatientId = Guid.CreateVersion7(), AdmissionId = Guid.CreateVersion7(), IssuedAt = Now };
        issue.Lines.Add(new IpIssueLine { ItemId = Guid.CreateVersion7(), ItemName = "Insulin regular", BatchNumber = "I7", Quantity = 1 });

        issue.MarkIssued();

        var dispensed = issue.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<MedicationDispensed>();
        dispensed.AdmissionId.ShouldBe(issue.AdmissionId);
        dispensed.Lines.ShouldHaveSingleItem().BatchNumber.ShouldBe("I7");
    }

    [Theory]
    [InlineData(ItemType.Drug, "PHARM-MED")]
    [InlineData(ItemType.Implant, "PHARM-IMPL")]
    [InlineData(ItemType.Consumable, "PHARM-CONS")]
    [InlineData(ItemType.Surgical, "PHARM-CONS")]
    public void Ip_lines_are_billed_under_the_matching_pharmacy_service(ItemType type, string code) =>
        InpatientPharmacyService.ServiceCodeFor(type).ShouldBe(code);

    [Theory]
    [InlineData("Ravi Kumar", "Ravi Kumar")]
    [InlineData("Kumar, Ravi", "\"Kumar, Ravi\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("-5", "'-5")]
    [InlineData(null, "")]
    public void Register_exports_escape_csv_and_spreadsheet_formulas(string? value, string expected) => Csv.Cell(value).ShouldBe(expected);
}
