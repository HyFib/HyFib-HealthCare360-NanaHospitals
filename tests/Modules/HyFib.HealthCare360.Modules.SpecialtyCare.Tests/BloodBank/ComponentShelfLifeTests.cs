using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;
using HyFib.HealthCare360.Modules.SpecialtyCare.Tests.Support;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Tests.BloodBank;

public sealed class ComponentShelfLifeTests
{
    [Theory]
    [InlineData(BloodComponentType.WholeBlood, 35)]
    [InlineData(BloodComponentType.PRBC, 35)]
    [InlineData(BloodComponentType.FFP, 365)]
    [InlineData(BloodComponentType.PlateletConcentrate, 5)]
    [InlineData(BloodComponentType.Cryoprecipitate, 365)]
    public void Shelf_life_matches_storage_standards(BloodComponentType component, int days)
    {
        ComponentShelfLife.Days(component).ShouldBe(days);
        ComponentShelfLife.ExpiryFor(component, TestData.Now).ShouldBe(TestData.Now.AddDays(days));
    }

    [Fact]
    public void Prepared_unit_inherits_bag_number_group_and_expiry_and_starts_in_quarantine()
    {
        var donation = new Donation { BagNumber = "NHBB26000123", VolumeMl = 450, CollectedAt = TestData.Now, BloodGroup = BloodGroup.BPositive };

        var unit = BloodComponent.Prepare(donation, BloodComponentType.PlateletConcentrate, null, "Agitator 1", TestData.Now.AddHours(2));

        unit.UnitNumber.ShouldBe("NHBB26000123-P");
        unit.Status.ShouldBe(BloodUnitStatus.Quarantine);
        unit.ExpiresAt.ShouldBe(TestData.Now.AddDays(5), "expiry counts from collection, not preparation");
        unit.VolumeMl.ShouldBe(60);
        unit.Tti.ShouldNotBeSameAs(donation.Tti);
    }

    [Theory]
    [InlineData(BloodBagType.Single, new[] { BloodComponentType.WholeBlood }, true)]
    [InlineData(BloodBagType.Single, new[] { BloodComponentType.PRBC }, false)]
    [InlineData(BloodBagType.Double, new[] { BloodComponentType.PRBC, BloodComponentType.FFP }, true)]
    [InlineData(BloodBagType.Double, new[] { BloodComponentType.PRBC, BloodComponentType.FFP, BloodComponentType.PlateletConcentrate }, false)]
    [InlineData(BloodBagType.Triple, new[] { BloodComponentType.PRBC, BloodComponentType.FFP, BloodComponentType.PlateletConcentrate }, true)]
    [InlineData(BloodBagType.Triple, new[] { BloodComponentType.FFP, BloodComponentType.PlateletConcentrate }, false)]
    [InlineData(BloodBagType.Triple, new[] { BloodComponentType.WholeBlood, BloodComponentType.FFP }, false)]
    [InlineData(BloodBagType.Quadruple, new[] { BloodComponentType.PRBC, BloodComponentType.FFP, BloodComponentType.PlateletConcentrate, BloodComponentType.Cryoprecipitate }, true)]
    [InlineData(BloodBagType.Triple, new[] { BloodComponentType.PRBC, BloodComponentType.PRBC }, false)]
    public void Separation_plan_must_fit_the_bag(BloodBagType bag, BloodComponentType[] components, bool valid) =>
        ComponentShelfLife.ValidateSeparation(bag, components, TestData.Now, TestData.Now.AddHours(2)).IsSuccess.ShouldBe(valid);

    [Fact]
    public void Plasma_and_platelets_must_be_separated_within_eight_hours()
    {
        BloodComponentType[] plan = [BloodComponentType.PRBC, BloodComponentType.FFP];

        ComponentShelfLife.ValidateSeparation(BloodBagType.Double, plan, TestData.Now, TestData.Now.AddHours(8)).IsSuccess.ShouldBeTrue();
        ComponentShelfLife.ValidateSeparation(BloodBagType.Double, plan, TestData.Now, TestData.Now.AddHours(9)).Error!.Code.ShouldBe("bloodbank.separation_too_late");
        ComponentShelfLife.ValidateSeparation(BloodBagType.Single, [BloodComponentType.WholeBlood], TestData.Now, TestData.Now.AddDays(1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Donation_can_be_separated_only_once() =>
        new Donation { Status = DonationStatus.Separated }.EnsureCanSeparate().Error!.Code.ShouldBe("bloodbank.already_separated");

    [Fact]
    public void Donation_lab_results_are_final_once_complete()
    {
        var donation = new Donation();
        var complete = new TtiPanel { Hiv = TtiResult.NonReactive, HbsAg = TtiResult.NonReactive, Hcv = TtiResult.NonReactive, Syphilis = TtiResult.NonReactive, Malaria = TtiResult.NonReactive };

        donation.RecordLabResults(BloodGroup.Unknown, complete, TestData.Now).IsFailure.ShouldBeTrue();
        donation.RecordLabResults(BloodGroup.ONegative, complete, TestData.Now).IsSuccess.ShouldBeTrue();
        donation.RecordLabResults(BloodGroup.ONegative, complete, TestData.Now).Error!.Code.ShouldBe("bloodbank.results_final");
    }

    [Fact]
    public void Reactive_donation_is_discarded() =>
        ReactiveDonation().Status.ShouldBe(DonationStatus.Discarded);

    private static Donation ReactiveDonation()
    {
        var donation = new Donation();
        donation.RecordLabResults(BloodGroup.APositive, new TtiPanel { Hcv = TtiResult.Reactive }, TestData.Now).IsSuccess.ShouldBeTrue();
        return donation;
    }
}
