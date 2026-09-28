using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Persistence;

internal sealed class PurchaseRequisitionConfiguration : IEntityTypeConfiguration<PurchaseRequisition>
{
    public void Configure(EntityTypeBuilder<PurchaseRequisition> b)
    {
        b.ToTable("purchase_requisitions", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.RejectReason).HasMaxLength(300);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseRequisitionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

internal sealed class PurchaseRequisitionLineConfiguration : IEntityTypeConfiguration<PurchaseRequisitionLine>
{
    public void Configure(EntityTypeBuilder<PurchaseRequisitionLine> b)
    {
        b.ToTable("purchase_requisition_lines", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.Quantity).IsQuantity();
        b.Property(x => x.Remarks).HasMaxLength(300);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.ToTable("purchase_orders", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.ReviewNote).HasMaxLength(500);
        b.Property(x => x.CloseReason).HasMaxLength(300);
        b.Ignore(x => x.IsReceivable);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.VendorId, x.OrderDate });
        b.HasIndex(x => new { x.Status, x.OrderDate });
        b.HasIndex(x => x.RequisitionId);
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ToTable("purchase_order_lines", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.PurchaseUnit).HasMaxLength(16);
        b.Property(x => x.Quantity).IsQuantity();
        b.Property(x => x.FreeQuantity).IsQuantity();
        b.Property(x => x.Rate).IsUnitRate();
        b.Property(x => x.DiscountPercent).IsPercent();
        b.Property(x => x.GstRate).IsPercent();
        b.Property(x => x.ReceivedQuantity).IsQuantity();
        b.Property(x => x.ReceivedFreeQuantity).IsQuantity();
        b.Ignore(x => x.PendingQuantity);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GoodsReceiptNoteConfiguration : IEntityTypeConfiguration<GoodsReceiptNote>
{
    public void Configure(EntityTypeBuilder<GoodsReceiptNote> b)
    {
        b.ToTable("goods_receipts", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.VendorInvoiceNumber).HasMaxLength(40);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.GoodsReceiptNoteId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();

        // A vendor invoice can be booked only once (cancelled drafts excepted).
        b.HasIndex(x => new { x.VendorId, x.VendorInvoiceNumber }).IsUnique().HasFilter("status <> 'Cancelled'");
        b.HasIndex(x => new { x.StoreId, x.ReceivedOn });
    }
}

internal sealed class GoodsReceiptLineConfiguration : IEntityTypeConfiguration<GoodsReceiptLine>
{
    public void Configure(EntityTypeBuilder<GoodsReceiptLine> b)
    {
        b.ToTable("goods_receipt_lines", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Quantity).IsQuantity();
        b.Property(x => x.FreeQuantity).IsQuantity();
        b.Property(x => x.Rate).IsUnitRate();
        b.Property(x => x.Mrp).IsUnitRate();
        b.Property(x => x.DiscountPercent).IsPercent();
        b.Property(x => x.GstRate).IsPercent();
        b.Property(x => x.UnitsPerPurchaseUnit).IsQuantity();
        b.Ignore(x => x.BaseQuantity);
        b.Ignore(x => x.UnitMrp);
        b.Ignore(x => x.UnitCost);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PurchaseOrderLineId);
        b.HasIndex(x => x.StockBatchId);
    }
}
