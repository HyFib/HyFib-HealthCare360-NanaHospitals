using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Persistence;

internal sealed class IndentConfiguration : IEntityTypeConfiguration<Indent>
{
    public void Configure(EntityTypeBuilder<Indent> b)
    {
        b.ToTable("indents", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.DecisionNote).HasMaxLength(300);
        b.Ignore(x => x.IsOpen);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.IndentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.FromStoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.FromStoreId, x.Status });
        b.HasIndex(x => new { x.ToStoreId, x.Status });
        b.HasIndex(x => x.RequestedAt);
    }
}

internal sealed class IndentLineConfiguration : IEntityTypeConfiguration<IndentLine>
{
    public void Configure(EntityTypeBuilder<IndentLine> b)
    {
        b.ToTable("indent_lines", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.RequestedQuantity).IsQuantity();
        b.Property(x => x.ApprovedQuantity).IsQuantity();
        b.Property(x => x.IssuedQuantity).IsQuantity();
        b.Property(x => x.Remarks).HasMaxLength(300);
        b.Ignore(x => x.PendingQuantity);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class IndentIssueConfiguration : IEntityTypeConfiguration<IndentIssue>
{
    public void Configure(EntityTypeBuilder<IndentIssue> b)
    {
        b.ToTable("indent_issues", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.ReceivedByName).HasMaxLength(120);
        b.Property(x => x.Remarks).HasMaxLength(500);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.IndentIssueId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Indent>().WithMany().HasForeignKey(x => x.IndentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => x.IssuedAt);
    }
}

internal sealed class IndentIssueLineConfiguration : IEntityTypeConfiguration<IndentIssueLine>
{
    public void Configure(EntityTypeBuilder<IndentIssueLine> b)
    {
        b.ToTable("indent_issue_lines", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Quantity).IsQuantity();
        b.Property(x => x.UnitCost).IsUnitRate();
        b.HasIndex(x => x.BatchId);
    }
}

internal sealed class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> b)
    {
        b.ToTable("stock_transfers", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Remarks).HasMaxLength(500);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockTransferId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.FromStoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.ToStoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.ToStoreId, x.Status });
        b.HasIndex(x => x.DispatchedAt);
    }
}

internal sealed class StockTransferLineConfiguration : IEntityTypeConfiguration<StockTransferLine>
{
    public void Configure(EntityTypeBuilder<StockTransferLine> b)
    {
        b.ToTable("stock_transfer_lines", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Mrp).IsUnitRate();
        b.Property(x => x.PurchaseRate).IsUnitRate();
        b.Property(x => x.GstRate).IsPercent();
        b.Property(x => x.Quantity).IsQuantity();
        b.HasIndex(x => x.SourceBatchId);
    }
}

internal sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> b)
    {
        b.ToTable("stock_adjustments", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.RejectReason).HasMaxLength(300);
        b.Ignore(x => x.MovementType);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StockAdjustmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.Status, x.RequestedAt });
        b.HasIndex(x => x.PhysicalCountId);
    }
}

internal sealed class StockAdjustmentLineConfiguration : IEntityTypeConfiguration<StockAdjustmentLine>
{
    public void Configure(EntityTypeBuilder<StockAdjustmentLine> b)
    {
        b.ToTable("stock_adjustment_lines", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.SystemQuantity).IsQuantity();
        b.Property(x => x.QuantityChange).IsQuantity();
        b.Property(x => x.UnitCost).IsUnitRate();
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PhysicalStockCountConfiguration : IEntityTypeConfiguration<PhysicalStockCount>
{
    public void Configure(EntityTypeBuilder<PhysicalStockCount> b)
    {
        b.ToTable("stock_counts", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PhysicalStockCountId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.StoreId, x.Status });
    }
}

internal sealed class StockCountLineConfiguration : IEntityTypeConfiguration<StockCountLine>
{
    public void Configure(EntityTypeBuilder<StockCountLine> b)
    {
        b.ToTable("stock_count_lines", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.SystemQuantity).IsQuantity();
        b.Property(x => x.CountedQuantity).IsQuantity();
        b.Property(x => x.VarianceQuantity).IsQuantity();
        b.Property(x => x.UnitCost).IsUnitRate();
        b.Property(x => x.Remarks).HasMaxLength(300);
        b.HasIndex(x => x.BatchId);
    }
}
