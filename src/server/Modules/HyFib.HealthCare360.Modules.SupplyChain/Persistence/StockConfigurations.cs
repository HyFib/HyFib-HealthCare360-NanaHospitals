using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Persistence;

internal sealed class StockBatchConfiguration : IEntityTypeConfiguration<StockBatch>
{
    public void Configure(EntityTypeBuilder<StockBatch> b)
    {
        // Last line of defence behind the domain rules: stock can never go negative or be over-reserved.
        b.ToTable("stock_batches", SupplyChainSchema.Name, t => t.HasCheckConstraint(
            "ck_stock_batches_quantities",
            "quantity_on_hand >= 0 AND quantity_reserved >= 0 AND quantity_reserved <= quantity_on_hand"));
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Mrp).IsUnitRate();
        b.Property(x => x.PurchaseRate).IsUnitRate();
        b.Property(x => x.GstRate).IsPercent();
        b.Property(x => x.QuantityOnHand).IsQuantity();
        b.Property(x => x.QuantityReserved).IsQuantity();
        b.Property(x => x.BlockReason).HasMaxLength(300);
        b.Ignore(x => x.QuantityAvailable);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);

        // One row per physical batch in a store: re-receipts of the same batch/expiry/MRP top it up.
        b.HasIndex(x => new { x.StoreId, x.ItemId, x.BatchNumber, x.ExpiryDate, x.Mrp }).IsUnique().AreNullsDistinct(false);
        b.HasIndex(x => new { x.StoreId, x.ItemId, x.ExpiryDate });
        b.HasIndex(x => x.ExpiryDate);
    }
}

internal sealed class StockLedgerEntryConfiguration : IEntityTypeConfiguration<StockLedgerEntry>
{
    public void Configure(EntityTypeBuilder<StockLedgerEntry> b)
    {
        b.ToTable("stock_ledger", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Quantity).IsQuantity();
        b.Property(x => x.BatchBalanceAfter).IsQuantity();
        b.Property(x => x.BalanceAfter).IsQuantity();
        b.Property(x => x.UnitCost).IsUnitRate();
        b.Property(x => x.Mrp).IsUnitRate();
        b.Property(x => x.ReferenceModule).HasMaxLength(40);
        b.Property(x => x.ReferenceNumber).HasMaxLength(32);
        b.Property(x => x.Remarks).HasMaxLength(300);
        b.Ignore(x => x.Value);
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.StoreId, x.ItemId, x.OccurredAt });
        b.HasIndex(x => new { x.ItemId, x.OccurredAt });
        b.HasIndex(x => new { x.MovementType, x.OccurredAt });
        b.HasIndex(x => new { x.ReferenceModule, x.ReferenceId });
        b.HasIndex(x => x.BatchId);
    }
}

internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> b)
    {
        b.ToTable("stock_reservations", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Quantity).IsQuantity();
        b.Property(x => x.ConsumedQuantity).IsQuantity();
        b.Property(x => x.ReleasedQuantity).IsQuantity();
        b.Property(x => x.ReferenceModule).HasMaxLength(40);
        b.Property(x => x.ReferenceNumber).HasMaxLength(32);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Ignore(x => x.Outstanding);
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ReferenceModule, x.ReferenceId, x.Status });
        b.HasIndex(x => new { x.StoreId, x.ItemId, x.Status });
    }
}
