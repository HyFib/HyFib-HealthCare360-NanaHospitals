using HyFib.HealthCare360.Modules.Clinical.Domain.Idsp;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Persistence;

internal sealed class NotifiableCaseConfiguration : IEntityTypeConfiguration<NotifiableCase>
{
    public void Configure(EntityTypeBuilder<NotifiableCase> b)
    {
        b.ToTable("notifiable_cases", ClinicalSchema.Name);
        b.Property(x => x.DiseaseCode).HasMaxLength(32);
        b.Property(x => x.IcdCode).HasMaxLength(16);
        b.Property(x => x.TestName).HasMaxLength(200);
        b.Property(x => x.TestResult).HasMaxLength(200);
        b.Property(x => x.DiscardReason).HasMaxLength(500);
        b.Property(x => x.NikshayId).HasMaxLength(32);
        b.Ignore(x => x.LastEventAt);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.PresumedWeek });
        b.HasIndex(x => new { x.FacilityId, x.ConfirmedWeek });
        b.HasIndex(x => new { x.PatientId, x.DiseaseCode });

        // The Ni-kshay register: tuberculosis cases still to notify, and those notified lately.
        b.HasIndex(x => new { x.DiseaseCode, x.Status, x.NikshayNotifiedOn });
        b.HasIndex(x => x.DiagnosisId);
        b.HasIndex(x => x.LabResultId);
    }
}

internal sealed class IdspWeeklyReportConfiguration : IEntityTypeConfiguration<IdspWeeklyReport>
{
    public void Configure(EntityTypeBuilder<IdspWeeklyReport> b)
    {
        b.ToTable("idsp_weekly_reports", ClinicalSchema.Name);
        b.Property(x => x.Reference).HasMaxLength(64);
        b.HasIndex(x => new { x.FacilityId, x.Year, x.Week, x.Form }).IsUnique();
    }
}
