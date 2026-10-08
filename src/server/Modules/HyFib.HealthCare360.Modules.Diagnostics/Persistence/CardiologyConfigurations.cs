using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Diagnostics.Persistence;

internal sealed class CardiologyStudyConfiguration : IEntityTypeConfiguration<CardiologyStudy>
{
    public void Configure(EntityTypeBuilder<CardiologyStudy> b)
    {
        b.ToTable("cardiology_studies", DiagnosticsSchema.Name);
        b.Property(x => x.AccessionNumber).HasMaxLength(24);
        b.Property(x => x.SourceOrderNumber).HasMaxLength(32);
        b.Property(x => x.ServiceCode).HasMaxLength(32);
        b.Property(x => x.StudyName).HasMaxLength(200);
        b.Property(x => x.ClinicalNotes).HasMaxLength(1000);
        b.Property(x => x.Room).HasMaxLength(64);
        b.Property(x => x.DeviceName).HasMaxLength(100);
        b.Property(x => x.TechnicianNotes).HasMaxLength(1000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.ChargeError).HasMaxLength(300);
        b.Property(x => x.StudyInstanceUid).HasMaxLength(64);
        b.Ignore(x => x.IsPackageComponent);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AccessionNumber).IsUnique();
        b.HasIndex(x => x.SourceOrderItemId).IsUnique();
        b.HasIndex(x => new { x.Status, x.StudyType });
        b.HasIndex(x => new { x.FacilityId, x.OrderedAt });
        b.HasIndex(x => new { x.PatientId, x.OrderedAt });
        b.HasIndex(x => x.PackageBookingId);
    }
}

internal sealed class CardiologyPacsObjectConfiguration : IEntityTypeConfiguration<CardiologyPacsObject>
{
    public void Configure(EntityTypeBuilder<CardiologyPacsObject> b)
    {
        b.ToTable("cardiology_pacs_objects", DiagnosticsSchema.Name);
        b.Property(x => x.SopInstanceUid).HasMaxLength(64);
        b.Property(x => x.Message).HasMaxLength(300);
        b.HasOne<CardiologyStudy>().WithMany().HasForeignKey(x => x.CardiologyStudyId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.CardiologyStudyId, x.FileId }).IsUnique();
    }
}

internal sealed class CardiologyReportConfiguration : IEntityTypeConfiguration<CardiologyReport>
{
    public void Configure(EntityTypeBuilder<CardiologyReport> b)
    {
        b.ToTable("cardiology_reports", DiagnosticsSchema.Name);

        // Structured measurements are stored inline (ecg_heart_rate, echo_lvef…) so trends can be queried.
        b.ComplexProperty(x => x.Ecg, c =>
        {
            c.Property(p => p.Rhythm).HasMaxLength(100);
            c.Property(p => p.Axis).HasMaxLength(64);
            c.Property(p => p.StTChanges).HasMaxLength(500);
            c.Property(p => p.Interpretation).HasMaxLength(2000);
        });
        b.ComplexProperty(x => x.Echo, c =>
        {
            c.Property(p => p.Lvef).HasPrecision(5, 1);
            c.Property(p => p.LeftVentricle).HasMaxLength(500);
            c.Property(p => p.RightVentricle).HasMaxLength(500);
            c.Property(p => p.LeftAtrium).HasMaxLength(300);
            c.Property(p => p.RightAtrium).HasMaxLength(300);
            c.Property(p => p.Valves).HasMaxLength(1000);
            c.Property(p => p.Rwma).HasMaxLength(500);
            c.Property(p => p.DiastolicFunction).HasMaxLength(300);
            c.Property(p => p.Pericardium).HasMaxLength(300);
            c.Property(p => p.Conclusion).HasMaxLength(2000);
        });
        b.ComplexProperty(x => x.Tmt, c =>
        {
            c.Property(p => p.Protocol).HasMaxLength(64);
            c.Property(p => p.Mets).HasPrecision(5, 1);
            c.Property(p => p.BpResponse).HasMaxLength(300);
            c.Property(p => p.Symptoms).HasMaxLength(300);
            c.Property(p => p.StChanges).HasMaxLength(500);
            c.Property(p => p.ReasonForTermination).HasMaxLength(300);
        });
        b.ComplexProperty(x => x.Holter, c =>
        {
            c.Property(p => p.LongestPause).HasMaxLength(64);
            c.Property(p => p.Arrhythmias).HasMaxLength(1000);
            c.Property(p => p.StChanges).HasMaxLength(500);
        });
        b.ComplexProperty(x => x.Abpm, c => c.Property(p => p.DippingPattern).HasMaxLength(64));

        b.Property(x => x.Conclusion).HasMaxLength(2000);
        b.Property(x => x.Recommendations).HasMaxLength(1000);
        b.Property(x => x.CriticalFindingNote).HasMaxLength(500);
        b.Property(x => x.VerificationCode).HasMaxLength(VerificationCode.Length);
        b.HasOne<CardiologyStudy>().WithMany().HasForeignKey(x => x.CardiologyStudyId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.CardiologyStudyId).IsUnique();
        b.HasIndex(x => x.VerificationCode).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.ReleasedAt });
        b.HasIndex(x => x.Status);
    }
}
