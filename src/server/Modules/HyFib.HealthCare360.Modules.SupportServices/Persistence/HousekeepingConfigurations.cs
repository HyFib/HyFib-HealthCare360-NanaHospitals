using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Housekeeping;

namespace HyFib.HealthCare360.Modules.SupportServices.Persistence;

internal sealed class CleaningAreaConfiguration : IEntityTypeConfiguration<CleaningArea>
{
    public void Configure(EntityTypeBuilder<CleaningArea> b)
    {
        b.ToTable("cleaning_areas", SupportServicesSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(120);
        b.Property(x => x.WeekDays).HasJsonRows();
        b.Property(x => x.Checklist).HasJsonRows();
        b.HasOne<Ward>().WithMany().HasForeignKey(x => x.WardId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique().HasFilter("is_deleted = false");
        b.HasIndex(x => x.WardId);
    }
}

internal sealed class CleaningRecordConfiguration : IEntityTypeConfiguration<CleaningRecord>
{
    public void Configure(EntityTypeBuilder<CleaningRecord> b)
    {
        b.ToTable("cleaning_records", SupportServicesSchema.Name);
        b.Property(x => x.CleanedByName).HasMaxLength(200);
        b.Property(x => x.Checklist).HasJsonRows();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.VerifiedByName).HasMaxLength(200);
        b.Property(x => x.VerificationRemarks).HasMaxLength(500);
        b.HasOne<CleaningArea>().WithMany().HasForeignKey(x => x.AreaId).OnDelete(DeleteBehavior.Restrict);

        // One cleaning of a slot stands; one sent back to be redone makes way for the next.
        b.HasIndex(x => new { x.AreaId, x.Date, x.Slot }).IsUnique().HasFilter("status <> 'Redo' AND is_deleted = false");
        b.HasIndex(x => new { x.FacilityId, x.Date });
    }
}

internal sealed class CleaningRequestConfiguration : IEntityTypeConfiguration<CleaningRequest>
{
    public void Configure(EntityTypeBuilder<CleaningRequest> b)
    {
        b.ToTable("cleaning_requests", SupportServicesSchema.Name);
        b.Property(x => x.Location).HasMaxLength(200);
        b.Property(x => x.Details).HasMaxLength(500);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.AcknowledgedByName).HasMaxLength(200);
        b.Property(x => x.CompletedByName).HasMaxLength(200);
        b.Property(x => x.Checklist).HasJsonRows();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.VerifiedByName).HasMaxLength(200);
        b.Property(x => x.VerificationRemarks).HasMaxLength(500);
        b.Ignore(x => x.ResponseMinutes);
        b.Ignore(x => x.CompletionMinutes);
        b.HasOne<Ward>().WithMany().HasForeignKey(x => x.WardId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Status, x.RequestedAt });
        b.HasIndex(x => x.WardId);
    }
}
