using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Persistence;

internal static class EngagementSchema
{
    public const string Name = "engagement";

    /// <summary>Unique indexes skip soft-deleted rows.</summary>
    public const string NotDeleted = "is_deleted = false";
}

internal sealed class LeadConfiguration : IEntityTypeConfiguration<Lead>
{
    public void Configure(EntityTypeBuilder<Lead> b)
    {
        b.ToTable("leads", EngagementSchema.Name);
        b.Property(x => x.LeadNumber).HasMaxLength(24);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.SourceDetail).HasMaxLength(200);
        b.Property(x => x.InterestPackage).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.LostReason).HasMaxLength(300);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.ConvertedPatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.LeadNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.NextFollowUpAt });
        b.HasIndex(x => x.Phone);
        b.HasIndex(x => x.OwnerUserId);
    }
}

internal sealed class LeadActivityConfiguration : IEntityTypeConfiguration<LeadActivity>
{
    public void Configure(EntityTypeBuilder<LeadActivity> b)
    {
        b.ToTable("lead_activities", EngagementSchema.Name);
        b.Property(x => x.Outcome).HasMaxLength(200);
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.LeadId, x.OccurredAt });
    }
}

internal sealed class FollowUpTaskConfiguration : IEntityTypeConfiguration<FollowUpTask>
{
    public void Configure(EntityTypeBuilder<FollowUpTask> b)
    {
        b.ToTable("follow_up_tasks", EngagementSchema.Name);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.OutcomeNotes).HasMaxLength(1000);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Property(x => x.SourceReference).HasMaxLength(100);
        b.Ignore(x => x.IsOpen);
        b.HasOne<Patient>().WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.Status, x.DueDate });
        b.HasIndex(x => new { x.AssigneeUserId, x.Status });
        b.HasIndex(x => new { x.PatientId, x.Status });
        b.HasIndex(x => new { x.Source, x.SourceReference, x.Type }).IsUnique()
            .HasFilter("source_reference IS NOT NULL AND " + EngagementSchema.NotDeleted);
    }
}

internal sealed class ReferringDoctorConfiguration : IEntityTypeConfiguration<ReferringDoctor>
{
    public void Configure(EntityTypeBuilder<ReferringDoctor> b)
    {
        b.ToTable("referring_doctors", EngagementSchema.Name);
        b.Property(x => x.Name).HasMaxLength(200);
        b.Property(x => x.Clinic).HasMaxLength(200);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Specialty).HasMaxLength(100);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.RegistrationNumber).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Ignore(x => x.AllNames);
        b.HasIndex(x => new { x.FacilityId, x.Name });
    }
}

internal sealed class CampaignConfiguration : IEntityTypeConfiguration<Campaign>
{
    public void Configure(EntityTypeBuilder<Campaign> b)
    {
        b.ToTable("campaigns", EngagementSchema.Name);
        b.Property(x => x.Name).HasMaxLength(150);
        b.Property(x => x.TemplateKey).HasMaxLength(64);
        b.Property(x => x.Message).HasMaxLength(1000);
        b.Property(x => x.SegmentCity).HasMaxLength(100);
        b.Property(x => x.CancelReason).HasMaxLength(300);
        b.Ignore(x => x.Segment);
        b.Ignore(x => x.IsEditable);
        b.HasIndex(x => new { x.Status, x.ScheduledAt });
    }
}

internal sealed class CampaignRecipientConfiguration : IEntityTypeConfiguration<CampaignRecipient>
{
    public void Configure(EntityTypeBuilder<CampaignRecipient> b)
    {
        b.ToTable("campaign_recipients", EngagementSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.SkipReason).HasMaxLength(200);
        b.Property(x => x.OptOutTokenHash).HasMaxLength(64);
        b.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.CampaignId, x.PatientId }).IsUnique();
        b.HasIndex(x => x.OptOutTokenHash).IsUnique();
        b.HasIndex(x => x.NotificationLogId);
    }
}

internal sealed class FeedbackFormConfiguration : IEntityTypeConfiguration<FeedbackForm>
{
    public void Configure(EntityTypeBuilder<FeedbackForm> b)
    {
        b.ToTable("feedback_forms", EngagementSchema.Name);
        b.Property(x => x.Code).HasMaxLength(32);
        b.Property(x => x.Title).HasMaxLength(150);
        b.Property(x => x.IntroText).HasMaxLength(500);
        b.Property(x => x.ThankYouText).HasMaxLength(500);
        b.Property(x => x.PublicToken).HasMaxLength(OpaqueToken.MaxLength);
        b.Ignore(x => x.AspectList);
        b.HasIndex(x => x.PublicToken).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique().HasFilter(EngagementSchema.NotDeleted);
    }
}

internal sealed class FeedbackResponseConfiguration : IEntityTypeConfiguration<FeedbackResponse>
{
    public void Configure(EntityTypeBuilder<FeedbackResponse> b)
    {
        b.ToTable("feedback_responses", EngagementSchema.Name);
        b.Property(x => x.Comment).HasMaxLength(2000);
        b.Property(x => x.ContactPhone).HasMaxLength(20);
        b.Ignore(x => x.Ratings);
        b.HasOne<FeedbackForm>().WithMany().HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.FacilityId, x.SubmittedAt });
        b.HasIndex(x => new { x.PatientId, x.SubmittedAt }).HasFilter("patient_id IS NOT NULL");
        b.HasIndex(x => new { x.DepartmentId, x.SubmittedAt });

        // A personal link answers once, even if the patient double-submits (the second insert is a 409).
        b.HasIndex(x => x.InvitationId).IsUnique().HasFilter("invitation_id IS NOT NULL");
    }
}

internal sealed class FeedbackInvitationConfiguration : IEntityTypeConfiguration<FeedbackInvitation>
{
    public void Configure(EntityTypeBuilder<FeedbackInvitation> b)
    {
        b.ToTable("feedback_invitations", EngagementSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).HasMaxLength(64);
        b.HasOne<FeedbackForm>().WithMany().HasForeignKey(x => x.FormId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => new { x.AdmissionId, x.FormId });
    }
}

internal sealed class ComplaintConfiguration : IEntityTypeConfiguration<Complaint>
{
    public void Configure(EntityTypeBuilder<Complaint> b)
    {
        b.ToTable("complaints", EngagementSchema.Name);
        b.Property(x => x.ComplaintNumber).HasMaxLength(24);
        b.Property(x => x.Category).HasMaxLength(64);
        b.Property(x => x.Description).HasMaxLength(4000);
        b.Property(x => x.ContactName).HasMaxLength(200);
        b.Property(x => x.ContactPhone).HasMaxLength(20);
        b.Property(x => x.Resolution).HasMaxLength(2000);
        b.Ignore(x => x.TurnaroundTime);
        b.HasIndex(x => x.ComplaintNumber).IsUnique();
        b.HasIndex(x => new { x.FacilityId, x.Status, x.DueAt });
        b.HasIndex(x => x.PatientId).HasFilter("patient_id IS NOT NULL");
    }
}

internal sealed class PatientSpendEntryConfiguration : IEntityTypeConfiguration<PatientSpendEntry>
{
    public void Configure(EntityTypeBuilder<PatientSpendEntry> b)
    {
        b.ToTable("patient_spend_entries", EngagementSchema.Name);
        b.HasKey(x => x.Id);
        b.Property(x => x.Reference).HasMaxLength(40);
        b.HasIndex(x => new { x.Kind, x.SourceId }).IsUnique();
        b.HasIndex(x => new { x.PatientId, x.OccurredAt });
    }
}
