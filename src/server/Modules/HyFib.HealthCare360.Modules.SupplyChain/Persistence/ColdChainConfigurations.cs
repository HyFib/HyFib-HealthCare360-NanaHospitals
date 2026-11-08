using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Persistence;

internal sealed class ColdStorageUnitConfiguration : IEntityTypeConfiguration<ColdStorageUnit>
{
    public void Configure(EntityTypeBuilder<ColdStorageUnit> b)
    {
        b.ToTable("cold_storage_units", SupplyChainSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.MinCelsius).HasPrecision(5, 1);
        b.Property(x => x.MaxCelsius).HasPrecision(5, 1);
        b.Property(x => x.LastCelsius).HasPrecision(5, 1);
        b.Property(x => x.LoggerKeyHash).HasMaxLength(64);
        b.Ignore(x => x.HasLogger);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique().HasFilter("is_deleted = false");
        b.HasIndex(x => x.StoreId);
    }
}

internal sealed class TemperatureReadingConfiguration : IEntityTypeConfiguration<TemperatureReading>
{
    public void Configure(EntityTypeBuilder<TemperatureReading> b)
    {
        b.ToTable("temperature_readings", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Celsius).HasPrecision(5, 1);
        b.Property(x => x.MinCelsius).HasPrecision(5, 1);
        b.Property(x => x.MaxCelsius).HasPrecision(5, 1);
        b.Property(x => x.RecordedByName).HasMaxLength(200);
        b.Property(x => x.Note).HasMaxLength(300);
        b.HasOne<ColdStorageUnit>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UnitId, x.TakenAt });

        // A logger that resends a batch after a lost reply does not record a reading twice.
        b.HasIndex(x => new { x.UnitId, x.TakenAt, x.Source }).IsUnique().HasFilter("source = 'Logger'");
    }
}

internal sealed class ColdChainExcursionConfiguration : IEntityTypeConfiguration<ColdChainExcursion>
{
    public void Configure(EntityTypeBuilder<ColdChainExcursion> b)
    {
        b.ToTable("cold_chain_excursions", SupplyChainSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.LowestCelsius).HasPrecision(5, 1);
        b.Property(x => x.HighestCelsius).HasPrecision(5, 1);
        b.Property(x => x.MinCelsius).HasPrecision(5, 1);
        b.Property(x => x.MaxCelsius).HasPrecision(5, 1);
        b.Property(x => x.ItemsAtRisk).HasMaxLength(2000);
        b.Property(x => x.ActionTaken).HasMaxLength(1000);
        b.Property(x => x.ActionByName).HasMaxLength(200);
        b.Ignore(x => x.IsOpen);
        b.HasOne<ColdStorageUnit>().WithMany().HasForeignKey(x => x.UnitId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.UnitId, x.StartedAt });
    }
}
