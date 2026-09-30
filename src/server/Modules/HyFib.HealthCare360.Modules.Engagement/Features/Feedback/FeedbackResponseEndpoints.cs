using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Feedback;

/// <summary>
/// /api/v1/feedback: responses, staff-captured feedback, personal feedback links and analytics (FB-01..04). Anonymous
/// responses never carry a patient. Opening a linked response is audited as patient-record access.
/// </summary>
internal static class FeedbackResponseEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/feedback").WithTags("Feedback");

        group.MapGet("/responses", List).RequirePermission(EngagementPermissions.FeedbackRead);
        group.MapGet("/responses/{id:guid}", Get).RequirePermission(EngagementPermissions.FeedbackRead);
        group.MapPost("/responses", CaptureByStaff)
            .RequireAnyPermission(EngagementPermissions.FeedbackManage, EngagementPermissions.FollowUpsManage).WithValidation<StaffFeedbackRequest>();
        group.MapPost("/invitations", SendLink)
            .RequireAnyPermission(EngagementPermissions.FeedbackManage, EngagementPermissions.FollowUpsManage).WithValidation<SendFeedbackLinkRequest>();
        group.MapGet("/analytics", Analytics).RequirePermission(EngagementPermissions.FeedbackRead);
    }

    private static async Task<IResult> List(
        [AsParameters] PageQuery query,
        Guid? formId,
        Guid? departmentId,
        Guid? patientId,
        FeedbackChannel? channel,
        Sentiment? sentiment,
        bool? lowOnly,
        DateOnly? from,
        DateOnly? to,
        HospitalDbContext db,
        CancellationToken ct)
    {
        var page = query.ToRequest();
        var responses = db.Set<FeedbackResponse>().AsNoTracking();
        if (formId is not null)
        {
            responses = responses.Where(r => r.FormId == formId);
        }

        if (departmentId is not null)
        {
            responses = responses.Where(r => r.DepartmentId == departmentId);
        }

        if (patientId is not null)
        {
            responses = responses.Where(r => r.PatientId == patientId);
        }

        if (channel is not null)
        {
            responses = responses.Where(r => r.Channel == channel);
        }

        if (sentiment is not null)
        {
            responses = responses.Where(r => r.Sentiment == sentiment);
        }

        if (lowOnly == true)
        {
            responses = responses.Where(r => r.IsLowScore);
        }

        if (from is { } start)
        {
            var since = EngagementClock.StartOfDayUtc(start);
            responses = responses.Where(r => r.SubmittedAt >= since);
        }

        if (to is { } end)
        {
            var until = EngagementClock.StartOfDayUtc(end.AddDays(1));
            responses = responses.Where(r => r.SubmittedAt < until);
        }

        var result = await (
                from r in responses
                join f in db.Set<FeedbackForm>() on r.FormId equals f.Id
                join c in db.Set<Complaint>() on r.ComplaintId equals c.Id into complaints
                from c in complaints.DefaultIfEmpty()
                orderby r.SubmittedAt descending
                select new { Response = r, FormTitle = f.Title, ComplaintNumber = c != null ? c.ComplaintNumber : null })
            .ToPagedResultAsync(page, ct);

        return TypedResults.Ok(result.Map(x => new FeedbackResponseListItem(
            x.Response.Id, x.Response.SubmittedAt, x.Response.FormId, x.FormTitle, x.Response.Channel, x.Response.DepartmentId, x.Response.Ratings.Average,
            x.Response.Nps, x.Response.Sentiment, x.Response.IsLowScore, x.Response.Comment is not null, x.Response.PatientId is null, x.ComplaintNumber)));
    }

    private static async Task<IResult> Get(Guid id, HospitalDbContext db, IAuditLogger audit, CancellationToken ct)
    {
        var row = await (
                from r in db.Set<FeedbackResponse>().AsNoTracking()
                join f in db.Set<FeedbackForm>() on r.FormId equals f.Id
                join c in db.Set<Complaint>() on r.ComplaintId equals c.Id into complaints
                from c in complaints.DefaultIfEmpty()
                join p in db.Set<Patient>() on r.PatientId equals p.Id into patients
                from p in patients.DefaultIfEmpty()
                where r.Id == id
                select new
                {
                    Response = r,
                    FormTitle = f.Title,
                    ComplaintNumber = c != null ? c.ComplaintNumber : null,
                    Uhid = p != null ? p.Uhid : null,
                    PatientName = p != null ? p.FullName : null,
                })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return Error.NotFound(nameof(FeedbackResponse), id).ToProblem();
        }

        var response = row.Response;
        if (response.PatientId is { } patientId)
        {
            await audit.PatientRecordAccessedAsync(patientId, "Viewed patient feedback", ct);
        }

        return TypedResults.Ok(new FeedbackResponseDetail(
            response.Id, response.FormId, row.FormTitle, response.Channel, response.SubmittedAt, response.PatientId, row.Uhid, row.PatientName,
            response.VisitId, response.AdmissionId, response.DepartmentId, RatingsInput.From(response.Ratings), response.Nps, response.Comment,
            response.Sentiment, response.SentimentScore, response.IsLowScore, response.WantsCallback, response.ContactPhone, response.ComplaintId,
            row.ComplaintNumber));
    }

    private static async Task<IResult> CaptureByStaff(StaffFeedbackRequest request, HospitalDbContext db, FeedbackService feedback, CancellationToken ct)
    {
        var form = await db.Set<FeedbackForm>().AsNoTracking().FirstOrDefaultAsync(f => f.Id == request.FormId && f.IsActive, ct);
        if (form is null)
        {
            return Error.Field("formId", "Choose an active feedback form of this facility.").ToProblem();
        }

        if (!await db.Set<Patient>().AnyAsync(p => p.Id == request.PatientId && p.IsActive, ct))
        {
            return Error.Field("patientId", "Choose an active registered patient.").ToProblem();
        }

        Guid? departmentId = null;
        if (request.VisitId is { } visitId)
        {
            var visit = await db.Set<Visit>().AsNoTracking().Where(v => v.Id == visitId && v.PatientId == request.PatientId)
                .Select(v => new { v.DepartmentId })
                .FirstOrDefaultAsync(ct);
            if (visit is null)
            {
                return Error.Field("visitId", "Choose one of this patient's visits at this facility.").ToProblem();
            }

            departmentId = visit.DepartmentId;
        }

        var outcome = await feedback.SubmitAsync(form, request.ToSubmission(),
            new FeedbackContext(FeedbackChannel.Staff, request.PatientId, request.VisitId, null, departmentId, null), ct);
        return outcome.ToCreated(o => $"/api/v1/feedback/responses/{o.ResponseId}");
    }

    private static async Task<IResult> SendLink(
        SendFeedbackLinkRequest request,
        HospitalDbContext db,
        FeedbackInvitationService invitations,
        ICurrentUser user,
        CancellationToken ct)
    {
        if (user.FacilityId is not { } facilityId)
        {
            return Error.Rule("facility.required", "Select a facility first.").ToProblem();
        }

        var visit = await db.Set<Visit>().AsNoTracking().Where(v => v.Id == request.VisitId && v.PatientId == request.PatientId)
            .Select(v => new { v.Type })
            .FirstOrDefaultAsync(ct);
        if (visit is null)
        {
            return Error.Field("visitId", "Choose one of this patient's visits at this facility.").ToProblem();
        }

        var type = visit.Type == VisitType.InPatient ? FeedbackFormType.IP : FeedbackFormType.OP;
        var invited = await invitations.InviteAsync(
            new FeedbackInviteRequest(facilityId, request.PatientId, request.VisitId, null, type, request.FormId), ct);
        if (invited.IsFailure)
        {
            return invited.Error!.ToProblem();
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Accepted((string?)null, new { invitationId = invited.Value });
    }

    private static async Task<IResult> Analytics(
        DateOnly? from,
        DateOnly? to,
        Guid? formId,
        Guid? departmentId,
        HospitalDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var resolved = ReportPeriod.Resolve(from, to, time.Today());
        if (resolved.IsFailure)
        {
            return resolved.Error!.ToProblem();
        }

        var period = resolved.Value;
        var since = period.SinceUtc;
        var until = period.UntilUtc;
        var responses = db.Set<FeedbackResponse>().AsNoTracking().Where(r => r.SubmittedAt >= since && r.SubmittedAt < until);
        if (formId is not null)
        {
            responses = responses.Where(r => r.FormId == formId);
        }

        if (departmentId is not null)
        {
            responses = responses.Where(r => r.DepartmentId == departmentId);
        }

        var points = (await responses
                .Select(r => new
                {
                    r.SubmittedAt,
                    r.Channel,
                    r.DepartmentId,
                    r.DoctorRating,
                    r.NursingRating,
                    r.FrontDeskRating,
                    r.BillingRating,
                    r.CleanlinessRating,
                    r.FoodRating,
                    r.OverallRating,
                    r.Nps,
                    r.Sentiment,
                    r.IsLowScore,
                })
                .ToListAsync(ct))
            .Select(r => new FeedbackPoint(
                r.SubmittedAt, r.Channel, r.DepartmentId,
                new FeedbackRatings(r.DoctorRating, r.NursingRating, r.FrontDeskRating, r.BillingRating, r.CleanlinessRating, r.FoodRating, r.OverallRating),
                r.Nps, r.Sentiment, r.IsLowScore))
            .ToList();

        var complaints = db.Set<Complaint>().AsNoTracking().Where(c => c.RaisedAt >= since && c.RaisedAt < until);
        if (departmentId is not null)
        {
            complaints = complaints.Where(c => c.DepartmentId == departmentId);
        }

        var complaintPoints = (await complaints.Select(c => new { c.RaisedAt, c.DueAt, c.ResolvedAt, c.Status }).ToListAsync(ct))
            .Select(c => new ComplaintPoint(c.RaisedAt, c.DueAt, c.ResolvedAt, c.Status))
            .ToList();
        var departments = await db.Set<Department>().AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);

        return TypedResults.Ok(FeedbackAnalyticsCalculator.Compute(period, points, complaintPoints, departments, time.GetUtcNow()));
    }
}
