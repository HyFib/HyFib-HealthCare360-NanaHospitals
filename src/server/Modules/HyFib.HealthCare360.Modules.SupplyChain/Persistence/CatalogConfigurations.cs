using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Persistence;

internal static class SupplyChainSchema
{
    public const string Name = "supplychain";
}

/// <summary>
/// Numeric precision used across the module: quantities to 3 decimals (ml, loose tablets) and unit rates to 4
/// decimals (a strip MRP of 32.50 ÷ 15 tablets must not lose paise when multiplied back).
/// </summary>
internal static class PrecisionExtensions
{
    public static PropertyBuilder<decimal> IsQuantity(this PropertyBuilder<decimal> property) => property.HasPrecision(18, 3);

    public static PropertyBuilder<decimal?> IsQuantity(this PropertyBuilder<decimal?> property) => property.HasPrecision(18, 3);

    public static PropertyBuilder<decimal> IsUnitRate(this PropertyBuilder<decimal> property) => property.HasPrecision(18, 4);

    public static PropertyBuilder<decimal> IsPercent(this PropertyBuilder<decimal> property) => property.HasPrecision(6, 2);
}

internal sealed class ItemCategoryConfiguration : IEntityTypeConfiguration<ItemCategory>
{
    public void Configure(EntityTypeBuilder<ItemCategory> b)
    {
        b.ToTable("item_categories", SupplyChainSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(300);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class ItemConfiguration : IEntityTypeConfiguration<Item>
{
    public void Configure(EntityTypeBuilder<Item> b)
    {
        b.ToTable("items", SupplyChainSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.GenericName).HasMaxLength(200);
        b.Property(x => x.BrandName).HasMaxLength(120);
        b.Property(x => x.Strength).HasMaxLength(60);
        b.Property(x => x.DosageForm).HasMaxLength(60);
        b.Property(x => x.PackSize).HasMaxLength(60);
        b.Property(x => x.BaseUnit).HasMaxLength(16);
        b.Property(x => x.PurchaseUnit).HasMaxLength(16);
        b.Property(x => x.PurchaseUnitFactor).IsQuantity();
        b.Property(x => x.HsnCode).HasMaxLength(8);
        b.Property(x => x.GstRate).IsPercent();
        b.Property(x => x.ReorderLevel).IsQuantity();
        b.Property(x => x.ReorderQuantity).IsQuantity();
        b.Property(x => x.MinimumStock).IsQuantity();
        b.Property(x => x.MaximumStock).IsQuantity();
        b.Property(x => x.Manufacturer).HasMaxLength(120);
        b.Property(x => x.Specification).HasMaxLength(200);
        b.Property(x => x.CeilingPrice).HasPrecision(18, 4);
        b.Property(x => x.Barcode).HasMaxLength(64);
        b.Ignore(x => x.RequiresPrescription);
        b.Ignore(x => x.RequiresRegister);
        b.Ignore(x => x.TracksExpiry);
        b.HasOne<ItemCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Barcode);
        b.HasIndex(x => x.GenericName);
        b.HasIndex(x => new { x.Type, x.IsActive });

        // Pharmacy counters search drugs by partial, misspelt names (PERF: trigram index).
        b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class StoreConfiguration : IEntityTypeConfiguration<Store>
{
    public void Configure(EntityTypeBuilder<Store> b)
    {
        b.ToTable("stores", SupplyChainSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Location).HasMaxLength(200);
        b.Ignore(x => x.IsDispensing);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique();
        b.HasIndex(x => x.Type);
    }
}

internal sealed class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> b)
    {
        b.ToTable("vendors", SupplyChainSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Gstin).HasMaxLength(15);
        b.Property(x => x.Pan).HasMaxLength(10);
        b.Property(x => x.DrugLicenseNumber).HasMaxLength(64);
        b.Property(x => x.ContactPerson).HasMaxLength(120);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Rating).HasPrecision(3, 1);
        b.ComplexProperty(x => x.Address, a =>
        {
            a.Property(p => p.Line1).HasMaxLength(200);
            a.Property(p => p.Line2).HasMaxLength(200);
            a.Property(p => p.City).HasMaxLength(100);
            a.Property(p => p.District).HasMaxLength(100);
            a.Property(p => p.State).HasMaxLength(100);
            a.Property(p => p.PinCode).HasMaxLength(10);
            a.Property(p => p.Country).HasMaxLength(100);
        });
        b.Ignore(x => x.StateCode);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.Gstin);
        b.HasIndex(x => x.Name);
    }
}
