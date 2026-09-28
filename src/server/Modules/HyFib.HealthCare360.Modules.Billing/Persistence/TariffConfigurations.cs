using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal static class BillingSchema
{
    public const string Name = "billing";
}

internal sealed class TariffPlanConfiguration : IEntityTypeConfiguration<TariffPlan>
{
    public void Configure(EntityTypeBuilder<TariffPlan> b)
    {
        b.ToTable("tariff_plans", BillingSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(128);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.DefaultDiscountPercent).HasPrecision(5, 2);
        b.HasMany(x => x.BedMultipliers).WithOne().HasForeignKey(x => x.TariffPlanId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.Type, x.IsDefault, x.IsActive });
        b.HasIndex(x => x.PayerId);
    }
}

internal sealed class TariffBedMultiplierConfiguration : IEntityTypeConfiguration<TariffBedMultiplier>
{
    public void Configure(EntityTypeBuilder<TariffBedMultiplier> b)
    {
        b.ToTable("tariff_bed_multipliers", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Multiplier).HasPrecision(9, 4);
        b.HasOne<ServiceItem>().WithMany().HasForeignKey(x => x.BedServiceItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TariffPlanId, x.BedServiceItemId }).IsUnique();
    }
}

internal sealed class TariffPriceConfiguration : IEntityTypeConfiguration<TariffPrice>
{
    public void Configure(EntityTypeBuilder<TariffPrice> b)
    {
        b.ToTable("tariff_prices", BillingSchema.Name);
        b.HasOne<TariffPlan>().WithMany().HasForeignKey(x => x.TariffPlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ServiceItem>().WithMany().HasForeignKey(x => x.ServiceItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TariffPlanId, x.ServiceItemId, x.EffectiveFrom }).IsUnique().HasFilter("is_deleted = false");
        b.HasIndex(x => x.ServiceItemId);
    }
}
