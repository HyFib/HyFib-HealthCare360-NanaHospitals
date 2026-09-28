using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Dialysis;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Persistence;

internal sealed class DialysisMachineConfiguration : IEntityTypeConfiguration<DialysisMachine>
{
    public void Configure(EntityTypeBuilder<DialysisMachine> b)
    {
        b.ToTable("dialysis_machines", SpecialtyCareSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Model).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.Manufacturer).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.SerialNumber).HasMaxLength(64);
        b.Property(x => x.Station).HasMaxLength(64);
        b.Property(x => x.LastDisinfectionMethod).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Ignore(x => x.NeedsDisinfection);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique();
    }
}

internal sealed class DialysisPatientProfileConfiguration : IEntityTypeConfiguration<DialysisPatientProfile>
{
    public void Configure(EntityTypeBuilder<DialysisPatientProfile> b)
    {
        b.ToTable("dialysis_profiles", SpecialtyCareSchema.Name);
        b.Property(x => x.AccessSite).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.PrimaryDiagnosis).HasMaxLength(300);
        b.Property(x => x.AnticoagulationNotes).HasMaxLength(1000);
        b.Property(x => x.ScheduleDays).HasJsonRows();
        b.Ignore(x => x.EffectiveMaxDialyzerUses);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.PatientId }).IsUnique();
    }
}

internal sealed class DialyzerConfiguration : IEntityTypeConfiguration<Dialyzer>
{
    public void Configure(EntityTypeBuilder<Dialyzer> b)
    {
        b.ToTable("dialyzers", SpecialtyCareSchema.Name);
        b.Property(x => x.SerialNumber).HasMaxLength(64);
        b.Property(x => x.Brand).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.Model).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.DiscardReason).HasMaxLength(300);
        b.Ignore(x => x.RemainingUses);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.IsDiscarded });
        b.HasIndex(x => x.SerialNumber);
    }
}

internal sealed class DialysisSessionConfiguration : IEntityTypeConfiguration<DialysisSession>
{
    /// <summary>Sessions that occupy a machine/patient slot (cancelled and missed ones free it).</summary>
    private const string ActiveSlot = "status IN ('Scheduled', 'InProgress', 'Completed') AND is_deleted = false";

    public void Configure(EntityTypeBuilder<DialysisSession> b)
    {
        b.ToTable("dialysis_sessions", SpecialtyCareSchema.Name);
        b.Property(x => x.SessionNumber).HasMaxLength(24);
        b.Property(x => x.ComplicationNotes).HasMaxLength(1000);
        b.Property(x => x.PerformedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.CancelReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.ChargeError).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.Readings).HasJsonRows();
        b.Property(x => x.Complications).HasJsonRows();
        b.Ignore(x => x.DurationMinutes);

        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DialysisMachine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Dialyzer>().WithMany().HasForeignKey(x => x.DialyzerId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.SessionNumber).IsUnique();
        b.HasIndex(x => new { x.ScheduledDate, x.Shift });
        b.HasIndex(x => new { x.PatientId, x.ScheduledDate });

        // Database-level guarantee against double-booking a machine or a patient in the same shift.
        b.HasIndex(x => new { x.MachineId, x.ScheduledDate, x.Shift }).IsUnique()
            .HasFilter("machine_id IS NOT NULL AND " + ActiveSlot).HasDatabaseName("ux_dialysis_sessions_machine_slot");
        b.HasIndex(x => new { x.PatientId, x.ScheduledDate, x.Shift }).IsUnique()
            .HasFilter(ActiveSlot).HasDatabaseName("ux_dialysis_sessions_patient_slot");
    }
}
