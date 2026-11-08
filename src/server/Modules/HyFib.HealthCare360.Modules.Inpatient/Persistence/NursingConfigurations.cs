using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Persistence;

internal sealed class NursingNoteConfiguration : IEntityTypeConfiguration<NursingNote>
{
    public void Configure(EntityTypeBuilder<NursingNote> b)
    {
        b.ToTable("nursing_notes", InpatientSchema.Name);
        b.Property(x => x.Note).HasMaxLength(4000);
        b.Property(x => x.RecordedByName).HasMaxLength(200);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.NoteFor });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class ShiftHandoverConfiguration : IEntityTypeConfiguration<ShiftHandover>
{
    public void Configure(EntityTypeBuilder<ShiftHandover> b)
    {
        b.ToTable("shift_handovers", InpatientSchema.Name);
        b.Property(x => x.Situation).HasMaxLength(2000);
        b.Property(x => x.Background).HasMaxLength(2000);
        b.Property(x => x.Assessment).HasMaxLength(2000);
        b.Property(x => x.Recommendation).HasMaxLength(2000);
        b.Property(x => x.PendingTasks).HasMaxLength(1000);
        b.Property(x => x.HandedOverByName).HasMaxLength(200);
        b.Property(x => x.AcknowledgedByName).HasMaxLength(200);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.HandedOverAt });
        b.HasIndex(x => new { x.WardId, x.DutyDate, x.FromShift });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class IntakeOutputEntryConfiguration : IEntityTypeConfiguration<IntakeOutputEntry>
{
    public void Configure(EntityTypeBuilder<IntakeOutputEntry> b)
    {
        b.ToTable("intake_output_entries", InpatientSchema.Name);
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.RecordedByName).HasMaxLength(200);
        b.Property(x => x.VoidReason).HasMaxLength(300);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.RecordedAt });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class NursingAssessmentConfiguration : IEntityTypeConfiguration<NursingAssessment>
{
    public void Configure(EntityTypeBuilder<NursingAssessment> b)
    {
        b.ToTable("nursing_assessments", InpatientSchema.Name);
        b.Property(x => x.Interpretation).HasMaxLength(300);
        b.Property(x => x.Responses).HasColumnType("jsonb");
        b.Property(x => x.AssessedByName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.Type, x.AssessedAt });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class MedicationOrderConfiguration : IEntityTypeConfiguration<MedicationOrder>
{
    public void Configure(EntityTypeBuilder<MedicationOrder> b)
    {
        b.ToTable("medication_orders", InpatientSchema.Name);
        b.Property(x => x.DrugName).HasMaxLength(200);
        b.Property(x => x.GenericName).HasMaxLength(200);
        b.Property(x => x.Strength).HasMaxLength(64);
        b.Property(x => x.Dose).HasMaxLength(64);
        b.Property(x => x.Route).HasMaxLength(32);
        b.Property(x => x.Frequency).HasMaxLength(32);
        b.Property(x => x.Instructions).HasMaxLength(500);
        b.Property(x => x.StopReason).HasMaxLength(300);
        b.Property(x => x.EnteredByName).HasMaxLength(200);
        b.Property(x => x.CountersignedByName).HasMaxLength(200);
        b.Property(x => x.StopRequestedByName).HasMaxLength(200);
        b.Property(x => x.StopRequestReason).HasMaxLength(300);
        b.Ignore(x => x.IsAsNeeded);
        b.Ignore(x => x.AwaitingCountersignature);
        b.Ignore(x => x.IsHighRisk);
        b.Ignore(x => x.StopPending);
        b.Ignore(x => x.DosesHeld);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.Status });

        // The orders waiting for a doctor's countersignature, and those past their time, are found without reading the rest.
        b.HasIndex(x => x.CountersignDueAt).HasFilter("countersigned_at IS NULL AND mode <> 'Written'");
        b.HasIndex(x => x.StopCountersignDueAt).HasFilter("stop_requested_at IS NOT NULL");
        b.HasIndex(x => x.PrescriptionLineId);
        b.HasIndex(x => x.PrescriptionId);
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class MedicationAdministrationConfiguration : IEntityTypeConfiguration<MedicationAdministration>
{
    public void Configure(EntityTypeBuilder<MedicationAdministration> b)
    {
        b.ToTable("medication_administrations", InpatientSchema.Name);
        b.Property(x => x.DrugName).HasMaxLength(200);
        b.Property(x => x.Dose).HasMaxLength(64);
        b.Property(x => x.Route).HasMaxLength(32);
        b.Property(x => x.RecordedByName).HasMaxLength(200);
        b.Property(x => x.Reason).HasMaxLength(300);
        b.Property(x => x.BatchNumber).HasMaxLength(64);
        b.Property(x => x.WitnessName).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<MedicationOrder>().WithMany().HasForeignKey(x => x.MedicationOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.ScheduledAt });
        b.HasIndex(x => new { x.Status, x.ScheduledAt });
        b.HasIndex(x => x.PatientId);
        // Scheduling is idempotent: one row per order per dose time.
        b.HasIndex(x => new { x.MedicationOrderId, x.ScheduledAt }).IsUnique().HasFilter("medication_order_id IS NOT NULL AND NOT is_deleted");
    }
}

internal sealed class NursingSafetyEventConfiguration : IEntityTypeConfiguration<NursingSafetyEvent>
{
    public void Configure(EntityTypeBuilder<NursingSafetyEvent> b)
    {
        b.ToTable("nursing_safety_events", InpatientSchema.Name);
        b.Property(x => x.Location).HasMaxLength(150);
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.ImmediateAction).HasMaxLength(1000);
        b.Property(x => x.BodySite).HasMaxLength(100);
        b.Property(x => x.RecordedByName).HasMaxLength(200);
        b.Property(x => x.IncidentNumber).HasMaxLength(24);
        b.Ignore(x => x.IsReportable);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.OccurredAt });
        b.HasIndex(x => new { x.FacilityId, x.Kind, x.OccurredAt });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class EarlyWarningEscalationConfiguration : IEntityTypeConfiguration<EarlyWarningEscalation>
{
    public void Configure(EntityTypeBuilder<EarlyWarningEscalation> b)
    {
        b.ToTable("early_warning_escalations", InpatientSchema.Name);
        b.Property(x => x.Summary).HasMaxLength(200);
        b.Property(x => x.RespondedByName).HasMaxLength(200);
        b.Property(x => x.Response).HasMaxLength(1000);
        b.Ignore(x => x.MinutesToRespond);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.Status, x.Level });
        b.HasIndex(x => new { x.WardId, x.Status });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class CarePlanProblemConfiguration : IEntityTypeConfiguration<CarePlanProblem>
{
    public void Configure(EntityTypeBuilder<CarePlanProblem> b)
    {
        b.ToTable("care_plan_problems", InpatientSchema.Name);
        b.Property(x => x.ProblemCode).HasMaxLength(32);
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.Goal).HasMaxLength(500);
        b.Property(x => x.InterventionsJson).HasColumnType("jsonb");
        b.Property(x => x.StartedByName).HasMaxLength(200);
        b.Property(x => x.ResolvedByName).HasMaxLength(200);
        b.Property(x => x.ResolutionNote).HasMaxLength(500);
        b.Ignore(x => x.Interventions);
        b.HasOne<Admission>().WithMany().HasForeignKey(x => x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.AdmissionId, x.Status });
        b.HasIndex(x => x.PatientId);
    }
}

internal sealed class CarePlanEvaluationConfiguration : IEntityTypeConfiguration<CarePlanEvaluation>
{
    public void Configure(EntityTypeBuilder<CarePlanEvaluation> b)
    {
        b.ToTable("care_plan_evaluations", InpatientSchema.Name);
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.EvaluatedByName).HasMaxLength(200);
        b.HasOne<CarePlanProblem>().WithMany().HasForeignKey(x => x.ProblemId).OnDelete(DeleteBehavior.Restrict);

        // One evaluation of a problem per shift.
        b.HasIndex(x => new { x.ProblemId, x.DutyDate, x.Shift }).IsUnique();
        b.HasIndex(x => x.AdmissionId);
    }
}
