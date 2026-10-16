using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Persistence;

internal static class ClinicalSchema
{
    public const string Name = "clinical";
}

internal sealed class EncounterConfiguration : IEntityTypeConfiguration<Encounter>
{
    public void Configure(EntityTypeBuilder<Encounter> b)
    {
        b.ToTable("encounters", ClinicalSchema.Name);
        b.Property(x => x.ChiefComplaint).HasMaxLength(1000);
        b.Property(x => x.HistoryOfPresentIllness).HasMaxLength(8000);
        b.Property(x => x.Examination).HasMaxLength(8000);
        b.Property(x => x.Assessment).HasMaxLength(4000);
        b.Property(x => x.Plan).HasMaxLength(8000);
        b.Property(x => x.FollowUpAdvice).HasMaxLength(1000);
        b.Property(x => x.ReferralExternal).HasMaxLength(300);
        b.Property(x => x.ReferralReason).HasMaxLength(1000);
        b.Property(x => x.AmendmentReason).HasMaxLength(500);
        b.Ignore(x => x.Content);
        b.Ignore(x => x.IsLocked);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Amendments).WithOne().HasForeignKey(x => x.EncounterId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.PatientId, x.StartedAt });
        b.HasIndex(x => new { x.PractitionerId, x.Status, x.StartedAt });
        b.HasIndex(x => x.VisitId);
        b.HasIndex(x => x.AdmissionId);
    }
}

internal sealed class EncounterAmendmentConfiguration : IEntityTypeConfiguration<EncounterAmendment>
{
    public void Configure(EntityTypeBuilder<EncounterAmendment> b)
    {
        b.ToTable("encounter_amendments", ClinicalSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.PreviousContent).HasColumnType("jsonb");
        b.HasIndex(x => new { x.EncounterId, x.AmendedAt });
    }
}

internal sealed class VitalSignsConfiguration : IEntityTypeConfiguration<VitalSigns>
{
    public void Configure(EntityTypeBuilder<VitalSigns> b)
    {
        b.ToTable("vital_signs", ClinicalSchema.Name);
        b.Property(x => x.Temperature).HasPrecision(5, 1);
        b.Property(x => x.HeightCm).HasPrecision(5, 1);
        b.Property(x => x.WeightKg).HasPrecision(6, 2);
        b.Property(x => x.Bmi).HasPrecision(5, 1);
        b.Property(x => x.BloodGlucose).HasPrecision(6, 1);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.EnteredInErrorReason).HasMaxLength(300);
        b.Ignore(x => x.TemperatureCelsius);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.RecordedAt });
        b.HasIndex(x => x.EncounterId);
    }
}

internal sealed class AllergyConfiguration : IEntityTypeConfiguration<Allergy>
{
    public void Configure(EntityTypeBuilder<Allergy> b)
    {
        b.ToTable("allergies", ClinicalSchema.Name);
        b.Property(x => x.Allergen).HasMaxLength(200);
        b.Property(x => x.Reaction).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.StatusReason).HasMaxLength(300);
        b.Ignore(x => x.IsActive);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.Status });
    }
}

internal sealed class NoKnownAllergiesStatementConfiguration : IEntityTypeConfiguration<NoKnownAllergiesStatement>
{
    /// <summary>Name of the one-live-statement-per-patient index (a concurrent second statement is a duplicate).</summary>
    public const string OnePerPatientIndex = "ux_no_known_allergies_patient";

    public void Configure(EntityTypeBuilder<NoKnownAllergiesStatement> b)
    {
        b.ToTable("no_known_allergies", ClinicalSchema.Name);
        b.Property(x => x.RecordedByName).HasMaxLength(200);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        // Removed statements are soft-deleted and kept as history, so uniqueness applies to live rows only.
        b.HasIndex(x => x.PatientId).IsUnique().HasFilter("is_deleted = false").HasDatabaseName(OnePerPatientIndex);
    }
}

internal sealed class PatientAlertConfiguration : IEntityTypeConfiguration<PatientAlert>
{
    public void Configure(EntityTypeBuilder<PatientAlert> b)
    {
        b.ToTable("patient_alerts", ClinicalSchema.Name);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Detail).HasMaxLength(500);
        b.Property(x => x.DeactivationReason).HasMaxLength(300);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.IsActive });
    }
}

internal sealed class DiagnosisConfiguration : IEntityTypeConfiguration<Diagnosis>
{
    public void Configure(EntityTypeBuilder<Diagnosis> b)
    {
        b.ToTable("diagnoses", ClinicalSchema.Name);
        b.Property(x => x.IcdCode).HasMaxLength(16);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Ignore(x => x.IsActiveProblem);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.Status });
        b.HasIndex(x => x.EncounterId);
        b.HasIndex(x => new { x.IcdCode, x.RecordedAt });
    }
}

internal sealed class ClinicalNoteConfiguration : IEntityTypeConfiguration<ClinicalNote>
{
    public void Configure(EntityTypeBuilder<ClinicalNote> b)
    {
        b.ToTable("clinical_notes", ClinicalSchema.Name);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Body).HasMaxLength(20000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.CreatedAt });
        b.HasIndex(x => x.EncounterId);
        b.HasIndex(x => new { x.AuthorUserId, x.IsSigned });
    }
}

internal sealed class ImmunizationConfiguration : IEntityTypeConfiguration<Immunization>
{
    public void Configure(EntityTypeBuilder<Immunization> b)
    {
        b.ToTable("immunizations", ClinicalSchema.Name);
        b.Property(x => x.VaccineName).HasMaxLength(200);
        b.Property(x => x.VaccineCode).HasMaxLength(32);
        b.Property(x => x.Route).HasMaxLength(32);
        b.Property(x => x.Site).HasMaxLength(64);
        b.Property(x => x.BatchNumber).HasMaxLength(64);
        b.Property(x => x.Manufacturer).HasMaxLength(128);
        b.Property(x => x.AdministeredBy).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.EnteredInErrorReason).HasMaxLength(300);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.AdministeredOn });
    }
}

internal sealed class ProcedureRecordConfiguration : IEntityTypeConfiguration<ProcedureRecord>
{
    public void Configure(EntityTypeBuilder<ProcedureRecord> b)
    {
        b.ToTable("procedure_records", ClinicalSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.CodeSystem).HasMaxLength(32);
        b.Property(x => x.Display).HasMaxLength(300);
        b.Property(x => x.PerformerName).HasMaxLength(200);
        b.Property(x => x.BodySite).HasMaxLength(100);
        b.Property(x => x.Outcome).HasMaxLength(1000);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.StatusReason).HasMaxLength(300);
        b.Ignore(x => x.IsEnteredInError);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.PerformedAt });
        b.HasIndex(x => x.EncounterId);

        // A completed operation is recorded once, however often the theatre's completion is retried.
        b.HasIndex(x => x.SurgeryBookingId).IsUnique().HasFilter("surgery_booking_id IS NOT NULL");
    }
}

internal sealed class ResultReviewConfiguration : IEntityTypeConfiguration<ResultReview>
{
    public void Configure(EntityTypeBuilder<ResultReview> b)
    {
        b.ToTable("result_reviews", ClinicalSchema.Name);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.ReviewNote).HasMaxLength(500);
        b.Ignore(x => x.IsReviewed);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ReportId).IsUnique();
        b.HasIndex(x => new { x.PractitionerId, x.ReviewedAt });
        b.HasIndex(x => x.PatientId);
    }
}
