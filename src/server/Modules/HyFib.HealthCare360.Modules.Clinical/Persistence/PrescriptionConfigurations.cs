using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Persistence;

internal sealed class PrescriptionConfiguration : IEntityTypeConfiguration<Prescription>
{
    public void Configure(EntityTypeBuilder<Prescription> b)
    {
        b.ToTable("prescriptions", ClinicalSchema.Name);
        b.Property(x => x.PrescriptionNumber).HasMaxLength(24);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Advice).HasMaxLength(2000);
        b.Property(x => x.VerificationToken).HasMaxLength(64);
        b.Property(x => x.PrescriberRegistrationNumber).HasMaxLength(64);
        b.Property(x => x.PrescriberRegistrationCouncil).HasMaxLength(128);
        b.Property(x => x.SafetyOverrideReason).HasMaxLength(1000);
        b.Property(x => x.SafetyAlerts).HasColumnType("jsonb");
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.SignedContentHash).HasMaxLength(64);
        b.Property(x => x.SignerName).HasMaxLength(Prescription.SignerNameMaxLength);
        b.Property(x => x.SignerSubject).HasMaxLength(Prescription.SignerDistinguishedNameMaxLength);
        b.Property(x => x.SignerSerialNumber).HasMaxLength(Prescription.SignerSerialNumberMaxLength);
        b.Property(x => x.SignerIssuer).HasMaxLength(Prescription.SignerDistinguishedNameMaxLength);
        b.Property(x => x.SignerThumbprint).HasMaxLength(Prescription.SignerThumbprintMaxLength);
        b.Property(x => x.SignerChainStatus).HasMaxLength(Prescription.SignerChainStatusMaxLength);
        b.Ignore(x => x.HasControlledDrugs);
        b.Ignore(x => x.IsDigitallySigned);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PrescriptionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.PrescriptionNumber).IsUnique();
        b.HasIndex(x => x.VerificationToken).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.Status, x.SignedAt });
        b.HasIndex(x => new { x.PractitionerId, x.Status });
        b.HasIndex(x => x.EncounterId);
        b.HasIndex(x => x.VisitId);
        b.HasIndex(x => x.AdmissionId);

        // The messages held for the doctor's DSC, which PrescriptionNoticeRelease looks through every few minutes: a few rows.
        b.HasIndex(x => x.PatientNoticeHeldAt).HasFilter("patient_notice_held_at IS NOT NULL");
    }
}

internal sealed class PrescriptionItemConfiguration : IEntityTypeConfiguration<PrescriptionItem>
{
    public void Configure(EntityTypeBuilder<PrescriptionItem> b)
    {
        b.ToTable("prescription_items", ClinicalSchema.Name);
        b.Property(x => x.DrugName).HasMaxLength(200);
        b.Property(x => x.GenericName).HasMaxLength(300);
        b.Property(x => x.Strength).HasMaxLength(64);
        b.Property(x => x.DosageForm).HasMaxLength(64);
        b.Property(x => x.Route).HasMaxLength(32);
        b.Property(x => x.Dose).HasMaxLength(64);
        b.Property(x => x.CustomFrequency).HasMaxLength(64);
        b.Property(x => x.DosesPerDay).HasPrecision(9, 4);
        b.Property(x => x.Quantity).HasPrecision(10, 2);
        b.Property(x => x.Instructions).HasMaxLength(500);
        b.Property(x => x.ControlledSchedule).HasMaxLength(8);
        b.Ignore(x => x.FrequencyText);
        b.HasIndex(x => x.PrescriptionId);
        b.HasIndex(x => x.DrugItemId);
    }
}

internal sealed class DrugInteractionConfiguration : IEntityTypeConfiguration<DrugInteraction>
{
    public void Configure(EntityTypeBuilder<DrugInteraction> b)
    {
        b.ToTable("drug_interactions", ClinicalSchema.Name);
        b.Property(x => x.DrugA).HasMaxLength(100);
        b.Property(x => x.DrugB).HasMaxLength(100);
        b.Property(x => x.Effect).HasMaxLength(500);
        b.Property(x => x.Management).HasMaxLength(500);
        b.HasIndex(x => new { x.DrugA, x.DrugB }).IsUnique();
    }
}

internal sealed class DrugDoseLimitConfiguration : IEntityTypeConfiguration<DrugDoseLimit>
{
    public void Configure(EntityTypeBuilder<DrugDoseLimit> b)
    {
        b.ToTable("drug_dose_limits", ClinicalSchema.Name);
        b.Property(x => x.GenericName).HasMaxLength(100);
        b.Property(x => x.MaxDailyDoseMg).HasPrecision(10, 3);
        b.Property(x => x.Route).HasMaxLength(32);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasIndex(x => new { x.GenericName, x.Route }).IsUnique().AreNullsDistinct(false);
    }
}

internal sealed class ControlledDrugRegisterEntryConfiguration : IEntityTypeConfiguration<ControlledDrugRegisterEntry>
{
    public void Configure(EntityTypeBuilder<ControlledDrugRegisterEntry> b)
    {
        b.ToTable("controlled_drug_register", ClinicalSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.PrescriptionNumber).HasMaxLength(24);
        b.Property(x => x.DrugName).HasMaxLength(200);
        b.Property(x => x.GenericName).HasMaxLength(300);
        b.Property(x => x.Strength).HasMaxLength(64);
        b.Property(x => x.Schedule).HasMaxLength(8);
        b.Property(x => x.Quantity).HasPrecision(10, 2);
        b.HasIndex(x => x.PrescribedAt);
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => x.PrescriptionItemId).IsUnique();
    }
}
