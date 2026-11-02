using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class ChargeRefusalConfiguration : IEntityTypeConfiguration<ChargeRefusal>
{
    public void Configure(EntityTypeBuilder<ChargeRefusal> b)
    {
        b.ToTable("charge_refusals", BillingSchema.Name);
        b.Property(x => x.SourceModule).HasMaxLength(64);
        b.Property(x => x.SourceReference).HasMaxLength(128);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.Code).HasMaxLength(64);
        b.Property(x => x.Message).HasMaxLength(300);

        // One refusal per source line, as one charge per source line.
        b.HasIndex(x => new { x.SourceModule, x.SourceReference }).IsUnique();
        b.HasIndex(x => x.AdmissionId);
    }
}
