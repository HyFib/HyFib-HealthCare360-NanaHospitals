using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Mortuary;

namespace HyFib.HealthCare360.Modules.SupportServices.Persistence;

internal sealed class MortuarySlotConfiguration : IEntityTypeConfiguration<MortuarySlot>
{
    public void Configure(EntityTypeBuilder<MortuarySlot> b)
    {
        b.ToTable("mortuary_slots", SupportServicesSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Chamber).HasMaxLength(100);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique().HasFilter("is_deleted = false");
    }
}

internal sealed class BodyRecordConfiguration : IEntityTypeConfiguration<BodyRecord>
{
    public void Configure(EntityTypeBuilder<BodyRecord> b)
    {
        b.ToTable("body_records", SupportServicesSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Sex).HasMaxLength(16);
        b.Property(x => x.ReceivedByName).HasMaxLength(200);
        b.Property(x => x.BroughtBy).HasMaxLength(200);
        b.Property(x => x.MlcNumber).HasMaxLength(64);
        b.Property(x => x.PoliceStation).HasMaxLength(200);
        b.Property(x => x.IdentificationMarks).HasMaxLength(1000);
        b.Property(x => x.Belongings).HasMaxLength(2000);
        b.Property(x => x.SlotCode).HasMaxLength(16);
        b.Property(x => x.ReceivedBy).HasMaxLength(200);
        b.Property(x => x.Relationship).HasMaxLength(64);
        b.Property(x => x.IdProof).HasMaxLength(200);
        b.Property(x => x.PoliceClearance).HasMaxLength(200);
        b.Property(x => x.ReleaseNotes).HasMaxLength(1000);
        b.Property(x => x.ReleasedByName).HasMaxLength(200);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MortuarySlot>().WithMany().HasForeignKey(x => x.SlotId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.ReceivedAt });

        // A slot holds one body at a time.
        b.HasIndex(x => x.SlotId).IsUnique().HasFilter("slot_id IS NOT NULL AND status = 'InStorage' AND is_deleted = false");
    }
}
