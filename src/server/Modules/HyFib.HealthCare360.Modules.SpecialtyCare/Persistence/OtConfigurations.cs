using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Persistence;

internal sealed class OperationTheatreConfiguration : IEntityTypeConfiguration<OperationTheatre>
{
    public void Configure(EntityTypeBuilder<OperationTheatre> b)
    {
        b.ToTable("theatres", SpecialtyCareSchema.Name);
        b.Property(x => x.Code).HasMaxLength(16);
        b.Property(x => x.Name).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.Location).HasMaxLength(SpecialtyCareSchema.Name200);
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique();
    }
}

internal sealed class SurgeryBookingConfiguration : IEntityTypeConfiguration<SurgeryBooking>
{
    internal const int SiteLevelLength = 60;

    public void Configure(EntityTypeBuilder<SurgeryBooking> b)
    {
        b.ToTable("surgery_bookings", SpecialtyCareSchema.Name);
        b.Property(x => x.BookingNumber).HasMaxLength(24);
        b.Property(x => x.ProcedureCode).HasMaxLength(32);
        b.Property(x => x.ProcedureName).HasMaxLength(300);
        b.Property(x => x.Diagnosis).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.Notes).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.CancelReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.PostponeReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.ChargeError).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.PreOpChecks).HasJsonRows();
        b.Property(x => x.SafetyChecks).HasJsonRows();
        b.Property(x => x.SiteLevel).HasMaxLength(SiteLevelLength);
        b.Property(x => x.Counts).HasJsonRows();
        b.Property(x => x.CountDiscrepancies).HasJsonRows();
        b.Property(x => x.ChecklistVoids).HasJsonRows();
        b.Ignore(x => x.RequiresAnaesthetist);
        b.Ignore(x => x.IsClosed);
        b.Ignore(x => x.Site);
        b.Ignore(x => x.MaterialsLocked);
        b.Ignore(x => x.MinutesInTheatre);
        b.Ignore(x => x.InRecovery);

        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<OperationTheatre>().WithMany().HasForeignKey(x => x.TheatreId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => x.BookingNumber).IsUnique();
        b.HasIndex(x => x.OrderItemId).IsUnique().HasFilter("order_item_id IS NOT NULL");
        b.HasIndex(x => new { x.TheatreId, x.ScheduledStart });
        b.HasIndex(x => new { x.SurgeonId, x.ScheduledStart });
        b.HasIndex(x => new { x.Status, x.ScheduledStart });
        b.HasIndex(x => new { x.PatientId, x.Status });
        b.HasIndex(x => x.AdmissionId);
    }
}

internal sealed class AnaesthesiaRecordConfiguration : IEntityTypeConfiguration<AnaesthesiaRecord>
{
    public void Configure(EntityTypeBuilder<AnaesthesiaRecord> b)
    {
        b.ToTable("anaesthesia_records", SpecialtyCareSchema.Name);
        b.Property(x => x.AirwayNotes).HasMaxLength(1000);
        b.Property(x => x.Complications).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.RecoveryNotes).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.SignedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Vitals).HasJsonRows();
        b.Property(x => x.Drugs).HasJsonRows();
        b.Property(x => x.Fluids).HasJsonRows();
        b.Ignore(x => x.IsSigned);
        b.HasOne<SurgeryBooking>().WithMany().HasForeignKey(x => x.SurgeryBookingId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.SurgeryBookingId).IsUnique();
    }
}

internal sealed class RecoveryRecordConfiguration : IEntityTypeConfiguration<RecoveryRecord>
{
    public void Configure(EntityTypeBuilder<RecoveryRecord> b)
    {
        b.ToTable("recovery_records", SpecialtyCareSchema.Name);
        b.Property(x => x.ReceivedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.HandedOverByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.DischargedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.OverrideByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Notes).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.HandoverNotes).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.OverrideReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.OxygenLitresPerMinute).HasPrecision(4, 1);
        b.Property(x => x.Observations).HasJsonRows();
        b.Ignore(x => x.IsDischarged);
        b.Ignore(x => x.Latest);
        b.HasOne<SurgeryBooking>().WithMany().HasForeignKey(x => x.SurgeryBookingId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.SurgeryBookingId).IsUnique();
    }
}

internal sealed class OperationNoteConfiguration : IEntityTypeConfiguration<OperationNote>
{
    public void Configure(EntityTypeBuilder<OperationNote> b)
    {
        b.ToTable("operation_notes", SpecialtyCareSchema.Name);
        b.Property(x => x.PreOperativeDiagnosis).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.PostOperativeDiagnosis).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.ProcedurePerformed).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.Findings).HasMaxLength(4000);
        b.Property(x => x.ProcedureDetails).HasMaxLength(SpecialtyCareSchema.LongNote);
        b.Property(x => x.Drains).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.Complications).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.PostOpOrders).HasMaxLength(4000);
        b.Property(x => x.SignedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Specimens).HasJsonRows();
        b.Ignore(x => x.IsSigned);
        b.HasOne<SurgeryBooking>().WithMany().HasForeignKey(x => x.SurgeryBookingId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.SurgeryBookingId).IsUnique();
    }
}

internal sealed class SurgeryMaterialConfiguration : IEntityTypeConfiguration<SurgeryMaterial>
{
    public void Configure(EntityTypeBuilder<SurgeryMaterial> b)
    {
        b.ToTable("surgery_materials", SpecialtyCareSchema.Name);
        b.Property(x => x.ItemName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Manufacturer).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Brand).HasMaxLength(120);
        b.Property(x => x.Specification).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.LotNumber).HasMaxLength(64);
        b.Property(x => x.SerialNumber).HasMaxLength(64);
        b.Property(x => x.RecordedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.ChargeError).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.CorrectionReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.CorrectionRequestedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.CorrectionApprovedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.Mrp).HasPrecision(18, 4);
        b.Property(x => x.CeilingPrice).HasPrecision(18, 4);
        b.Ignore(x => x.Amount);
        b.Ignore(x => x.Device);
        b.Ignore(x => x.IsSuperseded);
        b.HasOne<SurgeryBooking>().WithMany().HasForeignKey(x => x.SurgeryBookingId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.SurgeryBookingId);
        b.HasIndex(x => x.LotNumber);
        b.HasIndex(x => x.SerialNumber);
    }
}
