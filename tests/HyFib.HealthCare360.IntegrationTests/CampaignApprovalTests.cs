using System.Net;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Campaigns;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Bulk campaigns (MAKER-CHECKER.md, slice 14): asking to schedule a campaign leaves it a draft, and nothing is sent, until
/// someone other than its author approves it (<c>crm.campaigns.approve</c>): not the one who asked (<c>approval.self</c>),
/// nor the one who wrote or last edited it (<c>campaign.author_approves</c>). A draft edited while it waits is not scheduled
/// (<c>approval.stale</c>).
/// </summary>
public sealed class CampaignApprovalTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_campaign_is_scheduled_only_when_someone_other_than_its_author_approves()
    {
        var author = await server.SignInAsync(Users.HospitalAdmin);
        await using var setup = await SetUpAsync(author);
        var campaign = new Created(await author.PostAsync<Guid>("/api/v1/crm/campaigns", Draft(setup.Template, "Monsoon health check")));
        var path = $"/api/v1/crm/campaigns/{campaign.Id}";
        var at = DateTimeOffset.UtcNow.AddDays(2);

        var asked = await AskAsync(author, $"{path}/schedule", new ScheduleCampaignRequest(at));
        asked.Kind.ShouldBe("engagement.campaign_schedule");
        asked.Details.ShouldContain(d => d.Label == "Status" && d.Value == "Draft → scheduled");
        var waiting = await author.GetAsync<CampaignDetail>(path);
        (waiting.Status, waiting.ScheduleAskedAt is null).ShouldBe((CampaignStatus.Draft, false));

        using (var own = await author.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Mine", asked.Version)))
        {
            (await own.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.self");
        }

        var checker = await server.SignInAsync(Users.Admin);
        await checker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Message and audience checked.", asked.Version));
        var scheduled = await author.GetAsync<CampaignDetail>(path);
        scheduled.Status.ShouldBe(CampaignStatus.Scheduled);
        scheduled.ScheduledAt.ShouldNotBeNull().ShouldBe(at, TimeSpan.FromSeconds(1));
        scheduled.AudienceCount.ShouldBeGreaterThan(0);
        (await AuditAsync(campaign.Id)).ShouldContain("approved by");
    }

    [Fact]
    public async Task The_campaigns_author_does_not_approve_it_even_when_someone_else_asked()
    {
        var author = await server.SignInAsync(Users.HospitalAdmin);
        await using var setup = await SetUpAsync(author);
        var campaign = new Created(await author.PostAsync<Guid>("/api/v1/crm/campaigns", Draft(setup.Template, "Diabetes camp")));
        var asker = await server.SignInAsync(Users.Admin);
        var asked = await AskAsync(asker, $"/api/v1/crm/campaigns/{campaign.Id}/schedule", new ScheduleCampaignRequest(DateTimeOffset.UtcNow.AddDays(1)));

        using (var authors = await author.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Agreed", asked.Version)))
        {
            authors.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await authors.Content.ReadAsStringAsync(Ct)).ShouldContain("campaign.author_approves");
        }

        (await author.GetAsync<CampaignDetail>($"/api/v1/crm/campaigns/{campaign.Id}")).Status.ShouldBe(CampaignStatus.Draft);
        (await author.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}")).Status.ShouldBe(ApprovalStatus.Pending);
    }

    [Fact]
    public async Task A_draft_edited_while_it_waits_is_not_scheduled()
    {
        var author = await server.SignInAsync(Users.HospitalAdmin);
        await using var setup = await SetUpAsync(author);
        var campaign = new Created(await author.PostAsync<Guid>("/api/v1/crm/campaigns", Draft(setup.Template, "Eye camp")));
        var path = $"/api/v1/crm/campaigns/{campaign.Id}";
        var asked = await AskAsync(author, $"{path}/schedule", new ScheduleCampaignRequest(DateTimeOffset.UtcNow.AddDays(1)));
        var draft = await author.GetAsync<CampaignDetail>(path);
        await author.PutAsync(path, Draft(setup.Template, "Eye camp") with { Message = "Free eye check-up this Sunday, all day.", Version = draft.Version });

        var checker = await server.SignInAsync(Users.Admin);
        using (var stale = await checker.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest("Checked", asked.Version)))
        {
            (await stale.Content.ReadAsStringAsync(Ct)).ShouldContain("approval.stale");
        }

        (await author.GetAsync<CampaignDetail>(path)).Status.ShouldBe(CampaignStatus.Draft);
    }

    private static SaveCampaignRequest Draft(string template, string name) => new(
        name, CampaignChannel.Sms, template, "Free health check-up at the hospital this Sunday.", new SegmentInput(null, null, null, null, null, null, null), null);

    private static async Task<ApprovalRequestDto> AskAsync(ApiClient maker, string path, object body)
    {
        using var response = await maker.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        return await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, path);
    }

    /// <summary>
    /// What a campaign needs to be scheduled, for this test only: a template with the opt-out link, the portal's address for
    /// the facility, and a patient who agreed to marketing messages.
    /// </summary>
    private async Task<Setup> SetUpAsync(ApiClient admin)
    {
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId;
        var key = $"campaign.test_{Guid.NewGuid():N}"[..40];
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Add(new NotificationTemplate
            {
                Key = key,
                Channel = NotificationChannel.Sms,
                Language = "en",
                Body = "{{message}} Stop: {{optout}}",
                IsActive = true,
            });
            await db.SaveChangesAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().SetAsync(PortalAddress.BaseUrlSetting, "https://portal.nana.example", facilityId, cancellationToken: Ct);
        }

        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Lakshmi", MiddleName: null, LastName: "Campaign" + new string(Enumerable.Range(0, 6).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray()), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1970, 3, 3), AgeYears: null, AgeMonths: null, Phone: phone, AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: true));
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient.Id}/consents",
            new CaptureConsentRequest(ConsentType.MarketingCommunication, null, true, null, null, null, null, null));
        return new Setup(key, async () =>
        {
            await using var cleanup = server.Services.CreateAsyncScope();
            await cleanup.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(PortalAddress.BaseUrlSetting, facilityId, CancellationToken.None);
        });
    }

    private async Task<string> AuditAsync(Guid campaignId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return (await db.Set<AuditEvent>().AsNoTracking().SingleAsync(a => a.EntityId == campaignId.ToString() && a.Action == "crm.campaign.scheduled", Ct)).Summary!;
    }

    private sealed record Setup(string Template, Func<Task> Cleanup) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Cleanup();
    }

    private sealed record Created(Guid Id);
}
