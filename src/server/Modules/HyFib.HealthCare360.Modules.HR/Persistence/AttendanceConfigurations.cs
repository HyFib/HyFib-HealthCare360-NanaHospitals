using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Persistence;

internal sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> b)
    {
        b.ToTable("shifts", HrSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Color).HasMaxLength(7);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class RosterEntryConfiguration : IEntityTypeConfiguration<RosterEntry>
{
    public void Configure(EntityTypeBuilder<RosterEntry> b)
    {
        b.ToTable("roster_entries", HrSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EmployeeId, x.Date });
        b.HasIndex(x => new { x.FacilityId, x.Date });
    }
}

internal sealed class BiometricDeviceConfiguration : IEntityTypeConfiguration<BiometricDevice>
{
    public void Configure(EntityTypeBuilder<BiometricDevice> b)
    {
        b.ToTable("biometric_devices", HrSchema.Name);
        b.Property(x => x.SerialNumber).HasMaxLength(AdmsProtocol.MaxSerialLength);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Location).HasMaxLength(150);
        b.Property(x => x.Model).HasMaxLength(64);
        b.Property(x => x.AllowedIpAddress).HasMaxLength(64);
        b.Property(x => x.ApiKeyHash).HasMaxLength(64);
        b.Property(x => x.LastIpAddress).HasMaxLength(64);
        b.Property(x => x.AttLogStamp).HasMaxLength(20);
        b.Property(x => x.Connection).HasMaxLength(16);
        b.Property(x => x.PullAddress).HasMaxLength(BiometricDevice.MaxPullAddressLength);
        b.Property(x => x.LastPullError).HasMaxLength(500);
        b.HasIndex(x => x.SerialNumber).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class AttendancePunchConfiguration : IEntityTypeConfiguration<AttendancePunch>
{
    public void Configure(EntityTypeBuilder<AttendancePunch> b)
    {
        b.ToTable("attendance_punches", HrSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Pin).HasMaxLength(ZkTecoAttLogParser.MaxPinLength);
        b.Property(x => x.WorkCode).HasMaxLength(16);
        b.HasIndex(x => new { x.BiometricDeviceId, x.Pin, x.PunchedAt }).IsUnique();
        b.HasIndex(x => new { x.EmployeeId, x.PunchedAt });
        b.HasIndex(x => new { x.FacilityId, x.Pin }).HasFilter("employee_id IS NULL");
    }
}

internal sealed class AttendanceDayConfiguration : IEntityTypeConfiguration<AttendanceDay>
{
    public void Configure(EntityTypeBuilder<AttendanceDay> b)
    {
        b.ToTable("attendance_days", HrSchema.Name);
        b.Property(x => x.Flags).HasConversion<int>();
        b.Property(x => x.PresentDays).HasPrecision(4, 2);
        b.Property(x => x.PaidLeaveDays).HasPrecision(4, 2);
        b.Property(x => x.UnpaidLeaveDays).HasPrecision(4, 2);
        b.Property(x => x.AbsentDays).HasPrecision(4, 2);
        b.Property(x => x.OffDays).HasPrecision(4, 2);
        b.Ignore(x => x.LopDays);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EmployeeId, x.Date }).IsUnique().HasFilter(HrSchema.NotDeleted);
        b.HasIndex(x => new { x.FacilityId, x.Date, x.Status });
    }
}

internal sealed class AttendanceRegularisationConfiguration : IEntityTypeConfiguration<AttendanceRegularisation>
{
    public void Configure(EntityTypeBuilder<AttendanceRegularisation> b)
    {
        b.ToTable("attendance_regularisations", HrSchema.Name);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EmployeeId, x.Date });
        b.HasIndex(x => new { x.FacilityId, x.Status });
    }
}
