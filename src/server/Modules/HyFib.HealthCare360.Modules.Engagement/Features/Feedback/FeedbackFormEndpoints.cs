using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Feedback;

/// <summary>
/// /api/v1/feedback/forms: questionnaires and their QR codes (FB-01/02). Rotating a form's token retires the old QR
/// posters, for example after a poster has been misused.
/// </summary>
internal static class FeedbackFormEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/feedback/forms").WithTags("Feedback");

        group.MapGet("/", List).RequireAnyPermission(EngagementPermissions.FeedbackRead, EngagementPermissions.FeedbackManage);
        group.MapGet("/{id:guid}", Get).RequireAnyPermission(EngagementPermissions.FeedbackRead, EngagementPermissions.FeedbackManage);
        group.MapPost("/", Create).RequirePermission(EngagementPermissions.FeedbackManage).WithValidation<SaveFeedbackFormRequest>();
        group.MapPut("/{id:guid}", Update).RequirePermission(EngagementPermissions.FeedbackManage).WithValidation<SaveFeedbackFormRequest>().RequireVersion();
        group.MapPost("/{id:guid}/rotate-token", RotateToken).RequirePermission(EngagementPermissions.FeedbackManage);
    }

    private static async Task<IResult> List(bool? activeOnly, FeedbackFormType? type, HospitalDbContext db, PublicLinks links, ICurrentUser user, CancellationToken ct)
    {
        var forms = db.Set<FeedbackForm>().AsNoTracking();
        if (activeOnly == true)
        {
            forms = forms.Where(f => f.IsActive);
        }

        if (type is not null)
        {
            forms = forms.Where(f => f.Type == type);
        }

        var baseUrl = await links.BaseUrlAsync(user.FacilityId, ct);
        var rows = await forms.OrderBy(f => f.Code).ToListAsync(ct);
        return TypedResults.Ok(rows.Select(f => ToDto(f, baseUrl)).ToList());
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, PublicLinks links, CancellationToken ct)
    {
        var form = await db.Set<FeedbackForm>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
        return form is null
            ? Error.NotFound(nameof(FeedbackForm), id).ToProblem()
            : TypedResults.Ok(ToDto(form, await links.BaseUrlAsync(form.FacilityId, ct)));
    }

    private static async Task<IResult> Create(SaveFeedbackFormRequest request, HospitalDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var problem = await CheckAsync(db, request, null, ct);
        if (problem is not null)
        {
            return problem.ToProblem();
        }

        var form = new FeedbackForm { FacilityId = facilityId, PublicToken = OpaqueToken.New() };
        Apply(form, request);
        db.Add(form);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/v1/feedback/forms/{form.Id}", form.Id);
    }

    private static async Task<IResult> Update(Guid id, SaveFeedbackFormRequest request, HospitalDbContext db, CancellationToken ct)
    {
        var form = await db.Set<FeedbackForm>().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (form is null)
        {
            return Error.NotFound(nameof(FeedbackForm), id).ToProblem();
        }

        var problem = await CheckAsync(db, request, id, ct);
        if (problem is not null)
        {
            return problem.ToProblem();
        }

        if (request.Version is { } version)
        {
            db.SetExpectedVersion(form, version);
        }

        Apply(form, request);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> RotateToken(Guid id, HospitalDbContext db, PublicLinks links, CancellationToken ct)
    {
        var form = await db.Set<FeedbackForm>().FirstOrDefaultAsync(f => f.Id == id, ct);
        if (form is null)
        {
            return Error.NotFound(nameof(FeedbackForm), id).ToProblem();
        }

        form.PublicToken = OpaqueToken.New();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(form, await links.BaseUrlAsync(form.FacilityId, ct)));
    }

    private static async Task<Error?> CheckAsync(HospitalDbContext db, SaveFeedbackFormRequest request, Guid? exceptId, CancellationToken ct)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.Set<FeedbackForm>().AnyAsync(f => f.Code == code && f.Id != exceptId, ct))
        {
            return Error.Conflict("feedback.form_code_taken", $"A feedback form with code {code} already exists.");
        }

        return request.DepartmentId is { } departmentId && !await db.Set<Department>().AnyAsync(d => d.Id == departmentId && d.IsActive, ct)
            ? Error.Field("departmentId", "Choose an active department.")
            : null;
    }

    private static void Apply(FeedbackForm form, SaveFeedbackFormRequest request)
    {
        form.Code = request.Code.Trim().ToUpperInvariant();
        form.Title = request.Title.Trim();
        form.Type = request.Type;
        form.Aspects = (request.Aspects ?? []).Distinct().Select(a => a.ToString()).ToList();
        form.IncludesNps = request.IncludesNps;
        form.AllowsComment = request.AllowsComment;
        form.IntroText = string.IsNullOrWhiteSpace(request.IntroText) ? null : request.IntroText.Trim();
        form.ThankYouText = string.IsNullOrWhiteSpace(request.ThankYouText) ? null : request.ThankYouText.Trim();
        form.DepartmentId = request.DepartmentId;
        form.IsActive = request.IsActive;
    }

    internal static FeedbackFormDto ToDto(FeedbackForm f, string? baseUrl) =>
        new(f.Id, f.Code, f.Title, f.Type, f.AspectList, f.IncludesNps, f.AllowsComment, f.IntroText, f.ThankYouText, f.DepartmentId, f.PublicToken,
            baseUrl is null ? null : PublicLinks.FeedbackForm(baseUrl, f.PublicToken), f.IsActive, f.Version);
}
