using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Domain.Statutory;

namespace HyFib.HealthCare360.Modules.HR.Persistence;

internal sealed class TaxDeclarationConfiguration : IEntityTypeConfiguration<TaxDeclaration>
{
    public void Configure(EntityTypeBuilder<TaxDeclaration> b)
    {
        b.ToTable("tax_declarations", HrSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EmployeeId, x.FinancialYear }).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class StatutoryEmployerProfileConfiguration : IEntityTypeConfiguration<StatutoryEmployerProfile>
{
    public void Configure(EntityTypeBuilder<StatutoryEmployerProfile> b)
    {
        b.ToTable("statutory_employer_profiles", HrSchema.Name);
        b.Property(x => x.Tan).HasMaxLength(10);
        b.Property(x => x.Pan).HasMaxLength(10);
        b.Property(x => x.DeductorName).HasMaxLength(75);
        b.Property(x => x.Branch).HasMaxLength(75);
        b.Property(x => x.DeductorType).HasMaxLength(2);
        b.Property(x => x.AddressLine1).HasMaxLength(25);
        b.Property(x => x.AddressLine2).HasMaxLength(25);
        b.Property(x => x.AddressLine3).HasMaxLength(25);
        b.Property(x => x.City).HasMaxLength(25);
        b.Property(x => x.StateCode).HasMaxLength(2);
        b.Property(x => x.Pin).HasMaxLength(6);
        b.Property(x => x.Email).HasMaxLength(75);
        b.Property(x => x.Phone).HasMaxLength(15);
        b.Property(x => x.ResponsibleName).HasMaxLength(75);
        b.Property(x => x.ResponsibleDesignation).HasMaxLength(20);
        b.Property(x => x.ResponsiblePan).HasMaxLength(10);
        b.Property(x => x.ResponsibleMobile).HasMaxLength(10);
        b.Property(x => x.ResponsibleEmail).HasMaxLength(75);
        b.Property(x => x.EpfEstablishmentCode).HasMaxLength(20);
        b.Property(x => x.EsicEmployerCode).HasMaxLength(17);
        b.Property(x => x.SalaryDebitAccount).HasMaxLength(18);
        b.HasIndex(x => x.FacilityId).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class TdsChallanConfiguration : IEntityTypeConfiguration<TdsChallan>
{
    public void Configure(EntityTypeBuilder<TdsChallan> b)
    {
        b.ToTable("tds_challans", HrSchema.Name);
        b.Property(x => x.BsrCode).HasMaxLength(7);
        b.Property(x => x.ChallanSerial).HasMaxLength(5);
        b.Property(x => x.MinorHead).HasMaxLength(3);
        b.HasIndex(x => new { x.FacilityId, x.Form, x.Year, x.Month });
        b.HasIndex(x => new { x.FacilityId, x.BsrCode, x.DepositedOn, x.ChallanSerial }).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}
