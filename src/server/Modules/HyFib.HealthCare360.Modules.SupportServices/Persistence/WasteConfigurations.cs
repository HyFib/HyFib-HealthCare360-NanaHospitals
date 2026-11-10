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
        b.Property(x => x.HandoverWeightKg).HasPrecision(9, 3);
        b.Ignore(x => x.WeightDifferenceKg);
        b.Ignore(x => x.WeightMismatch);
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

internal sealed class WasteAccidentConfiguration : IEntityTypeConfiguration<WasteAccident>
{
    public void Configure(EntityTypeBuilder<WasteAccident> b)
    {
        b.ToTable("waste_accidents", SupportServicesSchema.Name);
        b.Property(x => x.Place).HasMaxLength(200);
        b.Property(x => x.Sequence).HasMaxLength(2000);
        b.Property(x => x.WasteInvolved).HasMaxLength(500);
        b.Property(x => x.Effects).HasMaxLength(2000);
        b.Property(x => x.EmergencyMeasures).HasMaxLength(2000);
        b.Property(x => x.StepsToAlleviate).HasMaxLength(2000);
        b.Property(x => x.StepsToPrevent).HasMaxLength(2000);
        b.Property(x => x.ReportedByName).HasMaxLength(200);
        b.Property(x => x.ReportedByDesignation).HasMaxLength(120);
        b.HasIndex(x => new { x.FacilityId, x.OccurredAt });
    }
}
