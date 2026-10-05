using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Domain;

namespace HyFib.HealthCare360.Modules.Scheduling.Persistence;

internal static class SchedulingSchema
{
    public const string Name = "scheduling";
}

internal sealed class DoctorScheduleConfiguration : IEntityTypeConfiguration<DoctorSchedule>
{
    public void Configure(EntityTypeBuilder<DoctorSchedule> b)
    {
        b.ToTable("doctor_schedules", SchedulingSchema.Name);
        b.Property(x => x.Room).HasMaxLength(32);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Ignore(x => x.SlotsPerSession);
        b.HasIndex(x => new { x.PractitionerId, x.Weekday });
        b.HasIndex(x => new { x.FacilityId, x.DepartmentId });
    }
}

internal sealed class ScheduleExceptionConfiguration : IEntityTypeConfiguration<ScheduleException>
{
    public void Configure(EntityTypeBuilder<ScheduleException> b)
    {
        b.ToTable("schedule_exceptions", SchedulingSchema.Name);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.Room).HasMaxLength(32);
        b.Ignore(x => x.IsAllDay);
        b.Ignore(x => x.RemovesSlots);
        b.Ignore(x => x.IsCompleteExtraSession);
        b.HasIndex(x => new { x.PractitionerId, x.FromDate, x.ToDate });
    }
}

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    /// <summary>Seat uniqueness applies only to bookings that still hold their seat.</summary>
    internal const string ActiveSeatFilter = "status NOT IN ('Cancelled', 'Rescheduled') AND is_deleted = false";

    public void Configure(EntityTypeBuilder<Appointment> b)
    {
        b.ToTable("appointments", SchedulingSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.Room).HasMaxLength(32);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.PaymentProvider).HasMaxLength(32);
        b.Property(x => x.GatewayOrderId).HasMaxLength(64);
        b.Property(x => x.GatewayPaymentId).HasMaxLength(64);
        b.Ignore(x => x.HoldsSeat);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.Number).IsUnique();
        b.HasIndex(x => new { x.PractitionerId, x.StartsAt });
        b.HasIndex(x => new { x.PatientId, x.StartsAt });
        b.HasIndex(x => new { x.FacilityId, x.StartsAt });
        b.HasIndex(x => new { x.Status, x.StartsAt });
        b.HasIndex(x => x.VisitId);
        b.HasIndex(x => x.RescheduledFromId);

        // Payment-gateway webhooks and Billing's payment re-check find a prepaid booking by its gateway order.
        b.HasIndex(x => x.GatewayOrderId).HasFilter("gateway_order_id IS NOT NULL");

        // Capacity is enforced by the database as well as by the booking lock: one active booking per seat.
        b.HasIndex(x => new { x.PractitionerId, x.StartsAt, x.SeatNumber })
            .IsUnique()
            .HasFilter(ActiveSeatFilter)
            .HasDatabaseName("ux_appointments_practitioner_slot_seat");
    }
}

internal sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> b)
    {
        b.ToTable("waitlist_entries", SchedulingSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PractitionerId, x.Status, x.PreferredFrom });
        b.HasIndex(x => new { x.PatientId, x.Status });
    }
}
