using HyFib.HealthCare360.Modules.SupportServices.Domain.Cssd;

namespace HyFib.HealthCare360.Modules.SupportServices.Persistence;

internal static class SupportServicesSchema
{
    public const string Name = "supportservices";
}

internal sealed class InstrumentSetConfiguration : IEntityTypeConfiguration<InstrumentSet>
{
    public void Configure(EntityTypeBuilder<InstrumentSet> b)
    {
        b.ToTable("instrument_sets", SupportServicesSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(128);
        b.Property(x => x.Category).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Ignore(x => x.InstrumentCount);
        b.Ignore(x => x.CountSheet);
        b.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.InstrumentSetId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique();
    }
}

internal sealed class InstrumentSetItemConfiguration : IEntityTypeConfiguration<InstrumentSetItem>
{
    public void Configure(EntityTypeBuilder<InstrumentSetItem> b)
    {
        b.ToTable("instrument_set_items", SupportServicesSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128);
    }
}

internal sealed class CssdRequestConfiguration : IEntityTypeConfiguration<CssdRequest>
{
    public void Configure(EntityTypeBuilder<CssdRequest> b)
    {
        b.ToTable("cssd_requests", SupportServicesSchema.Name);
        b.Property(x => x.RequestNumber).HasMaxLength(24);
        b.Property(x => x.RequestedByName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Ignore(x => x.IsOpen);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.CssdRequestId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.RequestNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => x.DepartmentId);
    }
}

internal sealed class CssdRequestLineConfiguration : IEntityTypeConfiguration<CssdRequestLine>
{
    public void Configure(EntityTypeBuilder<CssdRequestLine> b)
    {
        b.ToTable("cssd_request_lines", SupportServicesSchema.Name);
        b.HasKey(x => x.Id);
        b.HasOne<InstrumentSet>().WithMany().HasForeignKey(x => x.InstrumentSetId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SoiledReceiptConfiguration : IEntityTypeConfiguration<SoiledReceipt>
{
    public void Configure(EntityTypeBuilder<SoiledReceipt> b)
    {
        b.ToTable("soiled_receipts", SupportServicesSchema.Name);
        b.Property(x => x.ReceivedByName).HasMaxLength(200);
        b.Property(x => x.HandedOverByName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.SoiledReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.ReceivedAt);
        b.HasIndex(x => new { x.HasDiscrepancy, x.ReceivedAt });
    }
}

internal sealed class SoiledReceiptLineConfiguration : IEntityTypeConfiguration<SoiledReceiptLine>
{
    public void Configure(EntityTypeBuilder<SoiledReceiptLine> b)
    {
        b.ToTable("soiled_receipt_lines", SupportServicesSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Discrepancies).HasMaxLength(1000);
        b.Property(x => x.CountDetails).HasColumnType("jsonb");
        b.HasOne<InstrumentSet>().WithMany().HasForeignKey(x => x.InstrumentSetId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.SetIssueId);
    }
}

internal sealed class SterilizationLoadConfiguration : IEntityTypeConfiguration<SterilizationLoad>
{
    public void Configure(EntityTypeBuilder<SterilizationLoad> b)
    {
        b.ToTable("sterilization_loads", SupportServicesSchema.Name);
        b.Property(x => x.CycleNumber).HasMaxLength(24);
        b.Property(x => x.SterilizerName).HasMaxLength(100);
        b.Property(x => x.Program).HasMaxLength(100);
        b.Property(x => x.TemperatureC).HasPrecision(5, 1);
        b.Property(x => x.PressureKpa).HasPrecision(7, 1);
        b.Property(x => x.BiologicalIndicatorLot).HasMaxLength(64);
        b.Property(x => x.OperatorName).HasMaxLength(200);
        b.Property(x => x.ReleasedByName).HasMaxLength(200);
        b.Property(x => x.FailureReason).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Ignore(x => x.RequiresRecall);
        b.HasMany(x => x.Packs).WithOne().HasForeignKey(x => x.LoadId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CycleNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.LoadedAt });
        b.HasIndex(x => x.BiologicalIndicator);
    }
}

internal sealed class SterilePackConfiguration : IEntityTypeConfiguration<SterilePack>
{
    public void Configure(EntityTypeBuilder<SterilePack> b)
    {
        b.ToTable("sterile_packs", SupportServicesSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.PackNumber).HasMaxLength(32);
        b.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.HasOne<InstrumentSet>().WithMany().HasForeignKey(x => x.InstrumentSetId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.PackNumber).IsUnique();
        b.HasIndex(x => new { x.InstrumentSetId, x.Status });
        b.HasIndex(x => new { x.Status, x.SterileUntil });
    }
}

internal sealed class SetIssueConfiguration : IEntityTypeConfiguration<SetIssue>
{
    public void Configure(EntityTypeBuilder<SetIssue> b)
    {
        b.ToTable("set_issues", SupportServicesSchema.Name);
        b.Property(x => x.IssuedToName).HasMaxLength(200);
        b.Property(x => x.RecallReason).HasMaxLength(300);
        b.Property(x => x.RecallAcknowledgedByName).HasMaxLength(200);
        b.Property(x => x.RecallAction).HasMaxLength(500);
        b.Ignore(x => x.IsRecallOpen);
        b.HasOne<SterilePack>().WithMany().HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SterilizationLoad>().WithMany().HasForeignKey(x => x.LoadId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.LoadId, x.Status });
        b.HasIndex(x => new { x.DepartmentId, x.IssuedAt });
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => x.RecallFlaggedAt).HasFilter("recall_flagged_at IS NOT NULL AND recall_acknowledged_at IS NULL").HasDatabaseName("ix_set_issues_open_recalls");
    }
}
