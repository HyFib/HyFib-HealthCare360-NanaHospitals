using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Persistence;

internal sealed class AdmissionConfiguration : IEntityTypeConfiguration<Admission>
{
    /// <summary>Statuses in which the patient still holds a bed (kept in sync with <see cref="Admission.IsActive"/>).</summary>
    private const string ActiveFilter = "status IN ('Admitted', 'DischargeInitiated') AND NOT is_deleted";

    public void Configure(EntityTypeBuilder<Admission> b)
    {
        b.ToTable("admissions", InpatientSchema.Name);
        b.Property(x => x.AdmissionNumber).HasMaxLength(24);
        b.Property(x => x.ProvisionalDiagnosis).HasMaxLength(500);
        b.Property(x => x.ReferredBy).HasMaxLength(200);
        b.Property(x => x.PolicyNumberCipher).HasMaxLength(256);
        b.Property(x => x.PolicyNumberMasked).HasMaxLength(64);
        b.Property(x => x.MlcNumber).HasMaxLength(64);
        b.Property(x => x.AttendantName).HasMaxLength(200);
        b.Property(x => x.AttendantRelation).HasMaxLength(64);
        b.Property(x => x.AttendantPhone).HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.DischargeNotes).HasMaxLength(2000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Ignore(x => x.IsActive);

        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Ward>().WithMany().HasForeignKey(x => x.WardId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bed>().WithMany().HasForeignKey(x => x.BedId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.AdmissionNumber).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.AdmittedAt });
        b.HasIndex(x => new { x.Status, x.AdmittedAt });
        b.HasIndex(x => x.DischargedAt);
        b.HasIndex(x => x.VisitId);
        b.HasIndex(x => new { x.ConsultantPractitionerId, x.Status });
        // One active admission per patient and per bed, enforced by the database even under concurrent requests.
        b.HasIndex(x => x.PatientId).IsUnique().HasFilter(ActiveFilter).HasDatabaseName("ux_admissions_active_patient");
        b.HasIndex(x => x.BedId).IsUnique().HasFilter("bed_id IS NOT NULL AND " + ActiveFilter).HasDatabaseName("ux_admissions_active_bed");
    }
}

internal sealed class BedAssignmentConfiguration : IEntityTypeConfiguration<BedAssignment>
{
    public void Configure(EntityTypeBuilder<BedAssignment> b)
    {
        b.ToTable("bed_assignments", InpatientSchema.Name);
        b.Property(x => x.TransferReason).HasMaxLength(300);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bed>().WithMany().HasForeignKey(x => x.BedId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.FromAt });
        b.HasIndex(x => new { x.BedId, x.FromAt });
        b.HasIndex(x => new { x.WardId, x.FromAt });
    }
}

internal sealed class BedDayChargeConfiguration : IEntityTypeConfiguration<BedDayCharge>
{
    public void Configure(EntityTypeBuilder<BedDayCharge> b)
    {
        b.ToTable("bed_day_charges", InpatientSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.SourceReference).HasMaxLength(64);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.ChargeDate, x.Kind }).IsUnique();
    }
}

internal sealed class ConsultantVisitConfiguration : IEntityTypeConfiguration<ConsultantVisit>
{
    public void Configure(EntityTypeBuilder<ConsultantVisit> b)
    {
        b.ToTable("consultant_visits", InpatientSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.ChargeError).HasMaxLength(300);
        b.Ignore(x => x.ServiceCode);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.VisitedAt });
        b.HasIndex(x => new { x.PractitionerId, x.VisitedAt });
        // Lets the hourly job find visits whose charge Billing has not accepted yet.
        b.HasIndex(x => x.VisitedAt).HasFilter("charge_id IS NULL AND NOT is_deleted").HasDatabaseName("ix_consultant_visits_uncharged");
    }
}
