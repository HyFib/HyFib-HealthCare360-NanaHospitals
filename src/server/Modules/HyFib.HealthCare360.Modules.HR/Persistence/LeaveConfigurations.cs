using HyFib.HealthCare360.Modules.HR.Domain;

namespace HyFib.HealthCare360.Modules.HR.Persistence;

internal sealed class LeaveTypeConfiguration : IEntityTypeConfiguration<LeaveType>
{
    public void Configure(EntityTypeBuilder<LeaveType> b)
    {
        b.ToTable("leave_types", HrSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.AnnualQuota).HasPrecision(6, 2);
        b.Property(x => x.CarryForwardCap).HasPrecision(6, 2);
        b.Property(x => x.MaxDaysPerRequest).HasPrecision(6, 2);
        b.Property(x => x.DocumentRequiredAfterDays).HasPrecision(6, 2);
        b.HasIndex(x => x.Code).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class LeaveBalanceConfiguration : IEntityTypeConfiguration<LeaveBalance>
{
    public void Configure(EntityTypeBuilder<LeaveBalance> b)
    {
        b.ToTable("leave_balances", HrSchema.Name);
        b.Property(x => x.Opening).HasPrecision(6, 2);
        b.Property(x => x.Credited).HasPrecision(6, 2);
        b.Property(x => x.Adjusted).HasPrecision(6, 2);
        b.Property(x => x.Availed).HasPrecision(6, 2);
        b.Property(x => x.Encashed).HasPrecision(6, 2);
        b.Ignore(x => x.Closing);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LeaveType>().WithMany().HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EmployeeId, x.LeaveTypeId, x.Year }).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}

internal sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> b)
    {
        b.ToTable("leave_requests", HrSchema.Name);
        b.Property(x => x.Days).HasPrecision(6, 2);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.HasOne<Employee>().WithMany().HasForeignKey(x => x.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<LeaveType>().WithMany().HasForeignKey(x => x.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.EmployeeId, x.FromDate, x.ToDate });
        b.HasIndex(x => new { x.FacilityId, x.Status });
    }
}

internal sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> b)
    {
        b.ToTable("holidays", HrSchema.Name);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Ignore(x => x.IsClosure);
        b.HasIndex(x => new { x.FacilityId, x.Date }).IsUnique().HasFilter(HrSchema.NotDeleted);
    }
}
