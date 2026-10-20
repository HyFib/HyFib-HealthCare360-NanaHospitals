using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Persistence;

internal sealed class PatientIdentityCheckConfiguration : IEntityTypeConfiguration<PatientIdentityCheck>
{
    public void Configure(EntityTypeBuilder<PatientIdentityCheck> b)
    {
        b.ToTable("patient_identity_checks", PatientsSchema.Name);
        b.Property(x => x.Message).HasMaxLength(400);
        b.Property(x => x.AadhaarLast4).HasMaxLength(4);
        b.Property(x => x.Provider).HasMaxLength(16);
        b.Property(x => x.ErrorCode).HasMaxLength(16);
        b.Property(x => x.TransactionId).HasMaxLength(50);
        b.Property(x => x.ResponseCode).HasMaxLength(64);
        b.Property(x => x.DeviceProviderId).HasMaxLength(64);
        b.Property(x => x.DeviceModelId).HasMaxLength(64);
        b.Property(x => x.CheckedByName).HasMaxLength(200);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt });
    }
}
