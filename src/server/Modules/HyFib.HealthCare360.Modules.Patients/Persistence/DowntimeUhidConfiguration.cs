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
