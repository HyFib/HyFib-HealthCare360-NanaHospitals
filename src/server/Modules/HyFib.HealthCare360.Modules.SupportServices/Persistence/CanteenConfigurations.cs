using HyFib.HealthCare360.Modules.SupportServices.Domain.Canteen;

namespace HyFib.HealthCare360.Modules.SupportServices.Persistence;

internal sealed class MenuItemConfiguration : IEntityTypeConfiguration<MenuItem>
{
    public void Configure(EntityTypeBuilder<MenuItem> b)
    {
        b.ToTable("menu_items", SupportServicesSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.GstRate).HasPrecision(5, 2);
        b.Property(x => x.HsnSac).HasMaxLength(8);
        b.Property(x => x.Description).HasMaxLength(300);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.Category, x.IsAvailable });
    }
}

internal sealed class CanteenStockMovementConfiguration : IEntityTypeConfiguration<CanteenStockMovement>
{
    public void Configure(EntityTypeBuilder<CanteenStockMovement> b)
    {
        b.ToTable("canteen_stock_movements", SupportServicesSchema.Name);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasOne<MenuItem>().WithMany().HasForeignKey(x => x.MenuItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.MenuItemId, x.MovedAt });
    }
}

internal sealed class CanteenSaleConfiguration : IEntityTypeConfiguration<CanteenSale>
{
    public void Configure(EntityTypeBuilder<CanteenSale> b)
    {
        b.ToTable("canteen_sales", SupportServicesSchema.Name);
        b.Property(x => x.BillNumber).HasMaxLength(24);
        b.Property(x => x.CustomerName).HasMaxLength(200);
        b.Property(x => x.EmployeeCode).HasMaxLength(20);
        b.Property(x => x.EmployeeName).HasMaxLength(200);
        b.Property(x => x.PaymentReference).HasMaxLength(64);
        b.Property(x => x.CashierName).HasMaxLength(200);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CanteenSaleId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.BillNumber).IsUnique();
        b.HasIndex(x => new { x.BusinessDate, x.Status });
        b.HasIndex(x => new { x.EmployeeCode, x.BusinessDate });
    }
}

internal sealed class CanteenSaleLineConfiguration : IEntityTypeConfiguration<CanteenSaleLine>
{
    public void Configure(EntityTypeBuilder<CanteenSaleLine> b)
    {
        b.ToTable("canteen_sale_lines", SupportServicesSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(100);
        b.Property(x => x.HsnSac).HasMaxLength(8);
        b.Property(x => x.GstRate).HasPrecision(5, 2);
        b.HasIndex(x => x.MenuItemId);
    }
}

internal sealed class StaffCreditEntryConfiguration : IEntityTypeConfiguration<StaffCreditEntry>
{
    public void Configure(EntityTypeBuilder<StaffCreditEntry> b)
    {
        b.ToTable("staff_credit_entries", SupportServicesSchema.Name);
        b.Property(x => x.EmployeeCode).HasMaxLength(20);
        b.Property(x => x.EmployeeName).HasMaxLength(200);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.Property(x => x.SettlesPeriod).HasMaxLength(7);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasIndex(x => new { x.EmployeeCode, x.EntryDate });
        b.HasIndex(x => x.SaleId);
        b.HasIndex(x => new { x.EmployeeCode, x.SettlesPeriod }).IsUnique().HasFilter("settles_period IS NOT NULL AND NOT is_deleted");
    }
}
