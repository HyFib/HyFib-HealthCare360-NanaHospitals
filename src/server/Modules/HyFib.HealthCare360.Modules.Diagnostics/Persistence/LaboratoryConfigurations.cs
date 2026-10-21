using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Diagnostics.Persistence;

internal static class DiagnosticsSchema
{
    public const string Name = "diagnostics";

    /// <summary>
    /// Measured values need four decimals (TSH 0.005, specific gravity 1.015, INR 1.12). The platform-wide
    /// convention of numeric(18,2) is meant for money and would silently round them.
    /// </summary>
    public const int ValuePrecision = 18;

    public const int ValueScale = 4;
}

internal sealed class LabTestConfiguration : IEntityTypeConfiguration<LabTest>
{
    public void Configure(EntityTypeBuilder<LabTest> b)
    {
        b.ToTable("lab_tests", DiagnosticsSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.ShortName).HasMaxLength(32);
        b.Property(x => x.Method).HasMaxLength(200);
        b.Property(x => x.Loinc).HasMaxLength(16);
        b.Property(x => x.LoincName).HasMaxLength(255);
        b.Property(x => x.Unit).HasMaxLength(32);
        b.Property(x => x.QcGroup).HasMaxLength(32);
        b.Property(x => x.DeltaThresholdPercent).HasPrecision(7, 2);
        b.Property(x => x.Preparation).HasMaxLength(500);
        b.Property(x => x.Interpretation).HasMaxLength(2000);
        b.Ignore(x => x.QcKey);
        b.HasOne<ServiceItem>().WithMany().HasForeignKey(x => x.ServiceItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ReferralLab>().WithMany().HasForeignKey(x => x.DefaultReferralLabId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Components).WithOne().HasForeignKey(x => x.PanelTestId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Ranges).WithOne().HasForeignKey(x => x.LabTestId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.Section, x.IsActive });
        b.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class LabTestComponentConfiguration : IEntityTypeConfiguration<LabTestComponent>
{
    public void Configure(EntityTypeBuilder<LabTestComponent> b)
    {
        b.ToTable("lab_test_components", DiagnosticsSchema.Name);
        b.Property(x => x.GroupName).HasMaxLength(100);
        b.HasOne<LabTest>().WithMany().HasForeignKey(x => x.TestId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ReferenceRangeConfiguration : IEntityTypeConfiguration<ReferenceRange>
{
    public void Configure(EntityTypeBuilder<ReferenceRange> b)
    {
        b.ToTable("lab_reference_ranges", DiagnosticsSchema.Name);
        b.Property(x => x.Low).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.Property(x => x.High).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.Property(x => x.CriticalLow).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.Property(x => x.CriticalHigh).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.Property(x => x.Text).HasMaxLength(500);
        b.Ignore(x => x.IsSexSpecific);
    }
}

internal sealed class LabInstrumentConfiguration : IEntityTypeConfiguration<LabInstrument>
{
    public void Configure(EntityTypeBuilder<LabInstrument> b)
    {
        b.ToTable("lab_instruments", DiagnosticsSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Manufacturer).HasMaxLength(100);
        b.Property(x => x.Model).HasMaxLength(100);
        b.Property(x => x.Location).HasMaxLength(100);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class ReferralLabConfiguration : IEntityTypeConfiguration<ReferralLab>
{
    public void Configure(EntityTypeBuilder<ReferralLab> b)
    {
        b.ToTable("referral_labs", DiagnosticsSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.ContactPerson).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.AccreditationNumber).HasMaxLength(64);
        b.HasIndex(x => x.Code).IsUnique();
    }
}

internal sealed class LabOrderConfiguration : IEntityTypeConfiguration<LabOrder>
{
    public void Configure(EntityTypeBuilder<LabOrder> b)
    {
        b.ToTable("lab_orders", DiagnosticsSchema.Name);
        b.Property(x => x.AccessionNumber).HasMaxLength(24);
        b.Property(x => x.SourceOrderNumber).HasMaxLength(32);
        b.Property(x => x.ReferringDoctorName).HasMaxLength(200);
        b.Property(x => x.ClinicalNotes).HasMaxLength(1000);
        b.Ignore(x => x.AcceptsNewItems);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.LabOrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.AccessionNumber).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.OrderedAt });
        b.HasIndex(x => new { x.FacilityId, x.OrderedAt });
        b.HasIndex(x => new { x.Status, x.OrderedAt });
        b.HasIndex(x => x.SourceOrderId);
        b.HasIndex(x => x.VisitId);
    }
}

internal sealed class LabOrderItemConfiguration : IEntityTypeConfiguration<LabOrderItem>
{
    public void Configure(EntityTypeBuilder<LabOrderItem> b)
    {
        b.ToTable("lab_order_items", DiagnosticsSchema.Name);
        b.Property(x => x.TestCode).HasMaxLength(32);
        b.Property(x => x.TestName).HasMaxLength(200);
        b.Property(x => x.ChargeError).HasMaxLength(300);
        b.Property(x => x.DispatchReference).HasMaxLength(64);
        b.Property(x => x.ExternalReference).HasMaxLength(64);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Ignore(x => x.IsActive);
        b.Ignore(x => x.IsPackageComponent);
        b.HasOne<LabTest>().WithMany().HasForeignKey(x => x.LabTestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LabSample>().WithMany().HasForeignKey(x => x.SampleId).OnDelete(DeleteBehavior.SetNull);
        b.HasOne<ReferralLab>().WithMany().HasForeignKey(x => x.ReferralLabId).OnDelete(DeleteBehavior.Restrict);

        // One lab item per clinical order item: makes ServiceOrderPlaced handling idempotent at the database level.
        b.HasIndex(x => x.SourceOrderItemId).IsUnique();
        b.HasIndex(x => new { x.Status, x.Section });
        b.HasIndex(x => new { x.ReferralLabId, x.SentOutAt });
        b.HasIndex(x => x.ReleasedAt);
        b.HasIndex(x => x.PackageBookingId);
    }
}

internal sealed class LabSampleConfiguration : IEntityTypeConfiguration<LabSample>
{
    public void Configure(EntityTypeBuilder<LabSample> b)
    {
        b.ToTable("lab_samples", DiagnosticsSchema.Name);
        b.Property(x => x.Barcode).HasMaxLength(20);
        b.Property(x => x.CollectionSite).HasMaxLength(100);
        b.Property(x => x.RejectionRemarks).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<LabOrder>().WithMany().HasForeignKey(x => x.LabOrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.Barcode).IsUnique();
        b.HasIndex(x => new { x.Status, x.Section });
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => x.RejectedAt);
    }
}

internal sealed class LabResultConfiguration : IEntityTypeConfiguration<LabResult>
{
    public void Configure(EntityTypeBuilder<LabResult> b)
    {
        b.ToTable("lab_results", DiagnosticsSchema.Name);
        b.Property(x => x.TestCode).HasMaxLength(32);
        b.Property(x => x.TestName).HasMaxLength(200);
        b.Property(x => x.GroupName).HasMaxLength(100);
        b.Property(x => x.ValueText).HasMaxLength(2000);
        b.Property(x => x.NumericValue).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.Property(x => x.PreviousValue).HasPrecision(DiagnosticsSchema.ValuePrecision, DiagnosticsSchema.ValueScale);
        b.Property(x => x.DeltaPercent).HasPrecision(12, 1);
        b.Property(x => x.Unit).HasMaxLength(32);
        b.Property(x => x.ReferenceText).HasMaxLength(500);
        b.Property(x => x.Method).HasMaxLength(200);
        b.Property(x => x.InstrumentCode).HasMaxLength(32);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.Organism).HasMaxLength(200);
        b.Property(x => x.ColonyCount).HasMaxLength(100);
        b.Ignore(x => x.IsCritical);
        b.HasOne<LabOrderItem>().WithMany().HasForeignKey(x => x.LabOrderItemId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<LabTest>().WithMany().HasForeignKey(x => x.LabTestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LabSample>().WithMany().HasForeignKey(x => x.SampleId).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(x => x.Susceptibilities).WithOne().HasForeignKey(x => x.LabResultId).OnDelete(DeleteBehavior.Cascade);

        // Delta checks and cumulative reports read the patient's history per analyte.
        b.HasIndex(x => new { x.PatientId, x.LabTestId, x.VerifiedAt });
        b.HasIndex(x => x.LabOrderId);
        b.HasIndex(x => new { x.Status, x.Flag });
    }
}

internal sealed class LabSusceptibilityConfiguration : IEntityTypeConfiguration<LabSusceptibility>
{
    public void Configure(EntityTypeBuilder<LabSusceptibility> b)
    {
        b.ToTable("lab_result_susceptibilities", DiagnosticsSchema.Name);
        b.Property(x => x.Antibiotic).HasMaxLength(100);
        b.Property(x => x.Mic).HasMaxLength(32);
    }
}

internal sealed class LabReportConfiguration : IEntityTypeConfiguration<LabReport>
{
    public void Configure(EntityTypeBuilder<LabReport> b)
    {
        b.ToTable("lab_reports", DiagnosticsSchema.Name);
        b.Property(x => x.ReportNumber).HasMaxLength(24);
        b.Property(x => x.VerificationCode).HasMaxLength(VerificationCode.Length);
        b.HasOne<LabOrder>().WithMany().HasForeignKey(x => x.LabOrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Amendments).WithOne().HasForeignKey(x => x.LabReportId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.LabOrderId).IsUnique();
        b.HasIndex(x => x.VerificationCode).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.ReleasedAt });
        b.HasIndex(x => new { x.Status, x.ReleasedAt });
    }
}

internal sealed class LabReportAmendmentConfiguration : IEntityTypeConfiguration<LabReportAmendment>
{
    public void Configure(EntityTypeBuilder<LabReportAmendment> b)
    {
        b.ToTable("lab_report_amendments", DiagnosticsSchema.Name);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Changes).HasMaxLength(2000);
    }
}

internal sealed class CriticalCallLogConfiguration : IEntityTypeConfiguration<CriticalCallLog>
{
    public void Configure(EntityTypeBuilder<CriticalCallLog> b)
    {
        b.ToTable("critical_call_logs", DiagnosticsSchema.Name);
        b.Property(x => x.Finding).HasMaxLength(300);
        b.Property(x => x.InformedPersonName).HasMaxLength(200);
        b.Property(x => x.InformedPersonRole).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.LabResultId);
        b.HasIndex(x => x.ImagingStudyId);
        b.HasIndex(x => x.CardiologyStudyId);
        b.HasIndex(x => new { x.PatientId, x.InformedAt });
        b.HasIndex(x => x.InformedAt);
    }
}
