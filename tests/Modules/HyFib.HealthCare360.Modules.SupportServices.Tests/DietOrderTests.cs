using HyFib.HealthCare360.Modules.SupportServices.Domain.Dietary;

namespace HyFib.HealthCare360.Modules.SupportServices.Tests;

public sealed class DietOrderTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 6, 0, 0, TimeSpan.Zero);

    private static DietOrder Active(DateOnly? start = null) => new() { Status = DietOrderStatus.Active, StartDate = start ?? Today.AddDays(-2) };

    [Fact]
    public void An_active_order_covers_the_days_from_its_start()
    {
        var order = Active(Today);

        order.IsEffectiveOn(Today.AddDays(-1)).ShouldBeFalse();
        order.IsEffectiveOn(Today).ShouldBeTrue();
        order.IsEffectiveOn(Today.AddDays(30)).ShouldBeTrue();
    }

    [Fact]
    public void A_superseded_order_ends_the_day_before_its_replacement()
    {
        var order = Active();

        order.Supersede(Today, Now);

        order.Status.ShouldBe(DietOrderStatus.Stopped);
        order.EndDate.ShouldBe(Today.AddDays(-1));
        order.IsEffectiveOn(Today).ShouldBeFalse();
        order.StopReason!.ShouldStartWith("Superseded");
    }

    [Fact]
    public void An_order_superseded_on_its_first_day_never_ends_before_it_starts()
    {
        var order = Active(Today);

        order.Supersede(Today, Now);

        order.EndDate.ShouldBe(Today);
        order.IsEffectiveOn(Today).ShouldBeFalse();
    }

    [Fact]
    public void Stopping_twice_is_refused()
    {
        var order = Active();

        order.Stop(Guid.NewGuid(), "Oral intake resumed", Today, Now).IsSuccess.ShouldBeTrue();

        order.Stop(null, "again", Today, Now).Error!.Code.ShouldBe("diet.not_active");
    }

    [Fact]
    public void Discharge_completes_the_order()
    {
        var order = Active();

        order.CompleteOnDischarge(Today, Now);

        order.Status.ShouldBe(DietOrderStatus.Completed);
        order.EndDate.ShouldBe(Today);
        order.IsEffectiveOn(Today.AddDays(1)).ShouldBeFalse();
    }

    [Fact]
    public void Only_flagged_orders_are_reviewed()
    {
        var order = Active();
        order.MarkReviewed(null, Now).Error!.Code.ShouldBe("diet.no_review");

        order.NeedsReview = true;
        order.MarkReviewed(Guid.NewGuid(), Now).IsSuccess.ShouldBeTrue();
        order.NeedsReview.ShouldBeFalse();
    }

    [Theory]
    [InlineData("DIET-DIABETIC", "DIET-DIABETIC,DIABETIC")]
    [InlineData("diet-clear-liquid", "DIET-CLEAR-LIQUID,CLEAR-LIQUID")]
    [InlineData("renal", "RENAL")]
    [InlineData("DIET-", "DIET-")]
    [InlineData("", "")]
    public void Clinical_service_codes_map_to_diet_codes(string serviceCode, string expected) =>
        string.Join(',', DietCodes.Candidates(serviceCode)).ShouldBe(expected);

    [Fact]
    public void Nil_by_mouth_is_its_own_category() =>
        new DietType { Category = DietCategory.NilByMouth }.IsNilByMouth.ShouldBeTrue();
}
