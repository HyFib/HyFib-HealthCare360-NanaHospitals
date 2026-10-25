using HyFib.HealthCare360.Modules.SupportServices.Domain.Linen;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

/// <summary>Linen counted from place to place, and infected linen washed only by thermal disinfection.</summary>
public sealed class LinenTests
{
    [Fact]
    public void A_place_gives_up_no_more_linen_than_it_holds()
    {
        var store = new LinenHolding { Location = LinenLocation.Store, Quantity = 40 };
        store.Take(50, "bed sheets", "store").Error!.Code.ShouldBe("linen.short");
        store.Take(0, "bed sheets", "store").Error!.Fields!.Keys.ShouldContain("lines");
        store.Take(30, "bed sheets", "store").IsSuccess.ShouldBeTrue();
        store.Quantity.ShouldBe(10);
    }

    [Theory]
    [InlineData(71, 3, true)]
    [InlineData(65, 10, true)]
    [InlineData(70, 9, false)]
    [InlineData(65, 9, false)]
    [InlineData(90, 2, false)]
    public void Thermal_disinfection_holds_71_degrees_for_3_minutes_or_65_for_10(int temperature, int minutes, bool disinfects) =>
        LinenMovement.CheckWash(true, WashProcess.ThermalDisinfection, temperature, minutes).IsSuccess.ShouldBe(disinfects);

    [Fact]
    public void Infected_linen_is_not_washed_in_a_normal_wash()
    {
        LinenMovement.CheckWash(true, WashProcess.Normal, null, null).Error!.Fields!.Keys.ShouldContain("process");
        LinenMovement.CheckWash(false, WashProcess.Normal, null, null).IsSuccess.ShouldBeTrue();
        LinenMovement.CheckWash(false, WashProcess.ThermalDisinfection, null, null).Error!.Fields!.Keys.ShouldContain("temperatureC");
    }
}
