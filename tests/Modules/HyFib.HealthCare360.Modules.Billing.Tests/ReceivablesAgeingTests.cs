using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

public sealed class ReceivablesAgeingTests
{
    private static readonly DateOnly AsOf = new(2026, 9, 25);
    private static readonly Guid Star = Guid.CreateVersion7();
    private static readonly Guid Cghs = Guid.CreateVersion7();

    [Theory]
    [InlineData(0, 1)]
    [InlineData(30, 1)]
    [InlineData(31, 2)]
    [InlineData(60, 2)]
    [InlineData(61, 3)]
    [InlineData(90, 3)]
    [InlineData(91, 4)]
    [InlineData(400, 4)]
    [InlineData(-5, 1)]
    public void Receivables_fall_into_the_right_bucket(int ageDays, int bucket)
    {
        var result = ReceivablesAgeing.ByPayer([new ReceivableItem(Star, 1_000m, AsOf.AddDays(-ageDays))], AsOf)[Star];

        decimal[] buckets = [result.Days0To30, result.Days31To60, result.Days61To90, result.Over90];
        buckets[bucket - 1].ShouldBe(1_000m);
        result.Total.ShouldBe(1_000m);
    }

    [Fact]
    public void Receivables_are_totalled_per_payer_and_settled_items_are_ignored()
    {
        var result = ReceivablesAgeing.ByPayer(
        [
            new ReceivableItem(Star, 40_000m, AsOf.AddDays(-10)),
            new ReceivableItem(Star, 25_000m, AsOf.AddDays(-45)),
            new ReceivableItem(Cghs, 12_000m, AsOf.AddDays(-120)),
            new ReceivableItem(Cghs, 0m, AsOf.AddDays(-5)),
        ], AsOf);

        result[Star].Days0To30.ShouldBe(40_000m);
        result[Star].Days31To60.ShouldBe(25_000m);
        result[Star].Total.ShouldBe(65_000m);
        result[Cghs].Over90.ShouldBe(12_000m);
        result[Cghs].Days0To30.ShouldBe(0m);
    }
}
