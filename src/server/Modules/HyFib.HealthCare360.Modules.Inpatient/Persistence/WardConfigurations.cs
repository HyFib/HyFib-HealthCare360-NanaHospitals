using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Persistence;

internal static class InpatientSchema
{
    public const string Name = "inpatient";
}

internal sealed class WardConfiguration : IEntityTypeConfiguration<Ward>
{
    public void Configure(EntityTypeBuilder<Ward> b)
    {
        b.ToTable("wards", InpatientSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(128);
        b.Property(x => x.Floor).HasMaxLength(32);
        b.Property(x => x.Block).HasMaxLength(64);
        b.Property(x => x.NurseStation).HasMaxLength(64);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Ignore(x => x.IsCriticalCare);
        b.HasOne<ServiceItem>().WithMany().HasForeignKey(x => x.BedServiceItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.IsActive, x.DisplayOrder });
    }
}

internal sealed class RoomConfiguration : IEntityTypeConfiguration<Room>
{
    public void Configure(EntityTypeBuilder<Room> b)
    {
        b.ToTable("rooms", InpatientSchema.Name);
        b.Property(x => x.Number).HasMaxLength(16);
        b.Property(x => x.Description).HasMaxLength(200);
        b.HasOne<Ward>().WithMany().HasForeignKey(x => x.WardId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WardId, x.Number }).IsUnique();
    }
}

internal sealed class BedConfiguration : IEntityTypeConfiguration<Bed>
{
    public void Configure(EntityTypeBuilder<Bed> b)
    {
        b.ToTable("beds", InpatientSchema.Name);
        b.Property(x => x.Label).HasMaxLength(24);
        b.Property(x => x.StatusReason).HasMaxLength(300);
        b.HasOne<Ward>().WithMany().HasForeignKey(x => x.WardId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Room>().WithMany().HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Label }).IsUnique();
        b.HasIndex(x => new { x.WardId, x.Status });
        // A bed can hold only one admission at a time.
        b.HasIndex(x => x.CurrentAdmissionId).IsUnique().HasFilter("current_admission_id IS NOT NULL");
    }
}

internal sealed class HousekeepingTaskConfiguration : IEntityTypeConfiguration<HousekeepingTask>
{
    public void Configure(EntityTypeBuilder<HousekeepingTask> b)
    {
        b.ToTable("housekeeping_tasks", InpatientSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Bed>().WithMany().HasForeignKey(x => x.BedId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Status, x.RequestedAt });
        b.HasIndex(x => new { x.BedId, x.Status });
        b.HasIndex(x => x.CompletedAt);
    }
}
