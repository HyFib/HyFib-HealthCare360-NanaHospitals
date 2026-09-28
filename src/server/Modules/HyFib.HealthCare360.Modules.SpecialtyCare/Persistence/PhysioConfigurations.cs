using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Physiotherapy;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Persistence;

internal sealed class PhysioReferralConfiguration : IEntityTypeConfiguration<PhysioReferral>
{
    public void Configure(EntityTypeBuilder<PhysioReferral> b)
    {
        b.ToTable("physio_referrals", SpecialtyCareSchema.Name);
        b.Property(x => x.OrderNumber).HasMaxLength(32);
        b.Property(x => x.ServiceName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Reason).HasMaxLength(1000);
        b.Property(x => x.AssignedTherapistName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.DischargeSummary).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.CancelReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.OrderItemId).IsUnique().HasFilter("order_item_id IS NOT NULL");
        b.HasIndex(x => new { x.Status, x.ReferredAt });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class PhysioAssessmentConfiguration : IEntityTypeConfiguration<PhysioAssessment>
{
    public void Configure(EntityTypeBuilder<PhysioAssessment> b)
    {
        b.ToTable("physio_assessments", SpecialtyCareSchema.Name);
        b.Property(x => x.AssessedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.ChiefComplaint).HasMaxLength(1000);
        b.Property(x => x.PainLocation).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.FunctionalStatus).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.Goals).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.Notes).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.RangeOfMotion).HasJsonRows();
        b.Property(x => x.Strength).HasJsonRows();
        b.HasOne<PhysioReferral>().WithMany().HasForeignKey(x => x.ReferralId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ReferralId, x.AssessedAt });
    }
}

internal sealed class TreatmentPlanConfiguration : IEntityTypeConfiguration<TreatmentPlan>
{
    public void Configure(EntityTypeBuilder<TreatmentPlan> b)
    {
        b.ToTable("physio_treatment_plans", SpecialtyCareSchema.Name);
        b.Property(x => x.ExerciseProgramme).HasMaxLength(4000);
        b.Property(x => x.Precautions).HasMaxLength(1000);
        b.Property(x => x.Goals).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.Modalities).HasJsonRows();
        b.HasOne<PhysioReferral>().WithMany().HasForeignKey(x => x.ReferralId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.ReferralId).IsUnique();
    }
}

internal sealed class PhysioSessionConfiguration : IEntityTypeConfiguration<PhysioSession>
{
    public void Configure(EntityTypeBuilder<PhysioSession> b)
    {
        b.ToTable("physio_sessions", SpecialtyCareSchema.Name);
        b.Property(x => x.ProgressNote).HasMaxLength(4000);
        b.Property(x => x.TherapistName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.MissedReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.ChargeError).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.ModalitiesGiven).HasJsonRows();
        b.Ignore(x => x.CountsAgainstPlan);
        b.HasOne<PhysioReferral>().WithMany().HasForeignKey(x => x.ReferralId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.ReferralId, x.SessionNumber }).IsUnique();
        b.HasIndex(x => new { x.ScheduledAt, x.Status });
    }
}
