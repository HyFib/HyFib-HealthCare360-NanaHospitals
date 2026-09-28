using HyFib.HealthCare360.Modules.AI.Domain;

namespace HyFib.HealthCare360.Modules.AI.Persistence;

internal static class AiSchema
{
    public const string Name = "ai";
}

internal sealed class AiUsageConfiguration : IEntityTypeConfiguration<AiUsage>
{
    public void Configure(EntityTypeBuilder<AiUsage> b)
    {
        b.ToTable("usage_records", AiSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Purpose).HasMaxLength(64);
        b.Property(x => x.Provider).HasMaxLength(32);
        b.Property(x => x.Model).HasMaxLength(64);
        b.Property(x => x.Error).HasMaxLength(500);
        b.HasIndex(x => new { x.UserId, x.CreatedAt });
        b.HasIndex(x => x.CreatedAt);
    }
}
