using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;

namespace HyFib.HealthCare360.Modules.Privacy.Persistence;

internal sealed class ErasureReplayConfiguration : IEntityTypeConfiguration<ErasureReplay>
{
    public void Configure(EntityTypeBuilder<ErasureReplay> b)
    {
        b.ToTable("erasure_replays", PrivacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.RequestNumber).HasMaxLength(24);
        b.HasIndex(x => x.LedgerEntryId).IsUnique();
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => x.ReplayedAt);
    }
}

internal sealed class GuardianConsentConfiguration : IEntityTypeConfiguration<GuardianConsent>
{
    public void Configure(EntityTypeBuilder<GuardianConsent> b)
    {
        b.ToTable("guardian_consents", PrivacySchema.Name);
        b.Property(x => x.GuardianName).HasMaxLength(200);
        b.Property(x => x.GuardianPhone).HasMaxLength(20);
        b.Property(x => x.DocumentType).HasMaxLength(60);
        b.Property(x => x.DocumentLast4).HasMaxLength(4);
        b.Property(x => x.CodeHash).HasMaxLength(64);
        b.Property(x => x.RevokedReason).HasMaxLength(300);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.Status });
        b.HasIndex(x => new { x.GuardianPhone, x.CreatedAt });

        // One verified consent per child at a time.
        b.HasIndex(x => x.PatientId).IsUnique().HasFilter("status = 'Verified' AND is_deleted = false").HasDatabaseName("ux_guardian_consents_verified");
    }
}

internal sealed class ConsentManagerConfiguration : IEntityTypeConfiguration<ConsentManager>
{
    public void Configure(EntityTypeBuilder<ConsentManager> b)
    {
        b.ToTable("consent_managers", PrivacySchema.Name);
        b.Property(x => x.Code).HasMaxLength(12);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.RegistrationNumber).HasMaxLength(64);
        b.Property(x => x.BaseUrl).HasMaxLength(500);
        b.Property(x => x.SecretCipher).HasMaxLength(256).IsProtectedValue();
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class ConsentArtefactConfiguration : IEntityTypeConfiguration<ConsentArtefact>
{
    public void Configure(EntityTypeBuilder<ConsentArtefact> b)
    {
        b.ToTable("consent_artefacts", PrivacySchema.Name);
        b.Property(x => x.ArtefactId).HasMaxLength(100);
        b.Property(x => x.WithdrawalError).HasMaxLength(500);
        b.HasOne<ConsentManager>().WithMany().HasForeignKey(x => x.ConsentManagerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ConsentManagerId, x.ArtefactId }).IsUnique();
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => new { x.Status, x.ValidUntil });
    }
}

internal sealed class ConsentManagerEventConfiguration : IEntityTypeConfiguration<ConsentManagerEvent>
{
    public void Configure(EntityTypeBuilder<ConsentManagerEvent> b)
    {
        b.ToTable("consent_manager_events", PrivacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.EventId).HasMaxLength(100);
        b.Property(x => x.Kind).HasMaxLength(32);
        b.Property(x => x.ArtefactId).HasMaxLength(100);
        b.Property(x => x.Outcome).HasMaxLength(100);
        b.HasIndex(x => new { x.ConsentManagerId, x.EventId }).IsUnique();
    }
}

internal sealed class DpiaConfiguration : IEntityTypeConfiguration<Dpia>
{
    public void Configure(EntityTypeBuilder<Dpia> b)
    {
        b.ToTable("dpias", PrivacySchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Activity).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.DataPrincipals).HasMaxLength(300);
        b.Property(x => x.Purpose).HasMaxLength(1000);
        b.Property(x => x.ApprovedByName).HasMaxLength(200);
        b.Property(x => x.Risks).HasJsonRows();
        b.Property(x => x.Mitigations).HasJsonRows();
        b.Ignore(x => x.HighestRisk);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.Status, x.ReviewDue });
    }
}

internal sealed class DataAuditConfiguration : IEntityTypeConfiguration<DataAudit>
{
    public void Configure(EntityTypeBuilder<DataAudit> b)
    {
        b.ToTable("data_audits", PrivacySchema.Name);
        b.Property(x => x.Auditor).HasMaxLength(200);
        b.Property(x => x.Scope).HasMaxLength(2000);
        b.Property(x => x.Findings).HasMaxLength(4000);
        b.Property(x => x.SignificantObservations).HasMaxLength(4000);
        b.Property(x => x.ReportReference).HasMaxLength(100);
        b.HasIndex(x => x.PeriodTo);
    }
}
