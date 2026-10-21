using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Persistence;

/// <summary>Unique indexes on soft-deletable rows apply to live rows only, so a removed row never blocks re-creation.</summary>
internal static class LiveRows
{
    public const string Filter = "is_deleted = false";
}

internal sealed class RpmProgramConfiguration : IEntityTypeConfiguration<RpmProgram>
{
    public void Configure(EntityTypeBuilder<RpmProgram> b)
    {
        b.ToTable("rpm_programs", VirtualCareSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => x.Code).IsUnique().HasFilter(LiveRows.Filter);
    }
}

internal sealed class RpmEnrollmentConfiguration : IEntityTypeConfiguration<RpmEnrollment>
{
    public void Configure(EntityTypeBuilder<RpmEnrollment> b)
    {
        b.ToTable("rpm_enrollments", VirtualCareSchema.Name);
        b.Property(x => x.Goals).HasMaxLength(1000);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.StatusReason).HasMaxLength(300);
        b.Ignore(x => x.IsClosed);
        b.Ignore(x => x.RaisesAlerts);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<RpmProgram>().WithMany().HasForeignKey(x => x.ProgramId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.Status });
        b.HasIndex(x => new { x.FacilityId, x.Status });
    }
}

internal sealed class RpmDeviceConfiguration : IEntityTypeConfiguration<RpmDevice>
{
    public void Configure(EntityTypeBuilder<RpmDevice> b)
    {
        b.ToTable("rpm_devices", VirtualCareSchema.Name);
        b.Property(x => x.SerialNumber).HasMaxLength(64);
        b.Property(x => x.Manufacturer).HasMaxLength(100);
        b.Property(x => x.Model).HasMaxLength(100);
        b.Property(x => x.ApiKeyHash).HasMaxLength(64);
        b.HasIndex(x => x.SerialNumber).IsUnique().HasFilter(LiveRows.Filter);
        b.HasIndex(x => x.EnrollmentId);
        b.HasIndex(x => new { x.IsActive, x.LastSeenAt });
    }
}

/// <remarks>
/// The table is partitioned by month of <see cref="RpmReading.MeasuredAt"/> (SCAL-04, migration
/// <c>PartitionHighVolumeTables</c>). Its primary key is (id, measured_at), in the database and in the model, as
/// PostgreSQL requires the partition key in every unique index: an update EF writes then names the reading's month, which
/// PostgreSQL reads alone. Nothing references a reading by a foreign key (RPM alerts keep its id without one).
/// </remarks>
internal sealed class RpmReadingConfiguration : IEntityTypeConfiguration<RpmReading>
{
    public static readonly PartitionedTable Partitions = new(VirtualCareSchema.Name, "rpm_readings", "measured_at");

    public void Configure(EntityTypeBuilder<RpmReading> b)
    {
        b.ToTable("rpm_readings", VirtualCareSchema.Name);

        // As in the database, which partitions the readings by month (SCAL-04): an update then names the row's month.
        b.HasKey(x => new { x.Id, x.MeasuredAt });
        b.Property(x => x.Unit).HasMaxLength(16);
        b.Property(x => x.TextValue).HasMaxLength(200);
        b.HasOne<RpmEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);

        // A device retrying a batch must not create duplicates.
        b.HasIndex(x => new { x.EnrollmentId, x.Type, x.MeasuredAt }).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.MeasuredAt });
        b.HasIndex(x => new { x.FacilityId, x.ReceivedAt });
    }
}

internal sealed class RpmThresholdConfiguration : IEntityTypeConfiguration<RpmThreshold>
{
    public void Configure(EntityTypeBuilder<RpmThreshold> b)
    {
        b.ToTable("rpm_thresholds", VirtualCareSchema.Name);
        b.Ignore(x => x.Range);
        b.HasOne<RpmEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.EnrollmentId, x.Type }).IsUnique().HasFilter(LiveRows.Filter);
    }
}

internal sealed class RpmAlertConfiguration : IEntityTypeConfiguration<RpmAlert>
{
    public void Configure(EntityTypeBuilder<RpmAlert> b)
    {
        b.ToTable("rpm_alerts", VirtualCareSchema.Name);
        b.Property(x => x.Unit).HasMaxLength(16);
        b.Property(x => x.Message).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(RpmAlert.MaxNotesLength);
        b.Ignore(x => x.IsActive);
        b.HasOne<RpmEnrollment>().WithMany().HasForeignKey(x => x.EnrollmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Status, x.Severity });
        b.HasIndex(x => new { x.EnrollmentId, x.Status });
        b.HasIndex(x => x.RaisedAt);
    }
}
