using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Platform.Identity;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Leads;

/// <summary>/api/v1/crm/leads: enquiries worked by the CRM team until converted or lost (CRM-01).</summary>
internal static class LeadEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/crm/leads").WithTags("CRM").RequirePermission(EngagementPermissions.LeadsManage);

        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapPost("/", async (SaveLeadRequest r, LeadService leads, CancellationToken ct) =>
                (await leads.CreateAsync(r, ct)).ToCreated(l => $"/api/v1/crm/leads/{l.Id}"))
            .WithValidation<SaveLeadRequest>();
        group.MapPut("/{id:guid}", async (Guid id, SaveLeadRequest r, LeadService leads, CancellationToken ct) => (await leads.UpdateAsync(id, r, ct)).ToNoContent())
            .WithValidation<SaveLeadRequest>();
        group.MapPost("/{id:guid}/activities", async (Guid id, AddLeadActivityRequest r, LeadService leads, CancellationToken ct) =>
                (await leads.AddActivityAsync(id, r, ct)).ToCreated(activityId => $"/api/v1/crm/leads/{id}"))
            .WithValidation<AddLeadActivityRequest>();
        group.MapPost("/{id:guid}/status", async (Guid id, ChangeLeadStatusRequest r, LeadService leads, CancellationToken ct) =>
                (await leads.ChangeStatusAsync(id, r, ct)).ToNoContent())
            .WithValidation<ChangeLeadStatusRequest>();
        group.MapPost("/{id:guid}/convert", Convert).WithValidation<ConvertLeadRequest>();
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        LeadStatus? status,
        LeadSource? source,
        Guid? ownerUserId,
        bool? mine,
        bool? dueOnly,
        bool? includeClosed,
        HospitalDbContext db,
        ICurrentUser user,
        TimeProvider time,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var now = time.GetUtcNow();
        var leads = db.Set<Lead>().AsNoTracking();
        if (status is not null)
        {
            leads = leads.Where(l => l.Status == status);
        }
        else if (includeClosed != true)
        {
            leads = leads.Where(l => l.Status == LeadStatus.New || l.Status == LeadStatus.Contacted || l.Status == LeadStatus.Qualified);
        }

        if (source is not null)
        {
            leads = leads.Where(l => l.Source == source);
        }

        var owner = mine == true ? user.UserId : ownerUserId;
        if (owner is not null)
        {
            leads = leads.Where(l => l.OwnerUserId == owner);
        }

        if (dueOnly == true)
        {
            leads = leads.Where(l => l.NextFollowUpAt != null && l.NextFollowUpAt <= now);
        }

        if (page.SearchTerm is { } term)
        {
            var like = term.ToLikePattern();
            var number = term.ToUpperInvariant();
            var digits = new string(term.Where(char.IsAsciiDigit).ToArray());
            leads = leads.Where(l => l.LeadNumber == number || EF.Functions.ILike(l.Name, like, QueryExtensions.LikeEscape)
                                     || (digits.Length >= 4 && l.Phone != null && l.Phone.EndsWith(digits)));
        }

        // Due call-backs first, then the newest enquiries.
        var result = await leads
            .OrderBy(l => l.NextFollowUpAt == null).ThenBy(l => l.NextFollowUpAt).ThenByDescending(l => l.CreatedAt)
            .Select(l => new
            {
                l.Id,
                l.LeadNumber,
                l.Name,
                l.Phone,
                l.Source,
                l.Status,
                l.InterestPackage,
                l.InterestDepartmentId,
                l.OwnerUserId,
                l.NextFollowUpAt,
                l.LastContactedAt,
                l.CreatedAt,
            })
            .ToPagedResultAsync(page, ct);

        return TypedResults.Ok(result.Map(l => new LeadListItem(
            l.Id, l.LeadNumber, l.Name, l.Phone is null ? null : Phone.Mask(l.Phone), l.Source, l.Status, l.InterestPackage, l.InterestDepartmentId,
            l.OwnerUserId, l.NextFollowUpAt, l.LastContactedAt, l.CreatedAt, l.NextFollowUpAt is { } due && due <= now)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, CancellationToken ct)
    {
        var l = await db.Set<Lead>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return Error.NotFound(nameof(Lead), id).ToProblem();
        }

        var activities = await db.Set<LeadActivity>().AsNoTracking()
            .Where(a => a.LeadId == id)
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => new LeadActivityDto(a.Id, a.Type, a.OccurredAt, a.Outcome, a.Notes, a.PerformedByUserId))
            .ToListAsync(ct);

        return TypedResults.Ok(new LeadDetail(
            l.Id, l.LeadNumber, l.Name, l.Phone, l.Email, l.Gender, l.AgeYears, l.City, l.Source, l.SourceDetail, l.CampaignId, l.ReferringDoctorId,
            l.InterestDepartmentId, l.InterestPackage, l.Notes, l.Status, l.LostReason, l.OwnerUserId, l.NextFollowUpAt, l.LastContactedAt,
            l.ConvertedPatientId, l.ConvertedAppointmentId, l.ConvertedAt, activities, l.Version));
    }

    /// <summary>
    /// Registering a new patient during conversion also needs the patient-registration permission. This prevents the
    /// CRM screen from becoming a side door around Patients' access rules.
    /// </summary>
    private static async Task<IResult> Convert(
        Guid id,
        ConvertLeadRequest request,
        LeadService leads,
        ICurrentUser user,
        IPermissionService permissions,
        CancellationToken ct)
    {
        if (request.RegisterPatient is not null
            && (user.UserId is not { } userId || !await permissions.HasPermissionAsync(userId, PatientsPermissions.Register, ct)))
        {
            return Error.Forbidden("lead.register_denied", "You need the patient-registration permission to register a new patient.").ToProblem();
        }

        return (await leads.ConvertAsync(id, request, ct)).ToOk();
    }
}
