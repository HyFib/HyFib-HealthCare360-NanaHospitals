using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Persistence;

internal static class SpecialtyCareSchema
{
    public const string Name = "specialtycare";

    /// <summary>Common lengths so every text column is bounded consistently.</summary>
    public const int ShortText = 100;
    public const int Name200 = 200;
    public const int Reason = 500;
    public const int Note = 2000;
    public const int LongNote = 8000;
}

internal sealed class AdmissionRecordConfiguration : IEntityTypeConfiguration<AdmissionRecord>
{
    public void Configure(EntityTypeBuilder<AdmissionRecord> b)
    {
        b.ToTable("admissions", SpecialtyCareSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.AdmissionNumber).HasMaxLength(32);
        b.Ignore(x => x.IsActive);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.DischargedAt });
    }
}
