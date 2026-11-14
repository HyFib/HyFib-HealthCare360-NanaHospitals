using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Patients.Persistence;

internal sealed class PortalRecordConfirmationConfiguration : IEntityTypeConfiguration<PortalRecordConfirmation>
{
    public void Configure(EntityTypeBuilder<PortalRecordConfirmation> b)
    {
        b.ToTable("portal_record_confirmations", PatientsSchema.Name);
        b.Property(x => x.AccountPhone).HasMaxLength(20);

        // The wrong answers a number gave in the last day, counted before each try.
        b.HasIndex(x => new { x.AccountPhone, x.CreatedAt });
    }
}
