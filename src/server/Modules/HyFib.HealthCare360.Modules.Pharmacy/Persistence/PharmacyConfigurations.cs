using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Persistence;

internal static class PharmacySchema
{
    public const string Name = "pharmacy";
}

/// <summary>Quantities to 3 decimals (ml, loose tablets); unit MRP to 4 decimals (strip MRP ÷ pack size).</summary>
internal static class PharmacyPrecision
{
    public static PropertyBuilder<decimal> IsQuantity(this PropertyBuilder<decimal> property) => property.HasPrecision(18, 3);

    public static PropertyBuilder<decimal> IsUnitRate(this PropertyBuilder<decimal> property) => property.HasPrecision(18, 4);

    public static PropertyBuilder<decimal> IsPercent(this PropertyBuilder<decimal> property) => property.HasPrecision(6, 2);
}

internal sealed class PharmacyQueueEntryConfiguration : IEntityTypeConfiguration<PharmacyQueueEntry>
{
    public void Configure(EntityTypeBuilder<PharmacyQueueEntry> b)
    {
        b.ToTable("queue_entries", PharmacySchema.Name);
        b.Property(x => x.PrescriptionNumber).HasMaxLength(40);
        b.Property(x => x.CloseReason).HasMaxLength(300);
        b.Property(x => x.RefillNote).HasMaxLength(300);
        b.Ignore(x => x.RefillsRemaining);
        b.Ignore(x => x.IsOpen);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.QueueEntryId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        // One queue entry per signed prescription (refills are extra entries): a replayed event cannot queue it twice.
        b.HasIndex(x => x.PrescriptionId).IsUnique().HasFilter("refill_of_entry_id IS NULL");
        b.HasIndex(x => new { x.Status, x.ReceivedAt });
        b.HasIndex(x => new { x.PatientId, x.SignedAt });
        b.HasIndex(x => x.AdmissionId);
        b.HasIndex(x => x.RefillOfEntryId);
    }
}

internal sealed class PharmacyQueueLineConfiguration : IEntityTypeConfiguration<PharmacyQueueLine>
{
    public void Configure(EntityTypeBuilder<PharmacyQueueLine> b)
    {
        b.ToTable("queue_lines", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.DrugName).HasMaxLength(200);
        b.Property(x => x.GenericName).HasMaxLength(200);
        b.Property(x => x.Strength).HasMaxLength(60);
        b.Property(x => x.DosageForm).HasMaxLength(60);
        b.Property(x => x.Route).HasMaxLength(40);
        b.Property(x => x.Dose).HasMaxLength(60);
        b.Property(x => x.Frequency).HasMaxLength(60);
        b.Property(x => x.Instructions).HasMaxLength(500);
        b.Property(x => x.RequestedQuantity).IsQuantity();
        b.Property(x => x.DispensedQuantity).IsQuantity();
        b.Ignore(x => x.RemainingQuantity);
        b.HasIndex(x => x.PrescriptionLineId);
    }
}

internal sealed class PharmacySaleConfiguration : IEntityTypeConfiguration<PharmacySale>
{
    public void Configure(EntityTypeBuilder<PharmacySale> b)
    {
        b.ToTable("sales", PharmacySchema.Name);
        b.Property(x => x.InvoiceNumber).HasMaxLength(24);
        b.Property(x => x.CustomerName).HasMaxLength(200);
        b.Property(x => x.CustomerPhone).HasMaxLength(20);
        b.Property(x => x.PrescriptionNumber).HasMaxLength(40);
        b.Property(x => x.PrescriberName).HasMaxLength(200);
        b.Property(x => x.PrescriberRegistrationNumber).HasMaxLength(64);
        b.Property(x => x.CounsellingNotes).HasMaxLength(2000);
        b.Property(x => x.DiscountPercent).IsPercent();
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PharmacySaleId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Payments).WithOne().HasForeignKey(x => x.PharmacySaleId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.InvoiceNumber).IsUnique();
        b.HasIndex(x => new { x.StoreId, x.SoldAt });
        b.HasIndex(x => new { x.PatientId, x.SoldAt });
        b.HasIndex(x => x.QueueEntryId);
        b.HasIndex(x => new { x.Status, x.SoldAt });
    }
}

internal sealed class PharmacySaleItemConfiguration : IEntityTypeConfiguration<PharmacySaleItem>
{
    public void Configure(EntityTypeBuilder<PharmacySaleItem> b)
    {
        b.ToTable("sale_items", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.GenericName).HasMaxLength(200);
        b.Property(x => x.HsnCode).HasMaxLength(8);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Quantity).IsQuantity();
        b.Property(x => x.UnitMrp).IsUnitRate();
        b.Property(x => x.GstRate).IsPercent();
        b.Property(x => x.ReturnedQuantity).IsQuantity();
        b.Property(x => x.PrescribedDrugName).HasMaxLength(200);
        b.Property(x => x.SubstitutionReason).HasMaxLength(300);
        b.Ignore(x => x.ReturnableQuantity);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Schedule, x.ItemId });
    }
}

internal sealed class PharmacyPaymentConfiguration : IEntityTypeConfiguration<PharmacyPayment>
{
    public void Configure(EntityTypeBuilder<PharmacyPayment> b)
    {
        b.ToTable("sale_payments", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.HasIndex(x => new { x.ReceivedAt, x.Mode });
    }
}

internal sealed class PharmacySaleReturnConfiguration : IEntityTypeConfiguration<PharmacySaleReturn>
{
    public void Configure(EntityTypeBuilder<PharmacySaleReturn> b)
    {
        b.ToTable("sale_returns", PharmacySchema.Name);
        b.Property(x => x.ReturnNumber).HasMaxLength(24);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.RefundReference).HasMaxLength(64);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SaleReturnId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<PharmacySale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ReturnNumber).IsUnique();
        b.HasIndex(x => x.ReturnedAt);
    }
}

internal sealed class PharmacySaleReturnItemConfiguration : IEntityTypeConfiguration<PharmacySaleReturnItem>
{
    public void Configure(EntityTypeBuilder<PharmacySaleReturnItem> b)
    {
        b.ToTable("sale_return_items", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Quantity).IsQuantity();
        b.HasIndex(x => x.SaleItemId);
    }
}

internal sealed class ControlledDrugRegisterEntryConfiguration : IEntityTypeConfiguration<ControlledDrugRegisterEntry>
{
    public void Configure(EntityTypeBuilder<ControlledDrugRegisterEntry> b)
    {
        b.ToTable("controlled_drug_register", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.QuantityIn).IsQuantity();
        b.Property(x => x.QuantityOut).IsQuantity();
        b.Property(x => x.BalanceAfter).IsQuantity();
        b.Property(x => x.PatientName).HasMaxLength(200);
        b.Property(x => x.PatientUhid).HasMaxLength(20);
        b.Property(x => x.PatientAddress).HasMaxLength(400);
        b.Property(x => x.PrescriberName).HasMaxLength(200);
        b.Property(x => x.PrescriberRegistrationNumber).HasMaxLength(64);
        b.Property(x => x.PrescriptionNumber).HasMaxLength(64);
        b.Property(x => x.ReferenceModule).HasMaxLength(40);
        b.Property(x => x.ReferenceNumber).HasMaxLength(32);
        b.Property(x => x.Remarks).HasMaxLength(300);

        // Exactly one register line per stock movement, whichever path recorded it.
        b.HasIndex(x => x.LedgerEntryId).IsUnique();
        b.HasIndex(x => new { x.StoreId, x.ItemId, x.OccurredAt });
        b.HasIndex(x => new { x.Schedule, x.OccurredAt });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class RefillRequestConfiguration : IEntityTypeConfiguration<RefillRequest>
{
    public void Configure(EntityTypeBuilder<RefillRequest> b)
    {
        b.ToTable("refill_requests", PharmacySchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.RejectReason).HasMaxLength(300);
        b.HasOne<PharmacyQueueEntry>().WithMany().HasForeignKey(x => x.QueueEntryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.Status, x.RequestedAt });
        b.HasIndex(x => new { x.PatientId, x.RequestedAt });
    }
}
