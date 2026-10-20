namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>
/// The reorder rule behind the reorder report and the requisition made from it (INV-08, PH-04): stock plus what is on
/// order at or under the reorder level is suggested, topped up to the maximum, vital items first.
/// </summary>
public sealed class ReorderCalculatorTests
{
    /// <summary>
    /// Paracetamol counted in tablets and bought in strips of 10: reordered at 200 tablets, 300 at a time, and never more
    /// than 1,000 kept.
    /// </summary>
    private static Item Paracetamol(string name = "Paracetamol 500 mg", VedClass ved = VedClass.Essential) => new()
    {
        Code = "PCM500",
        Name = name,
        BaseUnit = "TAB",
        PurchaseUnit = "STRIP",
        PurchaseUnitFactor = 10,
        ReorderLevel = 200,
        ReorderQuantity = 300,
        MaximumStock = 1000,
        VedClass = ved,
    };

    [Theory]
    [InlineData(200, true)]
    [InlineData(199.5, true)]
    [InlineData(150, true)]
    [InlineData(0, true)]
    [InlineData(200.5, false)]
    [InlineData(201, false)]
    [InlineData(5000, false)]
    public void An_item_is_suggested_when_its_stock_is_at_or_under_the_reorder_level(double available, bool suggested) =>
        (ReorderCalculator.Suggest(Paracetamol(), (decimal)available, 0) is not null).ShouldBe(suggested);

    [Fact]
    public void Stock_still_on_order_counts_towards_the_level_so_an_open_order_is_not_ordered_twice()
    {
        // 150 tablets in the store and 10 strips (100 tablets) still to come: 250, above the level.
        ReorderCalculator.Suggest(Paracetamol(), 150, 10).ShouldBeNull();

        // 5 strips to come make 200, at the level: due, and the top-up leaves out the 50 tablets already ordered.
        var suggestion = ReorderCalculator.Suggest(Paracetamol(), 150, 5).ShouldNotBeNull();
        suggestion.Available.ShouldBe(150m);
        suggestion.OnOrder.ShouldBe(50m, "open orders are in strips; the rule counts tablets");
        suggestion.Quantity.ShouldBe(800m);
        suggestion.PurchaseUnits.ShouldBe(80m);
    }

    [Fact]
    public void The_suggestion_tops_the_stock_up_to_the_maximum()
    {
        var suggestion = ReorderCalculator.Suggest(Paracetamol(), 120, 2).ShouldNotBeNull();

        suggestion.Quantity.ShouldBe(860m);
        (suggestion.Available + suggestion.OnOrder + suggestion.Quantity).ShouldBe(1000m);
    }

    [Fact]
    public void With_no_maximum_set_it_tops_up_to_the_reorder_level_plus_the_reorder_quantity()
    {
        var item = Paracetamol();
        item.MaximumStock = 0;

        ReorderCalculator.Suggest(item, 120, 0).ShouldNotBeNull().Quantity.ShouldBe(380m);
    }

    [Fact]
    public void It_never_orders_less_than_the_reorder_quantity()
    {
        // A maximum set close to the level: topping up to 400 needs only 210 tablets, less than the 300 ordered at a time.
        var item = Paracetamol();
        item.MaximumStock = 400;

        var suggestion = ReorderCalculator.Suggest(item, 190, 0).ShouldNotBeNull();
        suggestion.Quantity.ShouldBe(300m);
        suggestion.PurchaseUnits.ShouldBe(30m);
    }

    [Fact]
    public void Purchase_units_are_rounded_up_to_whole_packs()
    {
        // 1,000 − 155 = 845 tablets: 84.5 strips, so 85 are bought.
        var suggestion = ReorderCalculator.Suggest(Paracetamol(), 155, 0).ShouldNotBeNull();

        suggestion.Quantity.ShouldBe(845m);
        suggestion.PurchaseUnits.ShouldBe(85m);
    }

    [Fact]
    public void An_item_with_no_reorder_level_or_no_longer_in_use_is_never_suggested()
    {
        var noLevel = Paracetamol();
        noLevel.ReorderLevel = 0;
        var retired = Paracetamol();
        retired.IsActive = false;

        ReorderCalculator.Suggest(noLevel, 0, 0).ShouldBeNull("a reorder level of 0 turns reordering off");
        ReorderCalculator.Suggest(retired, 0, 0).ShouldBeNull();
    }

    [Fact]
    public void More_received_than_ordered_on_the_open_orders_counts_as_nothing_on_order()
    {
        var suggestion = ReorderCalculator.Suggest(Paracetamol(), 100, -3).ShouldNotBeNull();

        suggestion.OnOrder.ShouldBe(0m);
        suggestion.Quantity.ShouldBe(900m);
    }

    [Fact]
    public void Stock_below_zero_is_made_good_as_well_as_topped_up()
    {
        var suggestion = ReorderCalculator.Suggest(Paracetamol(), -30, 0).ShouldNotBeNull();

        suggestion.Quantity.ShouldBe(1030m);
        suggestion.PurchaseUnits.ShouldBe(103m);
    }

    [Fact]
    public void An_item_with_no_valid_pack_size_is_ordered_in_its_base_unit()
    {
        var item = Paracetamol();
        item.PurchaseUnitFactor = 0;

        var suggestion = ReorderCalculator.Suggest(item, 155, 0).ShouldNotBeNull();
        suggestion.PurchaseUnits.ShouldBe(suggestion.Quantity);
    }

    [Fact]
    public void Vital_items_come_first_then_essential_then_desirable_each_by_name()
    {
        Item[] items =
        [
            Paracetamol("Gauze swab", VedClass.Desirable),
            Paracetamol("Atropine 0.6 mg", VedClass.Vital),
            Paracetamol("Paracetamol 500 mg", VedClass.Essential),
            Paracetamol("adrenaline 1 mg", VedClass.Vital),
            Paracetamol("Bandage roll", VedClass.Desirable),
            Paracetamol("Amoxicillin 500 mg", VedClass.Essential),
        ];

        var ranked = ReorderCalculator.Rank(items.Select(i => ReorderCalculator.Suggest(i, 0, 0).ShouldNotBeNull()));

        ranked.Select(s => s.Item.Name).ToArray().ShouldBe(
            ["adrenaline 1 mg", "Atropine 0.6 mg", "Amoxicillin 500 mg", "Paracetamol 500 mg", "Bandage roll", "Gauze swab"],
            "names are compared without regard to case");
    }

    [Theory]
    [InlineData(85, 85)]
    [InlineData(1, 1)]
    [InlineData(0.4, 1)]
    [InlineData(0, 1)]
    public void A_requisition_line_made_from_a_suggestion_asks_for_at_least_one_pack(double suggested, double ordered) =>
        ReorderCalculator.RequisitionQuantity((decimal)suggested).ShouldBe((decimal)ordered);
}
