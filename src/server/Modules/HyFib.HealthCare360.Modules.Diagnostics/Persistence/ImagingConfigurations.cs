using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Diagnostics.Persistence;

internal sealed class ImagingStudyConfiguration : IEntityTypeConfiguration<ImagingStudy>
{
    public void Configure(EntityTypeBuilder<ImagingStudy> b)
    {
        b.ToTable("imaging_studies", DiagnosticsSchema.Name);
        b.Property(x => x.AccessionNumber).HasMaxLength(24);
        b.Property(x => x.SourceOrderNumber).HasMaxLength(32);
        b.Property(x => x.ServiceCode).HasMaxLength(32);
        b.Property(x => x.StudyName).HasMaxLength(200);
        b.Property(x => x.BodyPart).HasMaxLength(100);
        b.Property(x => x.ReferringDoctorName).HasMaxLength(200);
        b.Property(x => x.ClinicalNotes).HasMaxLength(1000);
        b.Property(x => x.Room).HasMaxLength(64);
        b.Property(x => x.StationAeTitle).HasMaxLength(16);
        b.Property(x => x.ContrastAgent).HasMaxLength(100);
        b.Property(x => x.ContrastVolumeMl).HasPrecision(8, 1);
        b.Property(x => x.DoseNote).HasMaxLength(200);
        b.Property(x => x.TechnicianNotes).HasMaxLength(1000);
        b.Property(x => x.StudyInstanceUid).HasMaxLength(DicomConventions.MaxUidLength);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.ChargeError).HasMaxLength(300);
        b.Ignore(x => x.IsClosed);
        b.Ignore(x => x.IsPackageComponent);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Repeats).WithOne().HasForeignKey(x => x.ImagingStudyId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.AccessionNumber).IsUnique();
        b.HasIndex(x => x.SourceOrderItemId).IsUnique();
        b.HasIndex(x => new { x.Status, x.Modality });
        b.HasIndex(x => new { x.FacilityId, x.OrderedAt });
        b.HasIndex(x => new { x.PatientId, x.OrderedAt });
        b.HasIndex(x => x.ScheduledAt);
        b.HasIndex(x => x.StudyInstanceUid);
        b.HasIndex(x => x.PackageBookingId);
    }
}

internal sealed class ImagingRepeatConfiguration : IEntityTypeConfiguration<ImagingRepeat>
{
    public void Configure(EntityTypeBuilder<ImagingRepeat> b)
    {
        b.ToTable("imaging_repeats", DiagnosticsSchema.Name);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasIndex(x => x.RecordedAt);
    }
}

internal sealed class RadiologyReportConfiguration : IEntityTypeConfiguration<RadiologyReport>
{
    public void Configure(EntityTypeBuilder<RadiologyReport> b)
    {
        b.ToTable("radiology_reports", DiagnosticsSchema.Name);
        b.Property(x => x.Technique).HasMaxLength(2000);
        b.Property(x => x.ClinicalHistory).HasMaxLength(1000);
        b.Property(x => x.Comparison).HasMaxLength(500);
        b.Property(x => x.Findings).HasMaxLength(8000);
        b.Property(x => x.Impression).HasMaxLength(4000);
        b.Property(x => x.CriticalFindingNote).HasMaxLength(500);
        b.Property(x => x.VerificationCode).HasMaxLength(VerificationCode.Length);
        b.HasOne<ImagingStudy>().WithMany().HasForeignKey(x => x.ImagingStudyId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ReportTemplate>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(x => x.Addenda).WithOne().HasForeignKey(x => x.RadiologyReportId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.ImagingStudyId).IsUnique();
        b.HasIndex(x => x.VerificationCode).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.ReleasedAt });
        b.HasIndex(x => x.Status);
    }
}

internal sealed class RadiologyAddendumConfiguration : IEntityTypeConfiguration<RadiologyAddendum>
{
    public void Configure(EntityTypeBuilder<RadiologyAddendum> b)
    {
        b.ToTable("radiology_addenda", DiagnosticsSchema.Name);
        b.Property(x => x.Text).HasMaxLength(4000);
        b.Property(x => x.Reason).HasMaxLength(300);
    }
}

internal sealed class ReportTemplateConfiguration : IEntityTypeConfiguration<ReportTemplate>
{
    public void Configure(EntityTypeBuilder<ReportTemplate> b)
    {
        b.ToTable("report_templates", DiagnosticsSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.BodyPart).HasMaxLength(100);
        b.Property(x => x.Technique).HasMaxLength(2000);
        b.Property(x => x.Findings).HasMaxLength(8000);
        b.Property(x => x.Impression).HasMaxLength(4000);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.Modality, x.IsActive });
    }
}

internal sealed class ImagingProcedureConfiguration : IEntityTypeConfiguration<ImagingProcedure>
{
    public void Configure(EntityTypeBuilder<ImagingProcedure> b)
    {
        b.ToTable("imaging_procedures", DiagnosticsSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.BodyPart).HasMaxLength(100);
        b.Property(x => x.PatientPreparation).HasMaxLength(1000);
        b.HasOne<ServiceItem>().WithMany().HasForeignKey(x => x.ServiceItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class ImagingAiFindingConfiguration : IEntityTypeConfiguration<ImagingAiFinding>
{
    public void Configure(EntityTypeBuilder<ImagingAiFinding> b)
    {
        b.ToTable("imaging_ai_findings", DiagnosticsSchema.Name);
        b.Property(x => x.Engine).HasMaxLength(100);
        b.Property(x => x.EngineVersion).HasMaxLength(32);
        b.Property(x => x.Finding).HasMaxLength(2000);
        b.Property(x => x.Confidence).HasPrecision(5, 4);
        b.Property(x => x.ReviewNote).HasMaxLength(500);
        b.HasOne<ImagingStudy>().WithMany().HasForeignKey(x => x.ImagingStudyId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ImagingStudyId, x.Status });
    }
}
