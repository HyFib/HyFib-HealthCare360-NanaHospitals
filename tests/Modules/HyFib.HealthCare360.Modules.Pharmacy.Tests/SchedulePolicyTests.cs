using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Tests;

/// <summary>Drugs &amp; Cosmetics Rules at the OTC counter.</summary>
public sealed class SchedulePolicyTests
{
    private static readonly SaleCandidate Paracetamol = new("Paracetamol 500 mg", DrugSchedule.Otc, false);
    private static readonly SaleCandidate Cetirizine = new("Cetirizine 10 mg", DrugSchedule.None, false);
    private static readonly SaleCandidate Amoxicillin = new("Amoxicillin 500 mg", DrugSchedule.H, false);
    private static readonly SaleCandidate Cefixime = new("Cefixime 200 mg", DrugSchedule.H1, false);
    private static readonly SaleCandidate Morphine = new("Morphine 10 mg/ml", DrugSchedule.X, true);
    private static readonly SaleCandidate Tramadol = new("Tramadol 50 mg", DrugSchedule.H1, true);

    [Fact]
    public void Otc_and_unscheduled_items_sell_without_a_prescription()
    {
        SchedulePolicy.CheckCounterSale([Paracetamol, Cetirizine], hasPrescription: false, hasBuyerIdentity: false).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(DrugSchedule.H, false)]
    [InlineData(DrugSchedule.H1, false)]
    [InlineData(DrugSchedule.X, false)]
    [InlineData(DrugSchedule.G, true)]
    [InlineData(DrugSchedule.None, true)]
    public void Schedule_h_h1_and_x_are_blocked_without_a_prescription(DrugSchedule schedule, bool allowed)
    {
        var result = SchedulePolicy.CheckCounterSale([Paracetamol, new SaleCandidate("Medicine", schedule, false)], hasPrescription: false, hasBuyerIdentity: true);

        result.IsSuccess.ShouldBe(allowed);
        if (!allowed)
        {
            result.Error!.Code.ShouldBe("pharmacy.prescription_required");
            result.Error.Message.ShouldContain("Medicine");
        }
    }

    [Fact]
    public void A_controlled_drug_needs_a_prescription_even_without_a_schedule()
    {
        SchedulePolicy.CheckCounterSale([new SaleCandidate("NDPS drug", DrugSchedule.None, true)], false, true)
            .Error!.Code.ShouldBe("pharmacy.prescription_required");
    }

    [Fact]
    public void An_outside_prescription_allows_schedule_h()
    {
        SchedulePolicy.CheckCounterSale([Amoxicillin], hasPrescription: true, hasBuyerIdentity: false).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Register_items_also_need_the_buyer_identity()
    {
        SchedulePolicy.CheckCounterSale([Cefixime], hasPrescription: true, hasBuyerIdentity: false).Error!.Code.ShouldBe("pharmacy.buyer_required");
        SchedulePolicy.CheckCounterSale([Cefixime, Morphine], hasPrescription: true, hasBuyerIdentity: true).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Controlled_medicines_need_an_explicit_confirmation()
    {
        var result = SchedulePolicy.CheckControlledConfirmation([Paracetamol, Tramadol, Morphine], confirmed: false);

        result.Error!.Code.ShouldBe("pharmacy.controlled_confirmation_required");
        result.Error.Message.ShouldContain("Tramadol 50 mg");
        SchedulePolicy.CheckControlledConfirmation([Paracetamol, Tramadol], confirmed: true).IsSuccess.ShouldBeTrue();
        SchedulePolicy.CheckControlledConfirmation([Paracetamol, Amoxicillin], confirmed: false).IsSuccess.ShouldBeTrue();
    }
}
