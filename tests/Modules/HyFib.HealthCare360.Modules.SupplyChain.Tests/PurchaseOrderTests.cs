namespace HyFib.HealthCare360.Modules.SupplyChain.Tests;

public sealed class PurchaseOrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static PurchaseOrder Order(bool interState = false)
    {
        var order = new PurchaseOrder { Number = "NHPO26000001", IsInterState = interState };
        order.Lines.Add(new PurchaseOrderLine { ItemName = "Paracetamol 500 mg", PurchaseUnit = "STRIP", Quantity = 100, Rate = 11.50m, GstRate = 5 });
        order.Lines.Add(new PurchaseOrderLine { ItemName = "Syringe 5 ml", PurchaseUnit = "BOX", Quantity = 3, Rate = 480m, DiscountPercent = 10, GstRate = 5 });
        order.Recalculate();
        return order;
    }

    private static PurchaseOrder ApprovedOrder()
    {
        var order = Order();
        order.Submit(Now).IsSuccess.ShouldBeTrue();
        order.Approve(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        return order;
    }

    [Fact]
    public void Totals_split_gst_and_round_the_order_to_the_rupee()
    {
        var order = Order();

        // 1150.00 + (1440 − 144) = 2446.00 taxable; GST 57.50 + 64.80 = 122.30.
        order.GrossAmount.ShouldBe(2590m);
        order.DiscountAmount.ShouldBe(144m);
        order.TaxableAmount.ShouldBe(2446m);
        (order.CgstAmount + order.SgstAmount).ShouldBe(122.30m);
        order.IgstAmount.ShouldBe(0m);
        order.NetAmount.ShouldBe(2568m);
        order.RoundOff.ShouldBe(-0.30m);
    }

    [Fact]
    public void Inter_state_orders_carry_igst()
    {
        var order = Order(interState: true);

        order.IgstAmount.ShouldBe(122.30m);
        order.CgstAmount.ShouldBe(0m);
    }

    [Fact]
    public void Approval_follows_submission_and_can_send_the_order_back()
    {
        var order = Order();
        order.Approve(null, Now).Error!.Code.ShouldBe("po.not_submitted");

        order.Submit(Now).IsSuccess.ShouldBeTrue();
        order.ReturnToDraft("Rate for syringes is above contract").IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.Draft);
        order.ReviewNote.ShouldBe("Rate for syringes is above contract");

        order.Submit(Now).IsSuccess.ShouldBeTrue();
        order.Approve(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        order.MarkSent(Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.Sent);
    }

    [Fact]
    public void An_empty_order_cannot_be_submitted()
    {
        var order = new PurchaseOrder();

        order.Submit(Now).Error!.Code.ShouldBe("po.empty");
    }

    [Fact]
    public void Receipts_move_the_order_to_partially_received_then_closed()
    {
        var order = ApprovedOrder();
        var tablets = order.Lines[0];
        var syringes = order.Lines[1];

        order.RecordReceipt(tablets.Id, 60, 0, Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.PartiallyReceived);
        tablets.PendingQuantity.ShouldBe(40);

        order.RecordReceipt(tablets.Id, 41, 0, Now).Error!.Code.ShouldBe("po.over_receipt");

        order.RecordReceipt(tablets.Id, 40, 5, Now).IsSuccess.ShouldBeTrue();
        order.RecordReceipt(syringes.Id, 3, 0, Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.Closed);
        tablets.ReceivedFreeQuantity.ShouldBe(5);
        order.LastReceiptAt.ShouldBe(Now);
    }

    [Fact]
    public void Draft_orders_cannot_receive_goods()
    {
        var order = Order();

        order.RecordReceipt(order.Lines[0].Id, 1, 0, Now).Error!.Code.ShouldBe("po.not_receivable");
    }

    [Fact]
    public void Orders_with_receipts_are_short_closed_not_cancelled()
    {
        var order = ApprovedOrder();
        order.RecordReceipt(order.Lines[0].Id, 10, 0, Now);

        order.Cancel("Vendor out of stock", Now).Error!.Code.ShouldBe("po.not_cancellable");
        order.Close("Vendor out of stock", Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(PurchaseOrderStatus.Closed);
    }

    [Fact]
    public void Requisitions_are_ordered_only_after_approval()
    {
        var requisition = new PurchaseRequisition { Number = "NHPR26000001" };

        requisition.MarkOrdered(Guid.CreateVersion7()).Error!.Code.ShouldBe("requisition.not_approved");
        requisition.Approve(Guid.CreateVersion7(), Now).IsSuccess.ShouldBeTrue();
        requisition.MarkOrdered(Guid.CreateVersion7()).IsSuccess.ShouldBeTrue();
        requisition.Status.ShouldBe(RequisitionStatus.Ordered);
        requisition.Cancel("late", Now).Error!.Code.ShouldBe("requisition.closed");
    }
}
