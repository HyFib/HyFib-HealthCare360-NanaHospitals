using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.BloodBank;

namespace HyFib.HealthCare360.Modules.SpecialtyCare.Persistence;

internal sealed class DonorConfiguration : IEntityTypeConfiguration<Donor>
{
    public void Configure(EntityTypeBuilder<Donor> b)
    {
        b.ToTable("donors", SpecialtyCareSchema.Name);
        b.Property(x => x.DonorNumber).HasMaxLength(20);
        b.Property(x => x.FirstName).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.LastName).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.FullName).HasMaxLength(201);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Occupation).HasMaxLength(SpecialtyCareSchema.ShortText);
        b.Property(x => x.DeferralReason).HasMaxLength(SpecialtyCareSchema.Reason);
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
        b.HasIndex(x => x.DonorNumber).IsUnique();
        b.HasIndex(x => x.Phone);
        b.HasIndex(x => new { x.FirstName, x.DateOfBirth });
    }
}

internal sealed class DonorScreeningConfiguration : IEntityTypeConfiguration<DonorScreening>
{
    public void Configure(EntityTypeBuilder<DonorScreening> b)
    {
        b.ToTable("donor_screenings", SpecialtyCareSchema.Name);
        b.Property(x => x.ScreenedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.HaemoglobinGdl).HasPrecision(4, 1);
        b.Property(x => x.WeightKg).HasPrecision(5, 1);
        b.Property(x => x.TemperatureC).HasPrecision(4, 1);
        b.Property(x => x.Answers).HasJsonRows();
        b.HasOne<Donor>().WithMany().HasForeignKey(x => x.DonorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.DonorId, x.ScreenedAt });
    }
}

internal sealed class DonationConfiguration : IEntityTypeConfiguration<Donation>
{
    public void Configure(EntityTypeBuilder<Donation> b)
    {
        b.ToTable("donations", SpecialtyCareSchema.Name);
        b.Property(x => x.BagNumber).HasMaxLength(24);
        b.Property(x => x.CollectedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.DonorReaction).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.ComplexProperty(x => x.Tti);
        b.HasOne<Donor>().WithMany().HasForeignKey(x => x.DonorId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<DonorScreening>().WithMany().HasForeignKey(x => x.ScreeningId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.ReplacementForPatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.BagNumber).IsUnique();
        b.HasIndex(x => x.ScreeningId).IsUnique();
        b.HasIndex(x => new { x.Status, x.CollectedAt });
        b.HasIndex(x => x.DonorId);
    }
}

internal sealed class BloodComponentConfiguration : IEntityTypeConfiguration<BloodComponent>
{
    public void Configure(EntityTypeBuilder<BloodComponent> b)
    {
        b.ToTable("blood_components", SpecialtyCareSchema.Name);
        b.Property(x => x.UnitNumber).HasMaxLength(32);
        b.Property(x => x.StorageLocation).HasMaxLength(64);
        b.Property(x => x.DiscardReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.ComplexProperty(x => x.Tti);
        b.Ignore(x => x.IsInStock);
        b.HasOne<Donation>().WithMany().HasForeignKey(x => x.DonationId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.UnitNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.ComponentType, x.BloodGroup, x.ExpiresAt });
        b.HasIndex(x => x.ReservedForRequestId);
        b.HasIndex(x => x.DonationId);
    }
}

internal sealed class BloodRequestConfiguration : IEntityTypeConfiguration<BloodRequest>
{
    public void Configure(EntityTypeBuilder<BloodRequest> b)
    {
        b.ToTable("blood_requests", SpecialtyCareSchema.Name);
        b.Property(x => x.RequestNumber).HasMaxLength(24);
        b.Property(x => x.Indication).HasMaxLength(1000);
        b.Property(x => x.RequestedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.GroupedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.EmergencyReleaseAuthorisedBy).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.EmergencyReleaseReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.CancelReason).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.HaemoglobinGdl).HasPrecision(4, 1);
        b.Ignore(x => x.IsOpen);
        b.Ignore(x => x.IsGrouped);
        b.Ignore(x => x.EmergencyReleaseAuthorised);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.RequestNumber).IsUnique();
        b.HasIndex(x => x.OrderItemId).IsUnique().HasFilter("order_item_id IS NOT NULL");
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => x.AdmissionId);
    }
}

internal sealed class CrossMatchConfiguration : IEntityTypeConfiguration<CrossMatch>
{
    public void Configure(EntityTypeBuilder<CrossMatch> b)
    {
        b.ToTable("crossmatches", SpecialtyCareSchema.Name);
        b.Property(x => x.UnitNumber).HasMaxLength(32);
        b.Property(x => x.PerformedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Remarks).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.ChargeError).HasMaxLength(SpecialtyCareSchema.Reason);
        b.HasOne<BloodRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BloodComponent>().WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.RequestId, x.ComponentId });
        b.HasIndex(x => x.ComponentId);
    }
}

internal sealed class BloodIssueConfiguration : IEntityTypeConfiguration<BloodIssue>
{
    public void Configure(EntityTypeBuilder<BloodIssue> b)
    {
        b.ToTable("blood_issues", SpecialtyCareSchema.Name);
        b.Property(x => x.UnitNumber).HasMaxLength(32);
        b.Property(x => x.IssuedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.IssuedToName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.ReturnNotes).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.ChargeError).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Ignore(x => x.IsReturned);
        b.HasOne<BloodRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BloodComponent>().WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.PatientId, x.IssuedAt });
        b.HasIndex(x => x.RequestId);
        b.HasIndex(x => x.ComponentId);
    }
}

internal sealed class TransfusionReactionConfiguration : IEntityTypeConfiguration<TransfusionReaction>
{
    public void Configure(EntityTypeBuilder<TransfusionReaction> b)
    {
        b.ToTable("transfusion_reactions", SpecialtyCareSchema.Name);
        b.Property(x => x.UnitNumber).HasMaxLength(32);
        b.Property(x => x.ReportedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Vitals).HasMaxLength(SpecialtyCareSchema.Reason);
        b.Property(x => x.ActionsTaken).HasMaxLength(SpecialtyCareSchema.Note);
        b.Property(x => x.InvestigationFindings).HasMaxLength(4000);
        b.Property(x => x.ClosedByName).HasMaxLength(SpecialtyCareSchema.Name200);
        b.Property(x => x.Symptoms).HasJsonRows();
        b.Ignore(x => x.IsSerious);
        b.HasOne<BloodIssue>().WithMany().HasForeignKey(x => x.IssueId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.Status, x.ReportedAt });
        b.HasIndex(x => x.PatientId);
    }
}
