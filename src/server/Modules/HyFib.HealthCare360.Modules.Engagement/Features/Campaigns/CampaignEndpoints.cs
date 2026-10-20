using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Campaigns;

/// <summary>
/// /api/v1/crm/campaigns: consent-only SMS/WhatsApp campaigns (CRM-03). Drafting and audience preview need
/// crm.campaigns.manage. Scheduling, holding and cancelling need crm.campaigns.send, so a hospital can split the
/// author from the approver.
/// </summary>
internal static class CampaignEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/crm/campaigns").WithTags("CRM");
        string[] viewers = [EngagementPermissions.CampaignsManage, EngagementPermissions.CampaignsSend];

        group.MapGet("/", List).RequireAnyPermission(viewers);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(viewers);
        group.MapGet("/{id:guid}/delivery", Delivery).RequireAnyPermission(viewers);
        group.MapPost("/audience-preview", async (SegmentInput segment, CampaignChannel? channel, CampaignService campaigns, CancellationToken ct) =>
                (await campaigns.PreviewAsync(segment.ToSegment(), ct, channel)).ToOk())
            .RequirePermission(EngagementPermissions.CampaignsManage).WithValidation<SegmentInput>();
        group.MapPost("/", async (SaveCampaignRequest r, CampaignService campaigns, CancellationToken ct) =>
                (await campaigns.CreateAsync(r, ct)).ToCreated(id => $"/api/v1/crm/campaigns/{id}"))
            .RequirePermission(EngagementPermissions.CampaignsManage).WithValidation<SaveCampaignRequest>();
        group.MapPut("/{id:guid}", async (Guid id, SaveCampaignRequest r, CampaignService campaigns, CancellationToken ct) =>
                (await campaigns.UpdateAsync(id, r, ct)).ToNoContent())
            .RequirePermission(EngagementPermissions.CampaignsManage).WithValidation<SaveCampaignRequest>();
        group.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleCampaignRequest r, CampaignService campaigns, CancellationToken ct) =>
                (await campaigns.ScheduleAsync(id, r, ct)).ToNoContent())
            .RequirePermission(EngagementPermissions.CampaignsSend);
        group.MapPost("/{id:guid}/unschedule", async (Guid id, CampaignService campaigns, CancellationToken ct) =>
                (await campaigns.UnscheduleAsync(id, ct)).ToNoContent())
            .RequirePermission(EngagementPermissions.CampaignsSend);
        group.MapPost("/{id:guid}/cancel", async (Guid id, CancelCampaignRequest r, CampaignService campaigns, CancellationToken ct) =>
                (await campaigns.CancelAsync(id, r.Reason, ct)).ToNoContent())
            .RequirePermission(EngagementPermissions.CampaignsSend).WithValidation<CancelCampaignRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        CampaignStatus? status,
        CampaignChannel? channel,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var campaigns = db.Set<Campaign>().AsNoTracking();
        if (status is not null)
        {
            campaigns = campaigns.Where(c => c.Status == status);
        }

        if (channel is not null)
        {
            campaigns = campaigns.Where(c => c.Channel == channel);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            campaigns = campaigns.Where(c => EF.Functions.ILike(c.Name, like, QueryExtensions.LikeEscape));
        }

        return TypedResults.Ok(await campaigns
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new CampaignListItem(c.Id, c.Name, c.Channel, c.Status, c.ScheduledAt, c.AudienceCount, c.QueuedCount, c.SkippedCount, c.CreatedAt))
            .ToPagedResultAsync(page, ct));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var c = await db.Set<Campaign>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return c is null
            ? Error.NotFound(nameof(Campaign), id).ToProblem()
            : TypedResults.Ok(new CampaignDetail(
                c.Id, c.Name, c.Channel, c.TemplateKey, c.Message, c.Status,
                new SegmentInput(c.SegmentMinAge, c.SegmentMaxAge, c.SegmentGender, c.SegmentCity, c.SegmentLastVisitFrom, c.SegmentLastVisitTo, c.SegmentDepartmentIds),
                c.ScheduledAt, c.StartedAt, c.CompletedAt, c.CancelReason, c.AudienceCount, c.QueuedCount, c.SkippedCount, c.Version));
    }

    /// <summary>Recipients are not facility-scoped, so the campaign is checked first (facility filter) before counting.</summary>
    private static async Task<IResult> Delivery(Guid id, HospitalDbContext db, CampaignService campaigns, CancellationToken ct) =>
        await db.Set<Campaign>().AnyAsync(c => c.Id == id, ct)
            ? TypedResults.Ok(await campaigns.DeliveryAsync(id, ct))
            : Error.NotFound(nameof(Campaign), id).ToProblem();
}
