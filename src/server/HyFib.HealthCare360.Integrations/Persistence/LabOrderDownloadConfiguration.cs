using HyFib.HealthCare360.Integrations.Domain;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HyFib.HealthCare360.Integrations.Persistence;

internal sealed class LabOrderDownloadConfiguration : IEntityTypeConfiguration<LabOrderDownload>
{
    public void Configure(EntityTypeBuilder<LabOrderDownload> b)
    {
        b.ToTable("lab_order_downloads", IntegrationsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.InstrumentCode).HasMaxLength(32);
        b.Property(x => x.SampleBarcode).HasMaxLength(64);
        b.Property(x => x.LastError).HasMaxLength(500);

        // A connected analyser's session looks for its next due orders every few seconds.
        b.HasIndex(x => new { x.InstrumentCode, x.Status, x.NextAttemptAt });
        b.HasIndex(x => new { x.SourceMessageId, x.InstrumentCode }).IsUnique();
        b.HasIndex(x => new { x.InstrumentCode, x.SampleBarcode });
        b.HasIndex(x => x.CreatedAt);
    }
}
