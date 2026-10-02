using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class EngagementModuleTests
{
    [Fact]
    public void Module_uses_its_schema_name_as_key() => new EngagementModule().Name.ShouldBe("engagement");

    [Fact]
    public void Declares_crm_and_feedback_permissions_once()
    {
        var names = new EngagementModule().Permissions.Select(p => p.Name).ToList();

        names.ShouldBe(
        [
            EngagementPermissions.LeadsManage, EngagementPermissions.FollowUpsManage, EngagementPermissions.CampaignsManage,
            EngagementPermissions.CampaignsSend, EngagementPermissions.ReferralsManage, EngagementPermissions.Patient360Read,
            EngagementPermissions.FeedbackRead, EngagementPermissions.FeedbackManage, EngagementPermissions.ComplaintsManage,
            EngagementPermissions.ComplaintEscalations,
        ]);
        names.ShouldBeUnique();
        names.ShouldAllBe(n => n.StartsWith("crm.", StringComparison.Ordinal) || n.StartsWith("feedback.", StringComparison.Ordinal));
    }

    [Fact]
    public void Complaint_alerts_reach_quality_managers_and_crm_executives()
    {
        var complaints = new EngagementModule().Permissions.Single(p => p.Name == EngagementPermissions.ComplaintsManage);

        complaints.DefaultRoles.ShouldBe([SystemRoles.CrmExecutive, SystemRoles.QualityManager, SystemRoles.HospitalAdmin]);
    }

    [Fact]
    public void Overdue_complaints_are_escalated_above_the_team_that_handles_them()
    {
        var escalations = new EngagementModule().Permissions.Single(p => p.Name == EngagementPermissions.ComplaintEscalations);

        escalations.DefaultRoles.ShouldBe([SystemRoles.QualityManager, SystemRoles.HospitalAdmin]);
    }

    [Fact]
    public void Front_office_can_capture_leads_but_patients_get_no_crm_access()
    {
        var permissions = new EngagementModule().Permissions.ToList();

        permissions.Single(p => p.Name == EngagementPermissions.LeadsManage).DefaultRoles!.ShouldContain(SystemRoles.FrontOffice);
        permissions.ShouldAllBe(p => !p.DefaultRoles!.Contains(SystemRoles.Patient));
    }

    [Fact]
    public void Registers_its_services_the_campaign_dispatcher_and_the_follow_up_and_complaint_jobs()
    {
        var services = new ServiceCollection();

        new EngagementModule().AddServices(services, new ConfigurationBuilder().Build());

        services.ShouldContain(s => s.ServiceType == typeof(CommunicationConsentService));
        services.ShouldContain(s => s.ServiceType == typeof(CampaignService));
        services.ShouldContain(s => s.ServiceType == typeof(FeedbackInvitationService));
        services.ShouldContain(s => s.ServiceType == typeof(Patient360Service));
        services.ShouldContain(s => s.ServiceType == typeof(EngagementSettings));
        services.ShouldContain(s => s.ServiceType == typeof(FollowUpReminders));
        services.ShouldContain(s => s.ServiceType == typeof(EngagementMonitor));
        services.ShouldContain(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(CampaignDispatcher));
        services.ShouldContain(s => s.ServiceType == typeof(IHostedService) && s.ImplementationType == typeof(EngagementJobs));
    }
}
