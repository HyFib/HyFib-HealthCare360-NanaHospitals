using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Persistence;

internal sealed class IpAccountConfiguration : IEntityTypeConfiguration<IpAccount>
{
    public void Configure(EntityTypeBuilder<IpAccount> b)
    {
        b.ToTable("ip_accounts", BillingSchema.Name);
        b.Property(x => x.AdmissionNumber).HasMaxLength(32);
        b.Property(x => x.DischargeType).HasMaxLength(64);
        b.Property(x => x.DischargeApprovalReason).HasMaxLength(300);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Ignore(x => x.DepositBalance);
        b.Ignore(x => x.IsActive);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.AdmissionId).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.AdmittedAt });
        b.HasIndex(x => new { x.Status, x.AdmittedAt });
    }
}

internal sealed class PackageConfiguration : IEntityTypeConfiguration<Package>
{
    public void Configure(EntityTypeBuilder<Package> b)
    {
        b.ToTable("packages", BillingSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.HbpCode).HasMaxLength(8);
        b.HasMany(x => x.Inclusions).WithOne().HasForeignKey(x => x.PackageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ServiceItem>().WithMany().HasForeignKey(x => x.ServiceItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => x.IsActive);
    }
}

internal sealed class HbpProcedureConfiguration : IEntityTypeConfiguration<HbpProcedure>
{
    public void Configure(EntityTypeBuilder<HbpProcedure> b)
    {
        b.ToTable("hbp_procedures", BillingSchema.Name);
        b.Property(x => x.Code).HasMaxLength(8);
        b.Property(x => x.PackageCode).HasMaxLength(8);
        b.Property(x => x.SpecialtyCode).HasMaxLength(2);
        b.Property(x => x.Specialty).HasMaxLength(200);
        b.Property(x => x.PackageName).HasMaxLength(300);
        b.Property(x => x.ProcedureName).HasMaxLength(500);
        b.Property(x => x.Rate).HasPrecision(12, 2);
        b.Property(x => x.Stratification).HasMaxLength(500);
        b.Property(x => x.MasterVersion).HasMaxLength(64);
        b.HasIndex(x => x.Code).IsUnique();
        b.HasIndex(x => new { x.SpecialtyCode, x.IsActive });
    }
}

internal sealed class PackageInclusionConfiguration : IEntityTypeConfiguration<PackageInclusion>
{
    public void Configure(EntityTypeBuilder<PackageInclusion> b)
    {
        b.ToTable("package_inclusions", BillingSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.MaxQuantity).HasPrecision(18, 3);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasIndex(x => x.PackageId);
    }
}
