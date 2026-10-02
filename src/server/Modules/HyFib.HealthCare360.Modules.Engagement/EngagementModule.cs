using HyFib.HealthCare360.Modules.Engagement.Features.Campaigns;
using HyFib.HealthCare360.Modules.Engagement.Features.Complaints;
using HyFib.HealthCare360.Modules.Engagement.Features.Feedback;
using HyFib.HealthCare360.Modules.Engagement.Features.FollowUps;
using HyFib.HealthCare360.Modules.Engagement.Features.Leads;
using HyFib.HealthCare360.Modules.Engagement.Features.Patient360;
using HyFib.HealthCare360.Modules.Engagement.Features.Portal;
using HyFib.HealthCare360.Modules.Engagement.Features.Referrals;
using HyFib.HealthCare360.Modules.Engagement.Services;

namespace HyFib.HealthCare360.Modules.Engagement;

public static class EngagementPermissions
{
    public const string LeadsManage = "crm.leads.manage";
    public const string FollowUpsManage = "crm.followups.manage";
    public const string CampaignsManage = "crm.campaigns.manage";

    /// <summary>Separate from drafting so a hospital can have campaigns approved by someone other than the author.</summary>
    public const string CampaignsSend = "crm.campaigns.send";

    public const string ReferralsManage = "crm.referrals.manage";
    public const string Patient360Read = "crm.patient360.read";
    public const string FeedbackRead = "feedback.read";
    public const string FeedbackManage = "feedback.manage";

    /// <summary>Complaint handlers; holders receive the new-complaint alert.</summary>
    public const string ComplaintsManage = "feedback.complaints.manage";

    /// <summary>Those a complaint is escalated to when it passes its deadline unresolved (FB-03); an alert, not access.</summary>
    public const string ComplaintEscalations = "feedback.complaints.escalations";

    private static readonly string[] Crm = [SystemRoles.CrmExecutive, SystemRoles.HospitalAdmin];
    private static readonly string[] Experience = [SystemRoles.CrmExecutive, SystemRoles.QualityManager, SystemRoles.HospitalAdmin];

    internal static IEnumerable<PermissionDefinition> All { get; } =
    [
        new(LeadsManage, "CRM", "Capture and work leads; convert them into patients and appointments", [.. Crm, SystemRoles.FrontOffice]),
        new(FollowUpsManage, "CRM", "Follow-up call worklist: create, assign, record calls and send reminders", Crm),
        new(CampaignsManage, "CRM", "Draft SMS/WhatsApp campaigns, preview consented audiences and view delivery", Crm),
        new(CampaignsSend, "CRM", "Schedule, hold and cancel campaign sending", Crm),
        new(ReferralsManage, "CRM", "Referring doctors and referral analytics", Crm),
        new(Patient360Read, "CRM", "Patient 360° view: visits, spend, feedback, complaints and communication consent", Crm),
        new(FeedbackRead, "Feedback", "View feedback responses and analytics", Experience),
        new(FeedbackManage, "Feedback", "Maintain feedback forms and QR codes, record feedback and send feedback links", Experience),
        new(ComplaintsManage, "Feedback", "Handle patient complaints; receives new-complaint alerts", Experience),
        new(ComplaintEscalations, "Feedback", "Receive the alert when a complaint passes its resolution deadline unresolved",
            [SystemRoles.QualityManager, SystemRoles.HospitalAdmin]),
    ];
}

/// <summary>Healthcare CRM, campaigns, follow-ups, feedback and patient-portal preferences (CRM-01..05, FB-01..04, PP-05/06).</summary>
public sealed class EngagementModule : IModule
{
    public string Name => "engagement";

    public IEnumerable<PermissionDefinition> Permissions => EngagementPermissions.All;

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<PublicLinks>();
        services.AddScoped<CommunicationConsentService>();
        services.AddScoped<PatientFeedbackEraser>();
        services.AddScoped<LeadService>();
        services.AddScoped<EngagementSettings>();
        services.AddScoped<FollowUpReminders>();
        services.AddScoped<FollowUpService>();
        services.AddScoped<ReferralService>();
        services.AddScoped<CampaignService>();
        services.AddScoped<ComplaintService>();
        services.AddScoped<FeedbackService>();
        services.AddScoped<FeedbackInvitationService>();
        services.AddScoped<Patient360Service>();
        services.AddScoped<EngagementMonitor>();

        // Both return immediately on API-only nodes (PlatformOptions.RunsJobs).
        services.AddHostedService<CampaignDispatcher>();
        services.AddHostedService<EngagementJobs>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        LeadEndpoints.Map(endpoints);
        FollowUpEndpoints.Map(endpoints);
        ReferralEndpoints.Map(endpoints);
        CampaignEndpoints.Map(endpoints);
        Patient360Endpoints.Map(endpoints);
        FeedbackFormEndpoints.Map(endpoints);
        FeedbackResponseEndpoints.Map(endpoints);
        ComplaintEndpoints.Map(endpoints);
        PublicFeedbackEndpoints.Map(endpoints);
        PortalEndpoints.Map(endpoints);
    }
}
