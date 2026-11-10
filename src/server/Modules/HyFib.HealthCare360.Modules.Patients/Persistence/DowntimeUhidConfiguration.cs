using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Persistence;

internal sealed class DowntimeUhidBlockConfiguration : IEntityTypeConfiguration<DowntimeUhidBlock>
{
    public void Configure(EntityTypeBuilder<DowntimeUhidBlock> b)
    {
        b.ToTable("downtime_uhid_blocks", PatientsSchema.Name);
        b.Property(x => x.Label).HasMaxLength(100);
        b.Property(x => x.FirstUhid).HasMaxLength(20);
        b.Property(x => x.LastUhid).HasMaxLength(20);
        b.Property(x => x.IssuedByName).HasMaxLength(200);
        b.HasIndex(x => new { x.FacilityId, x.CreatedAt });
    }
}

internal sealed class DowntimeUhidConfiguration : IEntityTypeConfiguration<DowntimeUhid>
{
    public void Configure(EntityTypeBuilder<DowntimeUhid> b)
    {
        b.ToTable("downtime_uhids", PatientsSchema.Name);
        b.Property(x => x.Uhid).HasMaxLength(20);
        b.Property(x => x.EnteredByName).HasMaxLength(200);
        b.Property(x => x.VoidReason).HasMaxLength(300);
        b.Property(x => x.VoidedByName).HasMaxLength(200);
        b.Ignore(x => x.Status);
        b.HasOne<DowntimeUhidBlock>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Uhid).IsUnique();
        b.HasIndex(x => new { x.BlockId, x.Position }).IsUnique();
        b.HasIndex(x => x.PatientId).IsUnique().HasFilter("patient_id IS NOT NULL");
    }
}

internal sealed class DowntimeNumberBlockConfiguration : IEntityTypeConfiguration<DowntimeNumberBlock>
{
    public void Configure(EntityTypeBuilder<DowntimeNumberBlock> b)
    {
        b.ToTable("downtime_number_blocks", PatientsSchema.Name);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Counter).HasMaxLength(100);
        b.Property(x => x.FirstNumber).HasMaxLength(24);
        b.Property(x => x.LastNumber).HasMaxLength(24);
        b.Property(x => x.IssuedByName).HasMaxLength(200);
        b.Property(x => x.ResetPolicy).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.Period).HasMaxLength(16);
        b.Property(x => x.ReturnedByName).HasMaxLength(200);
        b.Property(x => x.ReturnNote).HasMaxLength(300);
        b.HasIndex(x => new { x.FacilityId, x.Kind, x.CreatedAt });
    }
}

internal sealed class DowntimeNumberConfiguration : IEntityTypeConfiguration<DowntimeNumber>
{
    public void Configure(EntityTypeBuilder<DowntimeNumber> b)
    {
        b.ToTable("downtime_numbers", PatientsSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.EntityType).HasMaxLength(64);
        b.Property(x => x.EnteredByName).HasMaxLength(200);
        b.Property(x => x.VoidReason).HasMaxLength(300);
        b.Property(x => x.VoidedByName).HasMaxLength(200);
        b.Ignore(x => x.Status);
        b.HasOne<DowntimeNumberBlock>().WithMany().HasForeignKey(x => x.BlockId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BlockId, x.Position }).IsUnique();

        // Numbers of different series may look alike (bills and receipts are different tables), so a number is unique
        // within its block's kind; the block's kind is not on the row, so per block plus the lookup index.
        b.HasIndex(x => new { x.BlockId, x.Number }).IsUnique();
        b.HasIndex(x => x.Number);
        b.HasIndex(x => new { x.EntityType, x.EntityId }).IsUnique().HasFilter("entity_id IS NOT NULL");
    }
}

internal sealed class LateBackEntryGrantConfiguration : IEntityTypeConfiguration<LateBackEntryGrant>
{
    public void Configure(EntityTypeBuilder<LateBackEntryGrant> b)
    {
        b.ToTable("late_back_entry_grants", PatientsSchema.Name);
        b.Property(x => x.Record).HasConversion<string>().HasMaxLength(24);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.HasIndex(x => new { x.FacilityId, x.RequestedBy, x.Record, x.ExpiresAt });
        b.HasIndex(x => x.ApprovalRequestId).IsUnique();
    }
}

internal sealed class DowntimePackSnapshotConfiguration : IEntityTypeConfiguration<DowntimePackSnapshot>
{
    public void Configure(EntityTypeBuilder<DowntimePackSnapshot> b)
    {
        b.ToTable("downtime_pack_snapshots", PatientsSchema.Name);
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.GeneratedByName).HasMaxLength(200);
        b.Property(x => x.Content).HasColumnType("jsonb");
        b.HasIndex(x => new { x.FacilityId, x.GeneratedAt });
    }
}
