using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.VirtualCare.Domain;

namespace HyFib.HealthCare360.Modules.VirtualCare.Persistence;

internal static class VirtualCareSchema
{
    public const string Name = "virtualcare";
}

internal sealed class TelehealthSessionConfiguration : IEntityTypeConfiguration<TelehealthSession>
{
    public void Configure(EntityTypeBuilder<TelehealthSession> b)
    {
        b.ToTable("telehealth_sessions", VirtualCareSchema.Name);
        b.Property(x => x.AppointmentNumber).HasMaxLength(24);
        b.Property(x => x.JoinTokenHash).HasMaxLength(64);
        b.Property(x => x.QualityNotes).HasMaxLength(TelehealthSession.MaxNotesLength);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Ignore(x => x.IsClosed);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        // One live session per appointment (a cancelled one may be followed by a new one).
        b.HasIndex(x => x.AppointmentId)
            .IsUnique()
            .HasFilter("appointment_id IS NOT NULL AND status <> 'Cancelled' AND is_deleted = false")
            .HasDatabaseName("ux_telehealth_sessions_appointment");
        b.HasIndex(x => new { x.PractitionerId, x.ScheduledStartAt });
        b.HasIndex(x => new { x.PatientId, x.ScheduledStartAt });
        b.HasIndex(x => new { x.Status, x.ScheduledEndAt });
        b.HasIndex(x => x.VisitId);
    }
}

internal sealed class TelehealthParticipantConfiguration : IEntityTypeConfiguration<TelehealthParticipant>
{
    public void Configure(EntityTypeBuilder<TelehealthParticipant> b)
    {
        b.ToTable("telehealth_participants", VirtualCareSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.DisplayName).HasMaxLength(200);
        b.Property(x => x.ConnectionId).HasMaxLength(128);
        b.Property(x => x.MaxPacketLossPercent).HasPrecision(5, 2);
        b.Ignore(x => x.IsPatientSide);
        b.Ignore(x => x.IsWaiting);
        b.HasOne<TelehealthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.SessionId, x.JoinedAt });
        b.HasIndex(x => x.ConnectionId);
    }
}

internal sealed class TelehealthMessageConfiguration : IEntityTypeConfiguration<TelehealthMessage>
{
    public void Configure(EntityTypeBuilder<TelehealthMessage> b)
    {
        b.ToTable("telehealth_messages", VirtualCareSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.SenderName).HasMaxLength(200);
        b.Property(x => x.Text).HasMaxLength(TelehealthMessage.MaxLength);
        b.HasOne<TelehealthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.SessionId, x.SentAt });
    }
}
