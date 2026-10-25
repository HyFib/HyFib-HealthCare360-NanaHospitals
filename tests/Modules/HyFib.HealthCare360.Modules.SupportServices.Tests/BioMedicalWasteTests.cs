using HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>A bag of bio-medical waste: weighed sensibly, kept no more than 48 hours, handed over once, voided only before.</summary>
public sealed class BioMedicalWasteTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "weightKg")]
    [InlineData(51, "weightKg")]
    public void A_bag_is_weighed_more_than_nothing_and_at_most_50_kg(decimal kg, string field) =>
        WasteBag.Validate(kg, Now, Now).Error!.Fields!.Keys.ShouldBe([field]);

    [Fact]
    public void A_bag_is_recorded_when_it_is_collected_not_in_the_future_or_a_week_later()
    {
        WasteBag.Validate(2.4m, Now, Now).IsSuccess.ShouldBeTrue();
        WasteBag.Validate(2.4m, Now.AddHours(1), Now).Error!.Fields!.Keys.ShouldBe(["collectedAt"]);
        WasteBag.Validate(2.4m, Now.AddDays(-8), Now).Error!.Fields!.Keys.ShouldBe(["collectedAt"]);
    }

    [Fact]
    public void A_bag_kept_beyond_48_hours_is_overdue_until_it_is_handed_over()
    {
        var bag = new WasteBag { Barcode = "NHBW26-0000001", CollectedAt = Now.AddHours(-49) };
        bag.IsOverdue(Now).ShouldBeTrue();
        new WasteBag { CollectedAt = Now.AddHours(-47) }.IsOverdue(Now).ShouldBeFalse();

        bag.HandOver(Guid.NewGuid()).IsSuccess.ShouldBeTrue();
        bag.IsOverdue(Now).ShouldBeFalse();
        bag.HandOver(Guid.NewGuid()).Error!.Code.ShouldBe("bmw.bag_not_pending");
        bag.Void("Wrong label").Error!.Code.ShouldBe("bmw.bag_not_pending");
    }

    [Fact]
    public void A_bag_is_voided_with_a_reason_and_then_never_handed_over()
    {
        var bag = new WasteBag { Barcode = "NHBW26-0000002", CollectedAt = Now };
        bag.Void(" ").Error!.Fields!.Keys.ShouldBe(["reason"]);
        bag.Void(" Weighed twice ").IsSuccess.ShouldBeTrue();
        (bag.Status, bag.VoidReason).ShouldBe((WasteBagStatus.Voided, "Weighed twice"));
        bag.HandOver(Guid.NewGuid()).Error!.Code.ShouldBe("bmw.bag_not_pending");
    }
}
