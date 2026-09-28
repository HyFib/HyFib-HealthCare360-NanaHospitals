namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

public sealed class InventoryAnalysisTests
{
    private static readonly Guid[] Ids = Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7()).ToArray();

    [Fact]
    public void Abc_splits_consumption_value_70_20_10()
    {
        var results = AbcAnalysis.Classify([(Ids[0], 700m), (Ids[1], 200m), (Ids[2], 50m), (Ids[3], 30m), (Ids[4], 20m)])
            .ToDictionary(r => r.ItemId);

        results[Ids[0]].Class.ShouldBe(AbcClass.A);
        results[Ids[1]].Class.ShouldBe(AbcClass.B);
        results[Ids[2]].Class.ShouldBe(AbcClass.C);
        results[Ids[4]].Class.ShouldBe(AbcClass.C);
        results[Ids[1]].CumulativePercent.ShouldBe(90m);
        results[Ids[0]].SharePercent.ShouldBe(70m);
    }

    [Fact]
    public void A_single_dominant_item_stays_in_class_a()
    {
        var results = AbcAnalysis.Classify([(Ids[0], 950m), (Ids[1], 30m), (Ids[2], 20m)]).ToDictionary(r => r.ItemId);

        results[Ids[0]].Class.ShouldBe(AbcClass.A);
        results[Ids[1]].Class.ShouldBe(AbcClass.C);
    }

    [Fact]
    public void Items_without_consumption_are_class_c()
    {
        AbcAnalysis.Classify([(Ids[0], 0m), (Ids[1], 0m)]).ShouldAllBe(r => r.Class == AbcClass.C);
        AbcAnalysis.Classify([(Ids[0], 10m), (Ids[1], 0m)]).Single(r => r.ItemId == Ids[1]).Class.ShouldBe(AbcClass.C);
    }

    [Fact]
    public void Fsn_ranks_by_issue_frequency_and_flags_non_moving_stock()
    {
        var results = FsnAnalysis.Classify([(Ids[0], 50), (Ids[1], 30), (Ids[2], 15), (Ids[3], 5), (Ids[4], 0)]).ToDictionary(r => r.ItemId);

        results[Ids[0]].Class.ShouldBe(FsnClass.Fast);
        results[Ids[1]].Class.ShouldBe(FsnClass.Fast);
        results[Ids[2]].Class.ShouldBe(FsnClass.Slow);
        results[Ids[3]].Class.ShouldBe(FsnClass.Slow);
        results[Ids[4]].Class.ShouldBe(FsnClass.NonMoving);
    }

    [Theory]
    [InlineData(AbcClass.A, VedClass.Vital, AbcVedCategory.I)]
    [InlineData(AbcClass.A, VedClass.Essential, AbcVedCategory.I)]
    [InlineData(AbcClass.A, VedClass.Desirable, AbcVedCategory.I)]
    [InlineData(AbcClass.B, VedClass.Vital, AbcVedCategory.I)]
    [InlineData(AbcClass.C, VedClass.Vital, AbcVedCategory.I)]
    [InlineData(AbcClass.B, VedClass.Essential, AbcVedCategory.II)]
    [InlineData(AbcClass.C, VedClass.Essential, AbcVedCategory.II)]
    [InlineData(AbcClass.B, VedClass.Desirable, AbcVedCategory.II)]
    [InlineData(AbcClass.C, VedClass.Desirable, AbcVedCategory.III)]
    public void Abc_ved_matrix_follows_the_standard_categories(AbcClass abc, VedClass ved, AbcVedCategory expected) =>
        VedAnalysis.Matrix(abc, ved).ShouldBe(expected);
}
