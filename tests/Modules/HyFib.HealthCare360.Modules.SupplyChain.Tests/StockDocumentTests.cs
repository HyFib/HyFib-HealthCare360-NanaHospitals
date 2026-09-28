namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

/// <summary>Rules of the stock documents: GRN lines, indents, transfers, adjustments, counts and the item master.</summary>
public sealed class StockDocumentTests
{
    private static readonly DateOnly Today = new(2026, 9, 25);
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static GoodsReceiptLine GrnLine(int expiresInDays = 400, decimal rate = 40m, decimal mrp = 70m, bool acceptShort = false) => new()
    {
        ItemName = "Amoxicillin 500 mg",
        BatchNumber = "AMX2601",
        ExpiryDate = Today.AddDays(expiresInDays),
        Quantity = 10,
        FreeQuantity = 2,
        Rate = rate,
        Mrp = mrp,
        GstRate = 5,
        UnitsPerPurchaseUnit = 10,
        RequiresBatch = true,
        RequiresMrp = true,
        AcceptShortExpiry = acceptShort,
    };

    [Fact]
    public void Grn_never_accepts_expired_goods_and_needs_confirmation_for_short_expiry()
    {
        GrnLine(expiresInDays: 0).Validate(Today).Error!.Code.ShouldBe("grn.expired");
        GrnLine(expiresInDays: 45).Validate(Today).Error!.Code.ShouldBe("grn.short_expiry");
        GrnLine(expiresInDays: 45, acceptShort: true).Validate(Today).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Grn_rate_cannot_exceed_mrp_and_medicines_need_batch_and_mrp()
    {
        GrnLine(rate: 80m, mrp: 70m).Validate(Today).Error!.Code.ShouldBe("grn.rate_above_mrp");
        GrnLine(mrp: 0).Validate(Today).Error!.Code.ShouldBe("grn.mrp_required");

        var noBatch = GrnLine();
        noBatch.BatchNumber = " ";
        noBatch.Validate(Today).Error!.Code.ShouldBe("grn.batch_required");
    }

    [Fact]
    public void Grn_converts_to_base_units_and_spreads_cost_over_free_goods()
    {
        var line = GrnLine();
        line.Price(interState: false);

        line.BaseQuantity.ShouldBe(120m);
        line.UnitMrp.ShouldBe(7m);
        line.TaxableAmount.ShouldBe(400m);
        line.UnitCost.ShouldBe(3.3333m);
    }

    [Fact]
    public void Indent_approval_can_reduce_quantities_and_issues_cannot_exceed_them()
    {
        var indent = new Indent { Number = "NHIND26000001" };
        var gloves = new IndentLine { ItemName = "Gloves", RequestedQuantity = 100 };
        var gauze = new IndentLine { ItemName = "Gauze", RequestedQuantity = 50 };
        indent.Lines.AddRange([gloves, gauze]);

        indent.Approve(null, new Dictionary<Guid, decimal> { [gloves.Id] = 60 }, Now).IsSuccess.ShouldBeTrue();
        gloves.PendingQuantity.ShouldBe(60);
        gauze.PendingQuantity.ShouldBe(50);

        indent.RecordIssue(gloves.Id, 61, Now).Error!.Code.ShouldBe("indent.over_issue");
        indent.RecordIssue(gloves.Id, 60, Now).IsSuccess.ShouldBeTrue();
        indent.Status.ShouldBe(IndentStatus.PartiallyIssued);
        indent.Cancel("no longer needed", Now).Error!.Code.ShouldBe("indent.not_cancellable");

        indent.RecordIssue(gauze.Id, 50, Now).IsSuccess.ShouldBeTrue();
        indent.Status.ShouldBe(IndentStatus.Issued);
        indent.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void Partially_issued_indents_can_be_short_closed()
    {
        var indent = new Indent();
        var line = new IndentLine { ItemName = "Gauze", RequestedQuantity = 50 };
        indent.Lines.Add(line);

        indent.Close("balance not needed", Now).Error!.Code.ShouldBe("indent.not_partial");
        indent.RecordIssue(line.Id, 20, Now);
        indent.Close("balance not needed", Now).IsSuccess.ShouldBeTrue();
        indent.Status.ShouldBe(IndentStatus.Closed);
    }

    [Fact]
    public void Transfers_are_received_or_cancelled_once_while_in_transit()
    {
        var transfer = new StockTransfer { Number = "NHSTN26000001" };

        transfer.Receive(null, Now).IsSuccess.ShouldBeTrue();
        transfer.Receive(null, Now).Error!.Code.ShouldBe("transfer.not_in_transit");
        transfer.Cancel("wrong store", Now).Error!.Code.ShouldBe("transfer.not_in_transit");
    }

    [Fact]
    public void Adjustments_need_a_second_person_and_write_offs_only_reduce_stock()
    {
        var requester = Guid.CreateVersion7();
        var adjustment = new StockAdjustment { Reason = AdjustmentReason.Expiry, RequestedByUserId = requester };
        adjustment.Lines.Add(new StockAdjustmentLine { QuantityChange = 5 });
        adjustment.ValidateLines().Error!.Code.ShouldBe("adjustment.reason_mismatch");

        adjustment.Lines[0].QuantityChange = -5;
        adjustment.ValidateLines().IsSuccess.ShouldBeTrue();
        adjustment.MovementType.ShouldBe(StockMovementType.ExpiryWriteOff);

        adjustment.Approve(requester, Now).Error!.Code.ShouldBe("adjustment.self_approval");
        adjustment.Approve(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        adjustment.Approve(Guid.CreateVersion7(), Now).Error!.Code.ShouldBe("adjustment.decided");
    }

    [Fact]
    public void Count_variances_are_measured_against_book_stock_at_submission()
    {
        var count = new PhysicalStockCount { Number = "NHPSC2600001" };
        var counted = new StockCountLine { BatchId = Guid.CreateVersion7(), SystemQuantity = 100 };
        var skipped = new StockCountLine { BatchId = Guid.CreateVersion7(), SystemQuantity = 20 };
        count.Lines.AddRange([counted, skipped]);

        count.Submit(new Dictionary<Guid, decimal>(), Now).Error!.Code.ShouldBe("count.nothing_counted");
        count.RecordCount(counted.Id, -1, null).Error!.Code.ShouldBe("count.negative");
        count.RecordCount(counted.Id, 95, "two strips damaged").IsSuccess.ShouldBeTrue();

        count.Submit(new Dictionary<Guid, decimal> { [counted.BatchId] = 98 }, Now).IsSuccess.ShouldBeTrue();
        counted.VarianceQuantity.ShouldBe(-3);
        skipped.VarianceQuantity.ShouldBeNull();
        count.RecordCount(counted.Id, 96, null).Error!.Code.ShouldBe("count.closed");
    }

    [Fact]
    public void Item_master_enforces_schedule_and_gst_consistency()
    {
        var morphine = new Item { Type = ItemType.Drug, Schedule = DrugSchedule.X, IsControlled = true, GstRate = 5, PurchaseUnitFactor = 1 };
        morphine.Validate().IsSuccess.ShouldBeTrue();
        morphine.RequiresPrescription.ShouldBeTrue();
        morphine.RequiresRegister.ShouldBeTrue();

        new Item { Type = ItemType.Consumable, Schedule = DrugSchedule.H, PurchaseUnitFactor = 1 }.Validate().IsFailure.ShouldBeTrue();
        new Item { Type = ItemType.Drug, GstRate = 7, PurchaseUnitFactor = 1 }.Validate().IsFailure.ShouldBeTrue();
        new Item { Type = ItemType.Drug, PurchaseUnitFactor = 0 }.Validate().IsFailure.ShouldBeTrue();

        Item.NeedsPrescription(DrugSchedule.H, false).ShouldBeTrue();
        Item.NeedsPrescription(DrugSchedule.G, false).ShouldBeFalse();
        Item.NeedsRegister(DrugSchedule.H, false).ShouldBeFalse();
        Item.NeedsRegister(DrugSchedule.H1, false).ShouldBeTrue();
    }

    [Theory]
    [InlineData("33AABCS1234F1Z5", "33AAGCS8765P1Z3", false)]
    [InlineData("27AABCO2468Q1Z6", "33AAGCS8765P1Z3", true)]
    [InlineData(null, "33AAGCS8765P1Z3", false)]
    public void Inter_state_supply_is_decided_by_gstin_state_codes(string? vendor, string? facility, bool expected) =>
        Vendor.IsInterState(vendor, facility).ShouldBe(expected);
}
