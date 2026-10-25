using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Clinical.Persistence;

internal sealed class EmergencyCaseConfiguration : IEntityTypeConfiguration<EmergencyCase>
{
    public void Configure(EntityTypeBuilder<EmergencyCase> b)
    {
        b.ToTable("emergency_cases", ClinicalSchema.Name);
        b.Property(x => x.DoctorName).HasMaxLength(200);
        b.Property(x => x.DisposedByName).HasMaxLength(200);
        b.Property(x => x.DispositionNotes).HasMaxLength(1000);
        b.Property(x => x.ReferredTo).HasMaxLength(200);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.VisitId).IsUnique();

        // The board reads the open cases of the facility.
        b.HasIndex(x => new { x.FacilityId, x.Disposition });
    }
}

internal sealed class EsiTriageConfiguration : IEntityTypeConfiguration<EsiTriage>
{
    public void Configure(EntityTypeBuilder<EsiTriage> b)
    {
        b.ToTable("esi_triages", ClinicalSchema.Name);
        b.Property(x => x.AssessedByName).HasMaxLength(200);
        b.Property(x => x.ChiefComplaint).HasMaxLength(300);
        b.Property(x => x.Reasons).HasMaxLength(2000);
        b.Property(x => x.OverrideReason).HasMaxLength(300);
        b.Property(x => x.TemperatureCelsius).HasPrecision(4, 1);
        b.HasOne<EmergencyCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.CaseId);
    }
}

internal sealed class MlcRecordConfiguration : IEntityTypeConfiguration<MlcRecord>
{
    public void Configure(EntityTypeBuilder<MlcRecord> b)
    {
        b.ToTable("mlc_records", ClinicalSchema.Name);
        b.Property(x => x.Number).HasMaxLength(24);
        b.Property(x => x.BroughtByName).HasMaxLength(200);
        b.Property(x => x.IncidentPlace).HasMaxLength(300);
        b.Property(x => x.Injuries).HasMaxLength(4000);
        b.Property(x => x.DoctorName).HasMaxLength(200);
        b.Property(x => x.DoctorRegistrationNumber).HasMaxLength(64);
        b.Property(x => x.PoliceStation).HasMaxLength(200);
        b.Property(x => x.ReceivedBy).HasMaxLength(200);
        b.Property(x => x.PoliceEntryNumber).HasMaxLength(64);
        b.Ignore(x => x.IsIntimated);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Visit>().WithMany().HasForeignKey(x => x.VisitId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.VisitId).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Number }).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.IdentifiedAt });
    }
}
