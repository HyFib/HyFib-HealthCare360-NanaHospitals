using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Campaigns;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Marketing campaigns (CRM-03): drafting against consented segments, scheduling (with template and opt-out checks),
/// cancelling, batch sending for the dispatcher and delivery tracking.
/// </summary>
public sealed class CampaignService(HospitalDbContext db, ICurrentUser currentUser, PublicLinks links, TimeProvider time)
{
    /// <summary>Every campaign template must offer an opt-out link (TRAI/DPDP), provided as {{optout}}.</summary>
    public const string OptOutPlaceholder = "{{optout}}";

    public IQueryable<Patient> Audience(Guid facilityId, CampaignSegment segment) =>
        CampaignAudienceQuery.Build(
            db.Set<Patient>(),
            db.Set<PatientConsent>(),
            db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]),
            segment,
            facilityId,
            time.Today());

    /// <summary>Audience size and a small masked sample, so planners can sanity-check a segment without seeing a list of patients.</summary>
    public async Task<Result<AudiencePreview>> PreviewAsync(CampaignSegment segment, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var audience = Audience(facilityId, segment);
        var count = await audience.CountAsync(ct);
        var today = time.Today();
        var sample = await audience.OrderBy(p => p.Id).Take(5)
            .Select(p => new { p.Uhid, p.FirstName, p.LastName, p.DateOfBirth, p.Address.City })
            .ToListAsync(ct);
        return new AudiencePreview(count, sample.Select(p => new AudienceSampleItem(
            p.Uhid,
            string.IsNullOrWhiteSpace(p.LastName) ? p.FirstName : $"{p.FirstName} {p.LastName.Trim()[0]}.",
            p.DateOfBirth is { } dob ? Age.Format(dob, today) : null,
            p.City)).ToList());
    }

    public async Task<Result<Guid>> CreateAsync(SaveCampaignRequest request, CancellationToken ct)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var problem = await ValidateTemplateAsync(request.TemplateKey, request.Channel, facilityId, ct);
        if (problem is not null)
        {
            return problem;
        }

        var campaign = new Campaign { FacilityId = facilityId, Status = CampaignStatus.Draft };
        Apply(campaign, request);
        db.Add(campaign);
        await db.SaveChangesAsync(ct);
        return campaign.Id;
    }

    public async Task<Result> UpdateAsync(Guid id, SaveCampaignRequest request, CancellationToken ct)
    {
        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Error.NotFound(nameof(Campaign), id);
        }

        if (!campaign.IsEditable)
        {
            return Error.Rule("campaign.not_draft", "Only draft campaigns can be edited. Take it back to draft first.");
        }

        var problem = await ValidateTemplateAsync(request.TemplateKey, request.Channel, campaign.FacilityId, ct);
        if (problem is not null)
        {
            return problem;
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(campaign, version);
        }

        Apply(campaign, request);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ScheduleAsync(Guid id, ScheduleCampaignRequest request, CancellationToken ct)
    {
        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Error.NotFound(nameof(Campaign), id);
        }

        var problem = await ValidateTemplateAsync(campaign.TemplateKey, campaign.Channel, campaign.FacilityId, ct);
        if (problem is not null)
        {
            return problem;
        }

        if (await links.BaseUrlAsync(campaign.FacilityId, ct) is null)
        {
            return Error.Rule("campaign.no_portal_url",
                $"Set the '{PublicLinks.BaseUrlSetting}' setting to the hospital's public https address first; every message must carry an opt-out link.");
        }

        var audience = await Audience(campaign.FacilityId, campaign.Segment).CountAsync(ct);
        var result = campaign.Schedule(request.ScheduledAt, audience, currentUser.UserId, time.GetUtcNow());
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    public Task<Result> UnscheduleAsync(Guid id, CancellationToken ct) => ChangeAsync(id, c => c.Unschedule(), ct);

    public Task<Result> CancelAsync(Guid id, string reason, CancellationToken ct) => ChangeAsync(id, c => c.Cancel(reason, time.GetUtcNow()), ct);

    public async Task<CampaignDelivery> DeliveryAsync(Guid id, CancellationToken ct)
    {
        var rows = await (
                from r in db.Set<CampaignRecipient>().AsNoTracking()
                join l in db.Set<NotificationLog>() on r.NotificationLogId equals l.Id into logs
                from l in logs.DefaultIfEmpty()
                where r.CampaignId == id
                select new { r.Status, LogStatus = l != null ? (NotificationStatus?)l.Status : null, r.OptedOutAt })
            .ToListAsync(ct);

        return new CampaignDelivery(
            rows.Count,
            rows.Count(r => r.LogStatus == NotificationStatus.Queued),
            rows.Count(r => r.LogStatus is NotificationStatus.Sent or NotificationStatus.Delivered),
            rows.Count(r => r.LogStatus == NotificationStatus.Failed),
            rows.Count(r => r.Status == CampaignRecipientStatus.Skipped || r.LogStatus == NotificationStatus.Skipped),
            rows.Count(r => r.OptedOutAt is not null));
    }

    /// <summary>
    /// Queues the next batch of messages for a sending campaign. Consent is re-evaluated now (not at scheduling), each
    /// patient gets a personal opt-out link, and each message is linked to its notification log for delivery tracking.
    /// Returns the number of patients processed (0 = the campaign is done).
    /// </summary>
    public async Task<int> SendBatchAsync(Campaign campaign, INotificationService notifications, string baseUrl, int batchSize, CancellationToken ct)
    {
        var processed = db.Set<CampaignRecipient>().Where(r => r.CampaignId == campaign.Id).Select(r => r.PatientId);
        var batch = await Audience(campaign.FacilityId, campaign.Segment)
            .Where(p => !processed.Contains(p.Id))
            .OrderBy(p => p.Id)
            .Take(batchSize)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.PreferredLanguage })
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var channel = campaign.Channel == CampaignChannel.WhatsApp ? NotificationChannel.WhatsApp : NotificationChannel.Sms;
        foreach (var patient in batch)
        {
            var token = OpaqueToken.New();
            var logIds = notifications.Queue(
                campaign.TemplateKey,
                new NotificationRecipient(patient.Phone, null, patient.Id, patient.FullName, LanguageCode(patient.PreferredLanguage)),
                new Dictionary<string, string> { ["message"] = campaign.Message, ["optout"] = PublicLinks.OptOut(baseUrl, token) },
                [channel],
                TimeSpan.FromDays(2),
                campaign.FacilityId);

            db.Add(new CampaignRecipient
            {
                CampaignId = campaign.Id,
                PatientId = patient.Id,
                NotificationLogId = logIds.Count > 0 ? logIds[0] : null,
                Status = CampaignRecipientStatus.Queued,
                OptOutTokenHash = OpaqueToken.Hash(token),
                CreatedAt = now,
            });
        }

        campaign.RecordBatch(batch.Count, 0);
        return batch.Count;
    }

    /// <summary>Notification language from the patient's free-text preference ("Hindi" → hi); English otherwise.</summary>
    public static string LanguageCode(string? preferredLanguage) =>
        preferredLanguage is { } language && (language.StartsWith("hi", StringComparison.OrdinalIgnoreCase) || language.Contains("हिन्दी", StringComparison.Ordinal))
            ? "hi"
            : "en";

    /// <summary>
    /// Messages use this facility's template when it has one, otherwise the all-facility one, in the patient's language
    /// with English as the fallback; so an English version must exist for this facility or all facilities. Every version
    /// a patient could receive must carry the opt-out placeholder.
    /// </summary>
    private async Task<Error?> ValidateTemplateAsync(string templateKey, CampaignChannel channel, Guid facilityId, CancellationToken ct)
    {
        var key = templateKey.Trim();
        var notificationChannel = channel == CampaignChannel.WhatsApp ? NotificationChannel.WhatsApp : NotificationChannel.Sms;
        var templates = await db.Set<NotificationTemplate>().AsNoTracking()
            .Where(t => t.Key == key && t.Channel == notificationChannel && t.IsActive && (t.FacilityId == null || t.FacilityId == facilityId))
            .Select(t => new { t.FacilityId, t.Language, t.Body })
            .ToListAsync(ct);
        if (!templates.Any(t => t.Language == "en"))
        {
            return Error.Field("templateKey",
                $"No active English {channel} template '{key}' for this facility or all facilities. Create it (with its DLT id) under notification templates.");
        }

        return templates.All(t => t.Body.Contains(OptOutPlaceholder, StringComparison.Ordinal))
            ? null
            : Error.Field("templateKey", $"Campaign templates must include an opt-out link placeholder {OptOutPlaceholder}.");
    }

    private static void Apply(Campaign campaign, SaveCampaignRequest request)
    {
        campaign.Name = request.Name.Trim();
        campaign.Channel = request.Channel;
        campaign.TemplateKey = request.TemplateKey.Trim();
        campaign.Message = request.Message.Trim();
        campaign.SetSegment(request.Segment.ToSegment());
    }

    private async Task<Result> ChangeAsync(Guid id, Func<Campaign, Result> change, CancellationToken ct)
    {
        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Error.NotFound(nameof(Campaign), id);
        }

        var result = change(campaign);
        if (result.IsSuccess)
        {
            await db.SaveChangesAsync(ct);
        }

        return result;
    }
}
