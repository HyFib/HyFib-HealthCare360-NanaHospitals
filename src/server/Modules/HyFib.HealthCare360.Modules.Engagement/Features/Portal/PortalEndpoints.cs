using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Portal;

/// <summary>
/// Patient Portal (PP-05/06): feedback and communication preferences. Every call is limited to the patients the
/// signed-in mobile number may act for (<see cref="PortalPatientScope"/>). Anyone else's id gets a 403.
/// </summary>
internal static class PortalEndpoints
{
    private const string Via = "patient portal";

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/portal").WithTags("Patient Portal").RequirePermission(PatientsPermissions.Portal)
            .RequireFeature(FeatureKeys.PatientPortal);

        group.MapGet("/feedback/forms", Forms);
        group.MapGet("/feedback", MyFeedback);
        group.MapPost("/feedback", Submit).WithValidation<PortalFeedbackRequest>();
        group.MapGet("/communication-preferences", async (Guid patientId, PortalPatientScope scope, CommunicationConsentService consents, CancellationToken ct) =>
        {
            var access = await scope.EnsureAccessAsync(patientId, ct);
            return access.IsFailure
                ? access.Error!.ToProblem()
                : TypedResults.Ok(await consents.PreferencesAsync(patientId, await scope.AccountPhoneAsync(ct), ct));
        });
        group.MapPut("/communication-preferences", async (CommunicationPreferencesRequest r, PortalPatientScope scope, CommunicationConsentService consents, CancellationToken ct) =>
        {
            var access = await scope.EnsureAccessAsync(r.PatientId, ct);
            return access.IsFailure
                ? access.Error!.ToProblem()
                : (await consents.SetPreferencesAsync(r.PatientId, r.Marketing, r.Updates, Via, ct, r.WhatsApp, await scope.AccountPhoneAsync(ct))).ToNoContent();
        }).WithValidation<CommunicationPreferencesRequest>();
    }

    /// <summary>Active portal forms of every facility (the portal is group-wide); the patient picks where they were seen.</summary>
    private static async Task<IResult> Forms(HospitalDbContext db, CancellationToken ct)
    {
        var forms = await (
                from f in db.Set<FeedbackForm>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                join h in db.Set<Facility>() on f.FacilityId equals h.Id
                where f.IsActive && f.Type == FeedbackFormType.Portal && h.IsActive
                orderby h.Name, f.Title
                select new { Form = f, Hospital = h.Name })
            .ToListAsync(ct);
        return TypedResults.Ok(forms.Select(x => new PortalFeedbackForm(
            x.Form.Id, x.Form.Title, x.Hospital, x.Form.IntroText, x.Form.AspectList, x.Form.IncludesNps, x.Form.AllowsComment)).ToList());
    }

    private static async Task<IResult> MyFeedback(Guid patientId, PortalPatientScope scope, HospitalDbContext db, CancellationToken ct)
    {
        var access = await scope.EnsureAccessAsync(patientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var rows = await (
                from r in db.Set<FeedbackResponse>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                join f in db.Set<FeedbackForm>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on r.FormId equals f.Id
                join c in db.Set<Complaint>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on r.ComplaintId equals c.Id into complaints
                from c in complaints.DefaultIfEmpty()
                where r.PatientId == patientId
                orderby r.SubmittedAt descending
                select new { Response = r, FormTitle = f.Title, ComplaintNumber = c != null ? c.ComplaintNumber : null, ComplaintStatus = c != null ? (ComplaintStatus?)c.Status : null })
            .Take(100)
            .ToListAsync(ct);
        return TypedResults.Ok(rows.Select(x => new PortalFeedbackItem(
            x.Response.Id, x.Response.SubmittedAt, x.FormTitle, x.Response.Ratings.Average, x.Response.Nps, x.ComplaintNumber, x.ComplaintStatus)).ToList());
    }

    private static async Task<IResult> Submit(PortalFeedbackRequest request, PortalPatientScope scope, HospitalDbContext db, FeedbackService feedback, CancellationToken ct)
    {
        var access = await scope.EnsureAccessAsync(request.PatientId, ct);
        if (access.IsFailure)
        {
            return access.Error!.ToProblem();
        }

        var form = await db.Set<FeedbackForm>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == request.FormId && f.IsActive && (f.Type == FeedbackFormType.Portal || f.Type == FeedbackFormType.General), ct);
        if (form is null)
        {
            return Error.Field("formId", "Choose an available feedback form.").ToProblem();
        }

        Guid? departmentId = null;
        if (request.VisitId is { } visitId)
        {
            var visit = await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .Where(v => v.Id == visitId && v.PatientId == request.PatientId && v.FacilityId == form.FacilityId)
                .Select(v => new { v.DepartmentId })
                .FirstOrDefaultAsync(ct);
            if (visit is null)
            {
                return Error.Field("visitId", "Choose one of your visits at this hospital.").ToProblem();
            }

            departmentId = visit.DepartmentId;
        }

        var outcome = await feedback.SubmitAsync(form, request.ToSubmission(),
            new FeedbackContext(FeedbackChannel.Portal, request.PatientId, request.VisitId, null, departmentId, null), ct);
        return outcome.ToHttpResult(o => TypedResults.Ok(new { o.ResponseId, o.ThankYou, o.ComplaintNumber }));
    }
}
