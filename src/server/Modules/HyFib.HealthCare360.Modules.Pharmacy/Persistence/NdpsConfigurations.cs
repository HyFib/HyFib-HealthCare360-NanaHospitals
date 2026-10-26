using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Persistence;

internal sealed class NarcoticCountConfiguration : IEntityTypeConfiguration<NarcoticCount>
{
    public void Configure(EntityTypeBuilder<NarcoticCount> b)
    {
        b.ToTable("narcotic_counts", PharmacySchema.Name);
        b.Property(x => x.CountedByName).HasMaxLength(200);
        b.Property(x => x.WitnessedByName).HasMaxLength(200);
        b.Property(x => x.Explanation).HasMaxLength(1000);
        b.Ignore(x => x.HasDifference);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.NarcoticCountId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.FacilityId, x.StoreId, x.CountedAt });
    }
}

internal sealed class NarcoticCountLineConfiguration : IEntityTypeConfiguration<NarcoticCountLine>
{
    public void Configure(EntityTypeBuilder<NarcoticCountLine> b)
    {
        b.ToTable("narcotic_count_lines", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.RegisterBalance).IsQuantity();
        b.Property(x => x.Counted).IsQuantity();
        b.Ignore(x => x.Difference);
    }
}
