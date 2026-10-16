using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Feedback;

/// <summary>
/// Anonymous endpoints behind links and QR codes, under the "public" rate limit:
/// <list type="bullet">
/// <item>QR or kiosk forms (FB-01), where the token identifies the form only.</item>
/// <item>Personal feedback links (FB-02), which are single use and expire after 14 days.</item>
/// <item>Campaign opt-out links (CRM-03).</item>
/// </list>
/// Sending feedback also asks for a proof of work (SRS SEC-06): the QR poster's form is open to anyone who has seen the
/// poster, and a script could otherwise fill the complaint queue. Opting out does not: its link is personal and doing
/// it twice changes nothing.
/// Responses never include anything about a patient (no name, UHID or number). Unknown tokens get one generic 404, so
/// the endpoints cannot be used to probe records. GET never changes anything, which matters because SMS apps and
/// link scanners pre-fetch links; the change happens on POST.
/// </summary>
internal static class PublicFeedbackEndpoints
{
    private const string InvalidLink = "This link is not valid.";

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/public").WithTags("Public").AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Public);

        // The tokens are the patients' links: logs record the route template instead of the path.
        group.MapGet("/feedback/forms/{token}", GetForm).WithSensitivePath();
        group.MapPost("/feedback/forms/{token}", SubmitToForm)
            .RequireProofOfWork(AntiAutomationActions.Feedback)
            .WithValidation<SubmitFeedbackRequest>()
            .WithSensitivePath();
        group.MapGet("/feedback/invitations/{token}", GetInvitation).WithSensitivePath();
        group.MapPost("/feedback/invitations/{token}", SubmitInvitation)
            .RequireProofOfWork(AntiAutomationActions.Feedback)
            .WithValidation<SubmitFeedbackRequest>()
            .WithSensitivePath();
        group.MapGet("/unsubscribe/{token}", GetOptOut).WithSensitivePath();
        group.MapPost("/unsubscribe/{token}", OptOut).WithSensitivePath();
    }

    private static async Task<IResult> GetForm(string token, HospitalDbContext db, CancellationToken ct)
    {
        var form = await FindFormAsync(db, token, ct);
        return form is null ? NotFound() : TypedResults.Ok(await ToPublicAsync(db, form, ct));
    }

    /// <summary>QR poster (default) or kiosk (<c>?kiosk=true</c>): anonymous, linked to the form's department only.</summary>
    private static async Task<IResult> SubmitToForm(string token, bool? kiosk, SubmitFeedbackRequest request, HospitalDbContext db, FeedbackService feedback, CancellationToken ct)
    {
        var form = await FindFormAsync(db, token, ct);
        if (form is null)
        {
            return NotFound();
        }

        var channel = kiosk == true ? FeedbackChannel.Kiosk : FeedbackChannel.QrCode;
        var outcome = await feedback.SubmitAsync(form, request.ToSubmission(), new FeedbackContext(channel, null, null, null, null, null), ct);
        return outcome.ToHttpResult(o => TypedResults.Ok(Receipt(o, request.WantsCallback)));
    }

    private static async Task<IResult> GetInvitation(string token, HospitalDbContext db, TimeProvider time, CancellationToken ct)
    {
        var invitation = await FindInvitationAsync(db, token, ct);
        if (invitation is null)
        {
            return NotFound();
        }

        var usable = CheckUsable(invitation, time.GetUtcNow());
        if (usable.IsFailure)
        {
            return usable.Error!.ToProblem();
        }

        var form = await db.Set<FeedbackForm>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == invitation.FormId, ct);
        return form is null ? NotFound() : TypedResults.Ok(await ToPublicAsync(db, form, ct));
    }

    /// <summary>
    /// Personal link: the response is linked to the patient and stay the link was issued for. The link is used up in
    /// the same transaction, and a unique index stops a double submit.
    /// </summary>
    private static async Task<IResult> SubmitInvitation(
        string token,
        SubmitFeedbackRequest request,
        HospitalDbContext db,
        FeedbackService feedback,
        TimeProvider time,
        CancellationToken ct)
    {
        var invitation = await FindInvitationAsync(db, token, ct, tracked: true);
        if (invitation is null)
        {
            return NotFound();
        }

        var now = time.GetUtcNow();
        var usable = CheckUsable(invitation, now);
        if (usable.IsFailure)
        {
            return usable.Error!.ToProblem();
        }

        var form = await db.Set<FeedbackForm>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == invitation.FormId, ct);
        if (form is null)
        {
            return NotFound();
        }

        var submission = request.ToSubmission();
        var check = FeedbackService.ValidateAgainstForm(form, submission);
        if (check.IsFailure)
        {
            return check.Error!.ToProblem();
        }

        // The visit's department routes any complaint to the team that treated the patient.
        var departmentId = invitation.VisitId is { } visitId
            ? await db.Set<Visit>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(v => v.Id == visitId).Select(v => v.DepartmentId).FirstOrDefaultAsync(ct)
            : null;

        invitation.UsedAt = now;
        var outcome = await feedback.SubmitAsync(form, submission,
            new FeedbackContext(FeedbackChannel.SmsLink, invitation.PatientId, invitation.VisitId, invitation.AdmissionId, departmentId, invitation.Id), ct);
        return outcome.ToHttpResult(o => TypedResults.Ok(Receipt(o, request.WantsCallback)));
    }

    private static async Task<IResult> GetOptOut(string token, HospitalDbContext db, CancellationToken ct)
    {
        if (!OpaqueToken.IsWellFormed(token))
        {
            return NotFound();
        }

        var hash = OpaqueToken.Hash(token);
        var recipient = await db.Set<CampaignRecipient>().AsNoTracking().Where(r => r.OptOutTokenHash == hash)
            .Select(r => new { r.OptedOutAt })
            .FirstOrDefaultAsync(ct);
        return recipient is null ? NotFound() : TypedResults.Ok(new { alreadyOptedOut = recipient.OptedOutAt is not null });
    }

    /// <summary>Withdraws marketing consent (DPDP: as easy as giving it); repeating the request is harmless.</summary>
    private static async Task<IResult> OptOut(string token, HospitalDbContext db, CommunicationConsentService consents, TimeProvider time, CancellationToken ct)
    {
        if (!OpaqueToken.IsWellFormed(token))
        {
            return NotFound();
        }

        var hash = OpaqueToken.Hash(token);
        var recipient = await db.Set<CampaignRecipient>().FirstOrDefaultAsync(r => r.OptOutTokenHash == hash, ct);
        if (recipient is null)
        {
            return NotFound();
        }

        if (recipient.OptedOutAt is not null)
        {
            return TypedResults.NoContent();
        }

        recipient.OptedOutAt = time.GetUtcNow();
        return (await consents.OptOutAsync(recipient.PatientId, "Unsubscribe link in a campaign message", ct)).ToNoContent();
    }

    private static async Task<FeedbackForm?> FindFormAsync(HospitalDbContext db, string token, CancellationToken ct) =>
        OpaqueToken.IsWellFormed(token)
            ? await db.Set<FeedbackForm>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
                .FirstOrDefaultAsync(f => f.PublicToken == token && f.IsActive, ct)
            : null;

    private static async Task<FeedbackInvitation?> FindInvitationAsync(HospitalDbContext db, string token, CancellationToken ct, bool tracked = false)
    {
        if (!OpaqueToken.IsWellFormed(token))
        {
            return null;
        }

        var hash = OpaqueToken.Hash(token);
        var invitations = db.Set<FeedbackInvitation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]);
        return await (tracked ? invitations : invitations.AsNoTracking()).FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
    }

    private static Result CheckUsable(FeedbackInvitation invitation, DateTimeOffset now) =>
        invitation.UsedAt is not null ? Error.Rule("feedback.link_used", "Thank you — feedback has already been received for this link.")
        : invitation.IsUsable(now) ? Result.Success()
        : Error.Rule("feedback.link_expired", "This feedback link has expired.");

    private static async Task<PublicFeedbackForm> ToPublicAsync(HospitalDbContext db, FeedbackForm form, CancellationToken ct)
    {
        var hospital = await db.Set<Facility>().AsNoTracking().Where(f => f.Id == form.FacilityId).Select(f => f.Name).FirstOrDefaultAsync(ct);
        return new PublicFeedbackForm(form.Title, form.IntroText, form.AspectList, form.IncludesNps, form.AllowsComment, hospital ?? "Hospital");
    }

    /// <summary>The complaint number is shown only to someone who left a number for a call-back (it is also sent by SMS).</summary>
    private static FeedbackReceipt Receipt(FeedbackOutcome outcome, bool wantsCallback) =>
        new(outcome.ThankYou, wantsCallback ? outcome.ComplaintNumber : null);

    private static IResult NotFound() => TypedResults.Problem(InvalidLink, statusCode: StatusCodes.Status404NotFound);
}
