using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.SupplyChain.Persistence;

internal sealed class AssetConfiguration : IEntityTypeConfiguration<Asset>
{
    public void Configure(EntityTypeBuilder<Asset> b)
    {
        b.ToTable("assets", SupplyChainSchema.Name);
        b.Property(x => x.AssetTag).HasMaxLength(24);
        b.Property(x => x.Barcode).HasMaxLength(64);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Make).HasMaxLength(100);
        b.Property(x => x.Model).HasMaxLength(100);
        b.Property(x => x.SerialNumber).HasMaxLength(100);
        b.Property(x => x.Location).HasMaxLength(200);
        b.Property(x => x.CustodianName).HasMaxLength(120);
        b.Property(x => x.PurchaseOrderNumber).HasMaxLength(32);
        b.Property(x => x.InvoiceNumber).HasMaxLength(40);
        b.Property(x => x.DepreciationRatePercent).IsPercent();
        b.Property(x => x.DisposalMethod).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Ignore(x => x.IsInService);
        b.Ignore(x => x.DepreciationStart);
        b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AssetTag).IsUnique();
        b.HasIndex(x => x.Barcode);
        b.HasIndex(x => x.SerialNumber);
        b.HasIndex(x => new { x.Category, x.Status });
        b.HasIndex(x => x.DepartmentId);
        b.HasIndex(x => x.WarrantyExpiry);
        b.HasIndex(x => x.NextCalibrationDue);
    }
}

internal sealed class AssetContractConfiguration : IEntityTypeConfiguration<AssetContract>
{
    public void Configure(EntityTypeBuilder<AssetContract> b)
    {
        b.ToTable("asset_contracts", SupplyChainSchema.Name);
        b.Property(x => x.ContractNumber).HasMaxLength(64);
        b.Property(x => x.CoverageNotes).HasMaxLength(1000);
        b.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AssetId, x.EndDate });
        b.HasIndex(x => x.EndDate);
    }
}

internal sealed class PreventiveMaintenancePlanConfiguration : IEntityTypeConfiguration<PreventiveMaintenancePlan>
{
    public void Configure(EntityTypeBuilder<PreventiveMaintenancePlan> b)
    {
        b.ToTable("pm_plans", SupplyChainSchema.Name);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Checklist).HasMaxLength(4000);
        b.Property(x => x.AssignedTo).HasMaxLength(120);
        b.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.IsActive, x.NextDueDate });
        b.HasIndex(x => x.AssetId);
    }
}

internal sealed class PmVisitConfiguration : IEntityTypeConfiguration<PmVisit>
{
    public void Configure(EntityTypeBuilder<PmVisit> b)
    {
        b.ToTable("pm_visits", SupplyChainSchema.Name);
        b.Property(x => x.PerformedBy).HasMaxLength(120);
        b.Property(x => x.Findings).HasMaxLength(2000);
        b.HasOne<PreventiveMaintenancePlan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AssetId, x.VisitDate });
    }
}

internal sealed class MaintenanceTicketConfiguration : IEntityTypeConfiguration<MaintenanceTicket>
{
    public void Configure(EntityTypeBuilder<MaintenanceTicket> b)
    {
        b.ToTable("maintenance_tickets", SupplyChainSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Issue).HasMaxLength(2000);
        b.Property(x => x.AssignedTo).HasMaxLength(120);
        b.Property(x => x.Resolution).HasMaxLength(2000);
        b.Property(x => x.RootCause).HasMaxLength(500);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.Status, x.Priority });
        b.HasIndex(x => new { x.AssetId, x.ReportedAt });
    }
}

internal sealed class CalibrationRecordConfiguration : IEntityTypeConfiguration<CalibrationRecord>
{
    public void Configure(EntityTypeBuilder<CalibrationRecord> b)
    {
        b.ToTable("calibration_records", SupplyChainSchema.Name);
        b.Property(x => x.Agency).HasMaxLength(200);
        b.Property(x => x.CertificateNumber).HasMaxLength(64);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AssetId, x.CalibratedOn });
        b.HasIndex(x => x.NextDueDate);
    }
}

internal sealed class CondemnationRequestConfiguration : IEntityTypeConfiguration<CondemnationRequest>
{
    public void Configure(EntityTypeBuilder<CondemnationRequest> b)
    {
        b.ToTable("condemnation_requests", SupplyChainSchema.Name);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.DecisionRemarks).HasMaxLength(500);
        b.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Status, x.RequestedAt });
        b.HasIndex(x => x.AssetId);
    }
}
