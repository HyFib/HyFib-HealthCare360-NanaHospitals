using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;

namespace HyFib.HealthCare360.Modules.Pharmacy.Persistence;

internal sealed class WardIndentConfiguration : IEntityTypeConfiguration<WardIndent>
{
    public void Configure(EntityTypeBuilder<WardIndent> b)
    {
        b.ToTable("ward_indents", PharmacySchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.WardName).HasMaxLength(100);
        b.Property(x => x.BedLabel).HasMaxLength(32);
        b.Property(x => x.Remarks).HasMaxLength(1000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Ignore(x => x.IsOpen);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.WardIndentId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.Status, x.Urgency, x.RaisedAt });
        b.HasIndex(x => x.AdmissionId);
    }
}

internal sealed class WardIndentLineConfiguration : IEntityTypeConfiguration<WardIndentLine>
{
    public void Configure(EntityTypeBuilder<WardIndentLine> b)
    {
        b.ToTable("ward_indent_lines", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.RequestedQuantity).IsQuantity();
        b.Property(x => x.IssuedQuantity).IsQuantity();
        b.Property(x => x.Remarks).HasMaxLength(300);
        b.Ignore(x => x.RemainingQuantity);
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class IpIssueConfiguration : IEntityTypeConfiguration<IpIssue>
{
    public void Configure(EntityTypeBuilder<IpIssue> b)
    {
        b.ToTable("ip_issues", PharmacySchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.IpIssueId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Store>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.AdmissionId, x.IssuedAt });
        b.HasIndex(x => x.WardIndentId);
        b.HasIndex(x => x.QueueEntryId);
    }
}

internal sealed class IpIssueLineConfiguration : IEntityTypeConfiguration<IpIssueLine>
{
    public void Configure(EntityTypeBuilder<IpIssueLine> b)
    {
        b.ToTable("ip_issue_lines", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Quantity).IsQuantity();
        b.Property(x => x.UnitMrp).IsUnitRate();
        b.Property(x => x.ReturnedQuantity).IsQuantity();
        b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StockBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.ChargeId);
    }
}

internal sealed class WardReturnConfiguration : IEntityTypeConfiguration<WardReturn>
{
    public void Configure(EntityTypeBuilder<WardReturn> b)
    {
        b.ToTable("ward_returns", PharmacySchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.WardReturnId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<IpIssue>().WithMany().HasForeignKey(x => x.IpIssueId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.AdmissionId, x.ReturnedAt });
    }
}

internal sealed class WardReturnLineConfiguration : IEntityTypeConfiguration<WardReturnLine>
{
    public void Configure(EntityTypeBuilder<WardReturnLine> b)
    {
        b.ToTable("ward_return_lines", PharmacySchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.ItemName).HasMaxLength(200);
        b.Property(x => x.BatchNumber).HasMaxLength(40);
        b.Property(x => x.Quantity).IsQuantity();
        b.HasIndex(x => x.IpIssueLineId);
    }
}
