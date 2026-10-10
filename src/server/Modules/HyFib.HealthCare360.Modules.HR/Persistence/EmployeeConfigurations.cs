using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Security;

namespace HyFib.HealthCare360.Modules.HR.Persistence;

internal static class HrSchema
{
    public const string Name = "hr";

    /// <summary>Unique indexes skip soft-deleted rows so a removed record can be re-created.</summary>
    public const string NotDeleted = "is_deleted = false";
}

internal sealed class DesignationConfiguration : IEntityTypeConfiguration<Designation>
{
    public void Configure(EntityTypeBuilder<Designation> b)
    {
        b.ToTable("designations", HrSchema.Name);
        b.Property(x => x.Code).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(100);
        b.HasIndex(x => x.Code).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> b)
    {
        b.ToTable("employees", HrSchema.Name);
        b.Property(x => x.EmployeeCode).HasMaxLength(24);
        b.Property(x => x.Title).HasMaxLength(16);
        b.Property(x => x.FirstName).HasMaxLength(100);
        b.Property(x => x.MiddleName).HasMaxLength(100);
        b.Property(x => x.LastName).HasMaxLength(100);
        b.Property(x => x.FullName).HasMaxLength(302);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.EmergencyContactName).HasMaxLength(200);
        b.Property(x => x.EmergencyContactPhone).HasMaxLength(20);
        b.Property(x => x.SeparationNote).HasMaxLength(500);
        b.Property(x => x.BiometricPin).HasMaxLength(ZkTecoAttLogParser.MaxPinLength);
        b.Property(x => x.BankName).HasMaxLength(100);
        b.Property(x => x.BankAccountCipher).HasMaxLength(256).IsProtectedValue();
        b.Property(x => x.BankAccountIndex).HasMaxLength(64).IsBlindIndexOf(nameof(Employee.BankAccountCipher));
        b.Property(x => x.BankAccountMasked).HasMaxLength(24);
        b.Property(x => x.BankIfscCipher).HasMaxLength(256).IsProtectedValue();
        b.Property(x => x.BankIfscMasked).HasMaxLength(16);
        b.Property(x => x.PanCipher).HasMaxLength(256).IsProtectedValue();
        b.Property(x => x.PanIndex).HasMaxLength(64).IsBlindIndexOf(nameof(Employee.PanCipher));
        b.Property(x => x.PanMasked).HasMaxLength(16);
        b.Property(x => x.Uan).HasMaxLength(12);
        b.Property(x => x.PfNumber).HasMaxLength(32);
        b.Property(x => x.EsiNumber).HasMaxLength(17);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.ComplexProperty(x => x.Address, a =>
        {
            a.Property(p => p.Line1).HasMaxLength(200);
            a.Property(p => p.Line2).HasMaxLength(200);
            a.Property(p => p.City).HasMaxLength(100);
            a.Property(p => p.District).HasMaxLength(100);
            a.Property(p => p.State).HasMaxLength(100);
            a.Property(p => p.PinCode).HasMaxLength(10);
            a.Property(p => p.Country).HasMaxLength(100);
        });

        b.HasOne<Designation>().WithMany().HasForeignKey(x => x.DesignationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.ReportingManagerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.DefaultShiftId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.EmployeeCode).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.BiometricPin }).IsUnique().HasFilter("biometric_pin IS NOT NULL AND " + HrSchema.NotDeleted);
        b.HasIndex(x => x.UserId).IsUnique().HasFilter("user_id IS NOT NULL AND " + HrSchema.NotDeleted);
        b.HasIndex(x => x.PractitionerId).IsUnique().HasFilter("practitioner_id IS NOT NULL AND " + HrSchema.NotDeleted);
        b.HasIndex(x => new { x.FacilityId, x.Status });
        b.HasIndex(x => x.DepartmentId);
        b.HasIndex(x => x.ReportingManagerId);
        b.HasIndex(x => x.BankAccountIndex);
        b.HasIndex(x => x.PanIndex);
        b.HasIndex(x => x.Phone);
        b.HasIndex(x => x.FullName).HasMethod("gin").HasOperators("gin_trgm_ops");
    }
}

internal sealed class CredentialConfiguration : IEntityTypeConfiguration<Credential>
{
    public void Configure(EntityTypeBuilder<Credential> b)
    {
        b.ToTable("credentials", HrSchema.Name);
        b.Property(x => x.Name).HasMaxLength(150);
        b.Property(x => x.Number).HasMaxLength(64);
        b.Property(x => x.IssuingAuthority).HasMaxLength(150);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EmployeeId, x.Type });
        b.HasIndex(x => x.ExpiresOn);
    }
}
