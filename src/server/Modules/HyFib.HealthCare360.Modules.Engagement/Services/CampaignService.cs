using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Campaigns;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Localization;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>
/// Marketing campaigns (CRM-03): drafting against consented segments, scheduling (with template and opt-out checks),
/// cancelling, batch sending for the dispatcher and delivery tracking.
/// <para>
/// A campaign is scheduled, and so sent, only when someone other than its author approves it (MAKER-CHECKER.md, slice 14):
/// asking to schedule it leaves it a draft, waiting for a holder of <see cref="EngagementPermissions.CampaignsApprove"/> who
/// neither wrote nor last edited it, nor asked.
/// </para>
/// </summary>
public sealed class CampaignService(HospitalDbContext db, ICurrentUser currentUser, PublicLinks links, IApprovals approvals, IAuditLogger audit, TimeProvider time)
{
    /// <summary>A bulk campaign asked to be scheduled, waiting for someone other than its author.</summary>
    public const string ScheduleKind = "engagement.campaign_schedule";

    /// <summary>Every campaign template must offer an opt-out link (TRAI/DPDP), provided as {{optout}}.</summary>
    public const string OptOutPlaceholder = "{{optout}}";

    /// <param name="channel">A WhatsApp campaign reaches only numbers that opted in to WhatsApp; null counts every channel's audience.</param>
    public IQueryable<Patient> Audience(Guid facilityId, CampaignSegment segment, CampaignChannel? channel = null)
    {
        var audience = CampaignAudienceQuery.Build(
            db.Set<Patient>(),
            db.Set<PatientConsent>(),
            db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]),
            segment,
            facilityId,
            time.Today());
        return channel == CampaignChannel.WhatsApp
            ? CampaignAudienceQuery.ReachableOnWhatsApp(audience, db.Set<WhatsAppOptIn>(), db.Set<WhatsAppOptOut>())
            : audience;
    }

    /// <summary>Audience size and a small masked sample, so planners can sanity-check a segment without seeing a list of patients.</summary>
    /// <param name="channel">For a WhatsApp campaign, only numbers that opted in to WhatsApp count.</param>
    public async Task<Result<AudiencePreview>> PreviewAsync(CampaignSegment segment, CancellationToken ct, CampaignChannel? channel = null)
    {
        if (currentUser.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.");
        }

        var audience = Audience(facilityId, segment, channel);
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

    /// <summary>
    /// Asks for the campaign to be scheduled: the checks of scheduling are made now (template, opt-out link, portal address,
    /// a time to come, a consenting audience), and then it waits for someone other than its author to approve; it stays a
    /// draft, and nothing is sent, until then. Editing the draft meanwhile makes the request stale.
    /// </summary>
    public async Task<Result<ApprovalRequestDto>> ScheduleAsync(Guid id, ScheduleCampaignRequest request, CancellationToken ct)
    {
        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
        {
            return Error.NotFound(nameof(Campaign), id);
        }

        var ready = await ReadyAsync(campaign, request.ScheduledAt, ct);
        if (ready.IsFailure)
        {
            return ready.Error!;
        }

        var audience = ready.Value;
        var authors = await AuthorNamesAsync(campaign, ct);
        List<ApprovalDetail> details =
        [
            new("Campaign", $"{campaign.Name} ({campaign.Channel})"),
            new("Message", Fit(campaign.Message, 500)),
            new("Template", campaign.TemplateKey),
            new("Audience", $"{audience:N0} patients who agreed to messages (counted now; consent is checked again as each is sent)"),
            new("Send at", LocalTime(request.ScheduledAt)),
            new("Status", "Draft → scheduled"),
            new("Written by", authors),
            new("Why it waits", "A bulk campaign is sent only when someone other than its author approves it"),
        ];
        return await approvals.RequestAsync(
            ScheduleKind,
            new ApprovalSubject(nameof(Campaign), campaign.Id, campaign.Version, campaign.FacilityId, null),
            new CampaignSchedulePayload(campaign.Id, request.ScheduledAt),
            Fit($"Send campaign \"{campaign.Name}\" by {campaign.Channel} to {audience:N0} patients at {LocalTime(request.ScheduledAt)}", 300),
            details,
            $"Schedule {campaign.Name} for {LocalTime(request.ScheduledAt)}",
            ct);
    }

    /// <summary>
    /// Schedules a campaign whose scheduling was approved, in the approval's transaction (the approval saves): only the
    /// draft as it was asked (<c>approval.stale</c>), by someone who neither wrote nor last edited it
    /// (<c>campaign.author_approves</c>, which leaves it waiting), with the checks of scheduling made again.
    /// </summary>
    internal async Task<Result<string>> ScheduleApprovedAsync(ApprovalRequest request, CancellationToken ct)
    {
        var asked = ApprovalPayload.Read<CampaignSchedulePayload>(request.Payload);
        var campaign = await db.Set<Campaign>().FirstOrDefaultAsync(c => c.Id == asked.CampaignId, ct);
        if (campaign is null)
        {
            return Error.NotFound(nameof(Campaign), asked.CampaignId);
        }

        if (currentUser.UserId is { } checker && (campaign.CreatedBy == checker || campaign.UpdatedBy == checker))
        {
            return Error.Forbidden("campaign.author_approves", "You wrote or last edited this campaign, so someone else approves it.");
        }

        if (campaign.Status != CampaignStatus.Draft || (request.SubjectVersion is { } version && campaign.Version != version))
        {
            return Error.Rule("approval.stale", $"Campaign \"{campaign.Name}\" was changed after this was asked for, so it was not scheduled. Ask again.");
        }

        if (asked.ScheduledAt < time.GetUtcNow().AddMinutes(-1))
        {
            return Error.Rule("approval.stale", $"The time asked for ({LocalTime(asked.ScheduledAt)}) has passed, so the campaign was not scheduled. Ask again with a new time.");
        }

        var ready = await ReadyAsync(campaign, asked.ScheduledAt, ct);
        if (ready.IsFailure)
        {
            return ready.Error!;
        }

        var scheduled = campaign.Schedule(asked.ScheduledAt, ready.Value, request.RequestedBy, time.GetUtcNow());
        if (scheduled.IsFailure)
        {
            return scheduled.Error!;
        }

        var approver = currentUser.DisplayName ?? currentUser.UserName ?? "Unknown user";
        audit.Add(new AuditEntry(AuditCategories.Data, "crm.campaign.scheduled",
            Fit($"Campaign \"{campaign.Name}\" scheduled for {LocalTime(asked.ScheduledAt)} to {ready.Value:N0} patients (asked for by {request.RequestedByName}, approved by {approver})", 1000))
        {
            EntityType = nameof(Campaign),
            EntityId = campaign.Id.ToString(),
            Severity = AuditSeverity.Warning,
            Details = new { campaign.Name, asked.ScheduledAt, audience = ready.Value, approval = request.Id, requestedBy = request.RequestedBy, approvedBy = currentUser.UserId },
        });
        return $"Campaign scheduled for {LocalTime(asked.ScheduledAt)}";
    }

    /// <summary>When the campaigns among these were asked to be scheduled and still wait.</summary>
    public Task<IReadOnlyDictionary<Guid, DateTimeOffset>> ScheduleAskedAsync(IReadOnlyCollection<Guid> campaignIds, CancellationToken ct) =>
        approvals.WaitingSinceAsync(ScheduleKind, nameof(Campaign), campaignIds, ct);

    /// <summary>The checks of scheduling; the audience it would reach now.</summary>
    private async Task<Result<int>> ReadyAsync(Campaign campaign, DateTimeOffset at, CancellationToken ct)
    {
        if (campaign.Status != CampaignStatus.Draft)
        {
            return Error.Rule("campaign.not_draft", "Only a draft campaign can be scheduled.");
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

        if (at < time.GetUtcNow().AddMinutes(-1))
        {
            return Error.Field("scheduledAt", "Choose a time in the future.");
        }

        var audience = await Audience(campaign.FacilityId, campaign.Segment, campaign.Channel).CountAsync(ct);
        return audience <= 0 ? Error.Rule("campaign.empty_audience", "No consenting patients match this segment.") : audience;
    }

    private async Task<string> AuthorNamesAsync(Campaign campaign, CancellationToken ct)
    {
        var ids = new[] { campaign.CreatedBy, campaign.UpdatedBy }.OfType<Guid>().Distinct().ToList();
        var names = await db.Set<User>().AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var created = campaign.CreatedBy is { } c ? names.GetValueOrDefault(c) ?? "Unknown user" : "Unknown user";
        return campaign.UpdatedBy is { } u && u != campaign.CreatedBy
            ? $"{created}, last edited by {names.GetValueOrDefault(u) ?? "Unknown user"}"
            : created;
    }

    private static string LocalTime(DateTimeOffset instant) =>
        instant.ToOffset(TimeSpan.FromMinutes(330)).ToString("dd MMM yyyy HH:mm 'IST'", System.Globalization.CultureInfo.InvariantCulture);

    private static string Fit(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

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
            rows.Count(r => r.Status == CampaignRecipientStatus.Skipped || r.LogStatus is NotificationStatus.Skipped or NotificationStatus.Suppressed),
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
        var batch = await Audience(campaign.FacilityId, campaign.Segment, campaign.Channel)
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

    /// <summary>
    /// Notification language from the patient's free-text preference ("Hindi" → hi, "Tamil" or "தமிழ்" → ta); English
    /// when it names none HC360 writes.
    /// </summary>
    public static string LanguageCode(string? preferredLanguage) => PatientLanguages.FromPreference(preferredLanguage) ?? PatientLanguages.Default;

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

/// <summary>What was asked: schedule this campaign for this time.</summary>
public sealed record CampaignSchedulePayload(Guid CampaignId, DateTimeOffset ScheduledAt);

/// <summary>A bulk campaign scheduled: someone who may send campaigns asks, someone other than its author approves.</summary>
internal sealed class CampaignScheduleApproval(CampaignService campaigns) : IApprovalAction
{
    public string Kind => CampaignService.ScheduleKind;

    public string Title => "Bulk campaign scheduled";

    public string ApprovePermission => EngagementPermissions.CampaignsApprove;

    public Task<Result<string>> ApplyAsync(ApprovalRequest request, CancellationToken ct) => campaigns.ScheduleApprovedAsync(request, ct);
}
