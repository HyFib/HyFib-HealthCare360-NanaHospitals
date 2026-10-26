using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Linen;

namespace HyFib.HealthCare360.Modules.SupportServices.Persistence;

internal sealed class LinenItemConfiguration : IEntityTypeConfiguration<LinenItem>
{
    public void Configure(EntityTypeBuilder<LinenItem> b)
    {
        b.ToTable("linen_items", SupportServicesSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique().HasFilter("is_deleted = false");
    }
}

internal sealed class LinenHoldingConfiguration : IEntityTypeConfiguration<LinenHolding>
{
    public void Configure(EntityTypeBuilder<LinenHolding> b)
    {
        b.ToTable("linen_holdings", SupportServicesSchema.Name, t => t.HasCheckConstraint("ck_linen_holdings_quantity", "quantity >= 0"));
        b.HasOne<LinenItem>().WithMany().HasForeignKey(x => x.LinenItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ward>().WithMany().HasForeignKey(x => x.WardId).OnDelete(DeleteBehavior.Restrict);

        // One count per item at each place.
        b.HasIndex(x => new { x.FacilityId, x.LinenItemId, x.Location, x.WardId, x.Infected }).IsUnique().AreNullsDistinct(false)
            .HasFilter("is_deleted = false");
    }
}

internal sealed class LinenMovementConfiguration : IEntityTypeConfiguration<LinenMovement>
{
    public void Configure(EntityTypeBuilder<LinenMovement> b)
    {
        b.ToTable("linen_movements", SupportServicesSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.WardName).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.ByName).HasMaxLength(200);
        b.Ignore(x => x.Pieces);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.LinenMovementId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.FacilityId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.At });
    }
}

internal sealed class LinenMovementLineConfiguration : IEntityTypeConfiguration<LinenMovementLine>
{
    public void Configure(EntityTypeBuilder<LinenMovementLine> b)
    {
        b.ToTable("linen_movement_lines", SupportServicesSchema.Name);
        b.Property(x => x.ItemName).HasMaxLength(100);
        b.HasOne<LinenItem>().WithMany().HasForeignKey(x => x.LinenItemId).OnDelete(DeleteBehavior.Restrict);
    }
}
