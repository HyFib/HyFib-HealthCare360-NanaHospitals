using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Persistence;

internal sealed class SalaryComponentConfiguration : IEntityTypeConfiguration<SalaryComponent>
{
    public void Configure(EntityTypeBuilder<SalaryComponent> b)
    {
        b.ToTable("salary_components", HrSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.DefaultValue).HasPrecision(18, 4);
        b.HasIndex(x => x.Code).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class EmployeeSalaryStructureConfiguration : IEntityTypeConfiguration<EmployeeSalaryStructure>
{
    public void Configure(EntityTypeBuilder<EmployeeSalaryStructure> b)
    {
        b.ToTable("salary_structures", HrSchema.Name);
        b.Property(x => x.ProfessionalTaxState).HasMaxLength(4);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.StructureId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.EmployeeId, x.EffectiveFrom }).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class SalaryStructureLineConfiguration : IEntityTypeConfiguration<SalaryStructureLine>
{
    public void Configure(EntityTypeBuilder<SalaryStructureLine> b)
    {
        b.ToTable("salary_structure_lines", HrSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Value).HasPrecision(18, 4);
        b.HasOne<SalaryComponent>().WithMany().HasForeignKey(x => x.SalaryComponentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.StructureId, x.SalaryComponentId }).IsUnique();
    }
}

internal sealed class ProfessionalTaxSlabConfiguration : IEntityTypeConfiguration<ProfessionalTaxSlab>
{
    public void Configure(EntityTypeBuilder<ProfessionalTaxSlab> b)
    {
        b.ToTable("professional_tax_slabs", HrSchema.Name);
        b.Property(x => x.StateCode).HasMaxLength(4);
        b.Property(x => x.StateName).HasMaxLength(64);
        b.HasIndex(x => new { x.StateCode, x.IncomeFrom }).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class PayrollRunConfiguration : IEntityTypeConfiguration<PayrollRun>
{
    public void Configure(EntityTypeBuilder<PayrollRun> b)
    {
        b.ToTable("payroll_runs", HrSchema.Name);
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.ReopenReason).HasMaxLength(500);
        b.Ignore(x => x.PeriodStart);
        b.Ignore(x => x.PeriodEnd);
        b.Ignore(x => x.IsEditable);
        b.HasIndex(x => new { x.FacilityId, x.Year, x.Month }).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class PayrollInputConfiguration : IEntityTypeConfiguration<PayrollInput>
{
    public void Configure(EntityTypeBuilder<PayrollInput> b)
    {
        b.ToTable("payroll_inputs", HrSchema.Name);
        b.Property(x => x.ComponentCode).HasMaxLength(24);
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.SourceReference).HasMaxLength(100);
        b.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.PayrollRunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PayrollRunId, x.EmployeeId });
        b.HasIndex(x => new { x.PayrollRunId, x.Source, x.SourceReference }).IsUnique()
            .HasFilter("source_reference IS NOT NULL AND " + HrSchema.NotDeleted);
    }
}

internal sealed class PayslipConfiguration : IEntityTypeConfiguration<Payslip>
{
    public void Configure(EntityTypeBuilder<Payslip> b)
    {
        b.ToTable("payslips", HrSchema.Name);
        b.Property(x => x.EmployeeCode).HasMaxLength(24);
        b.Property(x => x.EmployeeName).HasMaxLength(302);
        b.Property(x => x.DesignationName).HasMaxLength(100);
        b.Property(x => x.DepartmentName).HasMaxLength(200);
        b.Property(x => x.BankAccountMasked).HasMaxLength(24);
        b.Property(x => x.PayableDays).HasPrecision(6, 2);
        b.Property(x => x.LopDays).HasPrecision(6, 2);
        b.HasOne<PayrollRun>().WithMany().HasForeignKey(x => x.PayrollRunId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.PayslipId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.PayrollRunId, x.EmployeeId }).IsUnique().HasFilter(HrSchema.NotDeleted);
        b.HasIndex(x => new { x.EmployeeId, x.Year, x.Month });
    }
}

internal sealed class PayslipLineConfiguration : IEntityTypeConfiguration<PayslipLine>
{
    public void Configure(EntityTypeBuilder<PayslipLine> b)
    {
        b.ToTable("payslip_lines", HrSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => x.PayslipId);
    }
}
