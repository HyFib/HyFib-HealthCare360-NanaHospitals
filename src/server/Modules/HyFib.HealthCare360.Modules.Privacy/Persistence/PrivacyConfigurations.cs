using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;

namespace HyFib.HealthCare360.Modules.Privacy.Persistence;

internal static class PrivacySchema
{
    public const string Name = "privacy";
}

internal sealed class DataRequestConfiguration : IEntityTypeConfiguration<DataRequest>
{
    public void Configure(EntityTypeBuilder<DataRequest> b)
    {
        b.ToTable("data_requests", PrivacySchema.Name);
        b.Property(x => x.RequestNumber).HasMaxLength(24);
        b.Property(x => x.PrincipalName).HasMaxLength(200);
        b.Property(x => x.ContactPhone).HasMaxLength(20);
        b.Property(x => x.ContactEmail).HasMaxLength(200);
        b.Property(x => x.RequesterName).HasMaxLength(200);
        b.Property(x => x.Details).HasMaxLength(4000);
        b.Property(x => x.IdentityNote).HasMaxLength(500);
        b.Property(x => x.Response).HasMaxLength(4000);
        b.Property(x => x.Corrections).HasJsonRows();
        b.Property(x => x.SummarySnapshot).HasColumnType("jsonb");
        b.Ignore(x => x.IsOpen);
        b.Ignore(x => x.IsIdentityVerified);
        b.Ignore(x => x.NeedsVerifiedIdentity);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Events).WithOne().HasForeignKey(x => x.DataRequestId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.RequestNumber).IsUnique();
        b.HasIndex(x => x.PatientId).HasFilter("patient_id IS NOT NULL");
        b.HasIndex(x => new { x.Status, x.DueAt });
    }
}

internal sealed class DataRequestEventConfiguration : IEntityTypeConfiguration<DataRequestEvent>
{
    public void Configure(EntityTypeBuilder<DataRequestEvent> b)
    {
        b.ToTable("data_request_events", PrivacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Text).HasMaxLength(4000);
        b.HasIndex(x => new { x.DataRequestId, x.At });
    }
}

internal sealed class NomineeConfiguration : IEntityTypeConfiguration<Nominee>
{
    public void Configure(EntityTypeBuilder<Nominee> b)
    {
        b.ToTable("nominees", PrivacySchema.Name);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Relationship).HasMaxLength(64);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(200);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        // One nominee per patient; a removed one (soft-deleted) stays for the record.
        b.HasIndex(x => x.PatientId).IsUnique().HasFilter("is_deleted = false");
    }
}

internal sealed class BreachConfiguration : IEntityTypeConfiguration<Breach>
{
    public void Configure(EntityTypeBuilder<Breach> b)
    {
        b.ToTable("breaches", PrivacySchema.Name);
        b.Property(x => x.BreachNumber).HasMaxLength(24);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.Location).HasMaxLength(500);
        b.Property(x => x.Consequences).HasMaxLength(4000);
        b.Property(x => x.Mitigation).HasMaxLength(4000);
        b.Property(x => x.PrincipalSteps).HasMaxLength(4000);
        b.Property(x => x.Cause).HasMaxLength(4000);
        b.Property(x => x.Findings).HasMaxLength(4000);
        b.Property(x => x.Remediation).HasMaxLength(4000);
        b.Property(x => x.PrincipalNotice).HasMaxLength(320);
        b.Property(x => x.BoardReference).HasMaxLength(100);
        b.Property(x => x.ClosingNote).HasMaxLength(1000);
        b.Ignore(x => x.ReportDueAt);
        b.HasIndex(x => x.BreachNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.AwareAt });
    }
}

internal sealed class BreachAffectedPatientConfiguration : IEntityTypeConfiguration<BreachAffectedPatient>
{
    public void Configure(EntityTypeBuilder<BreachAffectedPatient> b)
    {
        b.ToTable("breach_affected_patients", PrivacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.NoticeChannels).HasMaxLength(64);
        b.HasOne<Breach>().WithMany().HasForeignKey(x => x.BreachId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.BreachId, x.PatientId }).IsUnique();
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class RetentionRunConfiguration : IEntityTypeConfiguration<RetentionRun>
{
    public void Configure(EntityTypeBuilder<RetentionRun> b)
    {
        b.ToTable("retention_runs", PrivacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Results).HasJsonRows();
        b.Property(x => x.Error).HasMaxLength(1000);
        b.Ignore(x => x.TotalRemoved);
        b.HasIndex(x => x.StartedAt);
    }
}

internal sealed class RetentionChangeConfiguration : IEntityTypeConfiguration<RetentionChange>
{
    public void Configure(EntityTypeBuilder<RetentionChange> b)
    {
        b.ToTable("retention_changes", PrivacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(64);
        b.HasIndex(x => new { x.Key, x.ChangedAt });
    }
}
