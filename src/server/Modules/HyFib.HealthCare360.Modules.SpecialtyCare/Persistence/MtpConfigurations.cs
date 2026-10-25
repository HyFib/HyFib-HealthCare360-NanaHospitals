using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Persistence;

internal sealed class MtpCaseConfiguration : IEntityTypeConfiguration<MtpCase>
{
    public void Configure(EntityTypeBuilder<MtpCase> b)
    {
        b.ToTable("mtp_cases", SpecialtyCareSchema.Name);
        b.Property(x => x.SerialNumber).HasMaxLength(24);
        b.Property(x => x.ApprovalNumber).HasMaxLength(64);
        b.Property(x => x.GuardianName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.GuardianRelation).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.MedicalBoardReference).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.Opinions).HasJsonRows();
        b.Property(x => x.PerformedBy).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Outcome).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.Remarks).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.CancelReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Ignore(x => x.IsClosed);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SerialNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.TerminatedOn });
        b.HasIndex(x => x.PatientId);
    }
}
