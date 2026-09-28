using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Persistence;

internal sealed class IcdCodeConfiguration : IEntityTypeConfiguration<IcdCode>
{
    public void Configure(EntityTypeBuilder<IcdCode> b)
    {
        b.ToTable("icd_codes", ClinicalSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.Release).HasMaxLength(32);
        b.Property(x => x.Chapter).HasMaxLength(8);
        b.Property(x => x.Block).HasMaxLength(16);
        b.Property(x => x.Keywords).HasMaxLength(300);
        b.Property(x => x.SearchText).HasMaxLength(700);
        b.HasIndex(x => new { x.Code, x.Release }).IsUnique();
        b.HasIndex(x => x.Block);
        // Title/keyword search with ILIKE (MRD-01) is served by a trigram index.
        b.HasIndex(x => x.SearchText).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class ProcedureCodeConfiguration : IEntityTypeConfiguration<ProcedureCode>
{
    public void Configure(EntityTypeBuilder<ProcedureCode> b)
    {
        b.ToTable("procedure_codes", ClinicalSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Title).HasMaxLength(300);
        b.Property(x => x.System).HasMaxLength(32);
        b.Property(x => x.Category).HasMaxLength(64);
        b.Property(x => x.Keywords).HasMaxLength(300);
        b.Property(x => x.SearchText).HasMaxLength(700);
        b.HasIndex(x => new { x.Code, x.System }).IsUnique();
        b.HasIndex(x => x.SearchText).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class EncounterCodingConfiguration : IEntityTypeConfiguration<EncounterCoding>
{
    public void Configure(EntityTypeBuilder<EncounterCoding> b)
    {
        b.ToTable("encounter_codings", ClinicalSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.CodingId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.AdmissionId).IsUnique();
        b.HasIndex(x => x.EncounterId).IsUnique();
        b.HasIndex(x => new { x.Status, x.EpisodeDate });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class CodingEntryConfiguration : IEntityTypeConfiguration<CodingEntry>
{
    public void Configure(EntityTypeBuilder<CodingEntry> b)
    {
        b.ToTable("coding_entries", ClinicalSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Title).HasMaxLength(300);
        b.HasIndex(x => x.CodingId);
        b.HasIndex(x => new { x.Kind, x.Code });
    }
}

internal sealed class MedicalRecordConfiguration : IEntityTypeConfiguration<MedicalRecord>
{
    public void Configure(EntityTypeBuilder<MedicalRecord> b)
    {
        b.ToTable("medical_records", ClinicalSchema.Name);
        b.Property(x => x.ArchiveLocation).HasMaxLength(200);
        b.Ignore(x => x.IsInpatient);
        b.Ignore(x => x.OpenDeficiencyCount);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Deficiencies).WithOne().HasForeignKey(x => x.MedicalRecordId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.AdmissionId).IsUnique();
        b.HasIndex(x => x.EncounterId).IsUnique();
        b.HasIndex(x => new { x.Status, x.DueDate });
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => x.RetainUntil);
    }
}

internal sealed class ChartDeficiencyConfiguration : IEntityTypeConfiguration<ChartDeficiency>
{
    public void Configure(EntityTypeBuilder<ChartDeficiency> b)
    {
        b.ToTable("chart_deficiencies", ClinicalSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.Property(x => x.ResolutionNote).HasMaxLength(500);
        b.Ignore(x => x.IsOpen);
        b.HasIndex(x => x.MedicalRecordId);
        b.HasIndex(x => new { x.AssignedPractitionerId, x.ResolvedAt });
    }
}

internal sealed class RecordRequestConfiguration : IEntityTypeConfiguration<RecordRequest>
{
    public void Configure(EntityTypeBuilder<RecordRequest> b)
    {
        b.ToTable("record_requests", ClinicalSchema.Name);
        b.Property(x => x.RequestNumber).HasMaxLength(24);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.RequestingDepartment).HasMaxLength(128);
        b.Property(x => x.Details).HasMaxLength(1000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.RequestedAt });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class DischargeSummaryConfiguration : IEntityTypeConfiguration<DischargeSummary>
{
    public void Configure(EntityTypeBuilder<DischargeSummary> b)
    {
        b.ToTable("discharge_summaries", ClinicalSchema.Name);
        b.Property(x => x.Diagnoses).HasMaxLength(4000);
        b.Property(x => x.PresentingComplaints).HasMaxLength(4000);
        b.Property(x => x.CourseInHospital).HasMaxLength(16000);
        b.Property(x => x.Procedures).HasMaxLength(4000);
        b.Property(x => x.InvestigationsSummary).HasMaxLength(8000);
        b.Property(x => x.ConditionAtDischarge).HasMaxLength(2000);
        b.Property(x => x.Medications).HasMaxLength(4000);
        b.Property(x => x.Advice).HasMaxLength(4000);
        b.Property(x => x.FollowUpInstructions).HasMaxLength(1000);
        b.Property(x => x.AmendmentReason).HasMaxLength(500);
        b.Ignore(x => x.Content);
        b.Ignore(x => x.IsSigned);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Amendments).WithOne().HasForeignKey(x => x.DischargeSummaryId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.AdmissionId).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.Status });
    }
}

internal sealed class DischargeSummaryAmendmentConfiguration : IEntityTypeConfiguration<DischargeSummaryAmendment>
{
    public void Configure(EntityTypeBuilder<DischargeSummaryAmendment> b)
    {
        b.ToTable("discharge_summary_amendments", ClinicalSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.PreviousContent).HasColumnType("jsonb");
        b.HasIndex(x => x.DischargeSummaryId);
    }
}
