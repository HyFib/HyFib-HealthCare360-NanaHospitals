using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.Analytics.Persistence;

internal static class AnalyticsSchema
{
    public const string Name = "analytics";
}

internal sealed class DashboardConfiguration : IEntityTypeConfiguration<Dashboard>
{
    public void Configure(EntityTypeBuilder<Dashboard> b)
    {
        b.ToTable("dashboards", AnalyticsSchema.Name);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.OwnerName).HasMaxLength(200);
        b.Property(x => x.LayoutJson).HasColumnType("jsonb");
        b.Ignore(x => x.IsTemplate);
        b.HasIndex(x => new { x.OwnerUserId, x.Name });
        b.HasIndex(x => x.IsShared);
    }
}

internal sealed class ReportScheduleConfiguration : IEntityTypeConfiguration<ReportSchedule>
{
    public void Configure(EntityTypeBuilder<ReportSchedule> b)
    {
        b.ToTable("report_schedules", AnalyticsSchema.Name);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.ReportId).HasMaxLength(64);
        b.Property(x => x.ParametersJson).HasColumnType("jsonb");
        b.Property(x => x.OwnerName).HasMaxLength(200);
        b.Property(x => x.LastError).HasMaxLength(500);
        b.Property(x => x.ExtractName).HasMaxLength(ReportSchedule.MaxExtractNameLength);
        b.Ignore(x => x.IsExtract);
        b.Ignore(x => x.IsFailing);

        // The job looks for due schedules every minute.
        b.HasIndex(x => new { x.IsActive, x.NextRunAt });
        b.HasIndex(x => x.OwnerUserId);

        // Two extracts of a facility never write the same file.
        b.HasIndex(x => new { x.FacilityId, x.ExtractName }).IsUnique().HasFilter("extract_name IS NOT NULL AND is_deleted = false");
    }
}

internal sealed class ReportDeliveryConfiguration : IEntityTypeConfiguration<ReportDelivery>
{
    public void Configure(EntityTypeBuilder<ReportDelivery> b)
    {
        b.ToTable("report_deliveries", AnalyticsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ReportId).HasMaxLength(64);
        b.Property(x => x.ReportTitle).HasMaxLength(200);
        b.Property(x => x.FileName).HasMaxLength(200);
        b.Property(x => x.ContentType).HasMaxLength(100);
        b.Property(x => x.Error).HasMaxLength(1000);

        // Moves to the current key after a rotation (SRS SEC-07), since it may be kept for years.
        b.Property(x => x.Content).IsProtectedBytes(ReportDeliveryService.FilePurpose);
        b.HasMany(x => x.Recipients).WithOne().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ScheduleId, x.CreatedAt });

        // Retention purge scans old deliveries that still hold a file.
        b.HasIndex(x => x.CreatedAt);
    }
}

internal sealed class ReportDeliveryRecipientConfiguration : IEntityTypeConfiguration<ReportDeliveryRecipient>
{
    public void Configure(EntityTypeBuilder<ReportDeliveryRecipient> b)
    {
        b.ToTable("report_delivery_recipients", AnalyticsSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Email).HasMaxLength(254);
        b.Property(x => x.LastError).HasMaxLength(500);
        b.HasIndex(x => x.DeliveryId);
    }
}
