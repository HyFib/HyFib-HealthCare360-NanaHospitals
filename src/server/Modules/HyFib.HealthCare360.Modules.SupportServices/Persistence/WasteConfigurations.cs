using HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;

namespace HyFib.HealthCare360.Modules.SupportServices.Persistence;

internal sealed class WasteBagConfiguration : IEntityTypeConfiguration<WasteBag>
{
    public void Configure(EntityTypeBuilder<WasteBag> b)
    {
        b.ToTable("waste_bags", SupportServicesSchema.Name);
        b.Property(x => x.Barcode).HasMaxLength(32);
        b.Property(x => x.Source).HasMaxLength(120);
        b.Property(x => x.WeightKg).HasPrecision(9, 3);
        b.Property(x => x.CollectedByName).HasMaxLength(200);
        b.Property(x => x.VoidReason).HasMaxLength(300);
        b.HasIndex(x => x.Barcode).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.CollectedAt });
        b.HasIndex(x => x.HandoverId);
    }
}

internal sealed class WasteHandoverConfiguration : IEntityTypeConfiguration<WasteHandover>
{
    public void Configure(EntityTypeBuilder<WasteHandover> b)
    {
        b.ToTable("waste_handovers", SupportServicesSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Cbwtf).HasMaxLength(200);
        b.Property(x => x.VehicleNumber).HasMaxLength(20);
        b.Property(x => x.ReceivedBy).HasMaxLength(120);
        b.Property(x => x.ReceiptNumber).HasMaxLength(40);
        b.Property(x => x.HandedOverByName).HasMaxLength(200);
        b.Property(x => x.TotalKg).HasPrecision(11, 3);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.HandedOverAt });
    }
}
