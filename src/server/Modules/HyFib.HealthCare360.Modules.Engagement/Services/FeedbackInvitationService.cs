using System.Linq.Expressions;
using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>Whom to invite, for which stay or visit; <paramref name="FormId"/> overrides the facility's default form of <paramref name="FormType"/>.</summary>
public sealed record FeedbackInviteRequest(Guid FacilityId, Guid PatientId, Guid? VisitId, Guid? AdmissionId, FeedbackFormType FormType, Guid? FormId);

/// <summary>
/// Personal feedback links (FB-02): a single-use link by SMS ("feedback.request") after a discharge or visit. Only
/// patients with an active SMS-updates consent and a mobile number are invited, never deceased or inactive records,
/// and at most once per form and stay or visit. The link carries a random token; only its hash is stored.
/// </summary>
public sealed class FeedbackInvitationService(
    HospitalDbContext db,
    CommunicationConsentService consents,
    PublicLinks links,
    INotificationService notifications,
    TimeProvider time)
{
    public const string RequestTemplate = "feedback.request";

    /// <summary>Adds the invitation and queues the SMS in the caller's unit of work; the caller saves.</summary>
    public async Task<Result<Guid>> InviteAsync(FeedbackInviteRequest request, CancellationToken ct)
    {
        if (request.VisitId is null && request.AdmissionId is null)
        {
            return Error.Field("visitId", "Choose the visit or admission the feedback is about.");
        }

        var forms = db.Set<FeedbackForm>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(f => f.FacilityId == request.FacilityId && f.IsActive);
        var form = request.FormId is { } formId
            ? await forms.FirstOrDefaultAsync(f => f.Id == formId, ct)
            : await forms.Where(f => f.Type == request.FormType).OrderBy(f => f.CreatedAt).FirstOrDefaultAsync(ct);
        if (form is null)
        {
            return Error.Rule("feedback.no_form", $"There is no active {request.FormType} feedback form for this facility.");
        }

        var patient = await db.Set<Patient>().AsNoTracking().Where(p => p.Id == request.PatientId)
            .Select(p => new { p.Id, p.FullName, p.Phone, p.PreferredLanguage, p.IsDeceased, p.IsActive })
            .FirstOrDefaultAsync(ct);
        if (patient is null)
        {
            return Error.NotFound(nameof(Patient), request.PatientId);
        }

        if (patient.IsDeceased || !patient.IsActive)
        {
            return Error.Rule("feedback.patient_inactive", "Feedback links are not sent for this patient record.");
        }

        if (patient.Phone is null)
        {
            return Error.Rule("feedback.no_phone", "The patient has no mobile number on record.");
        }

        if (!await consents.HasTransactionalConsentAsync(patient.Id, ct))
        {
            return Error.Rule("feedback.no_consent", "The patient has not agreed to SMS updates.");
        }

        var sameStay = SameStay(form.Id, patient.Id, request.VisitId, request.AdmissionId);
        if (db.Set<FeedbackInvitation>().Local.Any(sameStay.Compile())
            || await db.Set<FeedbackInvitation>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AnyAsync(sameStay, ct))
        {
            return Error.Conflict("feedback.already_invited", "A feedback link has already been sent for this stay or visit.");
        }

        var baseUrl = await links.BaseUrlAsync(form.FacilityId, ct);
        if (baseUrl is null)
        {
            return Error.Rule("feedback.no_portal_url", $"Set the '{PublicLinks.BaseUrlSetting}' setting to the hospital's https portal address first.");
        }

        var now = time.GetUtcNow();
        var token = OpaqueToken.New();
        var invitation = new FeedbackInvitation
        {
            FacilityId = form.FacilityId,
            FormId = form.Id,
            PatientId = patient.Id,
            VisitId = request.VisitId,
            AdmissionId = request.AdmissionId,
            TokenHash = OpaqueToken.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + FeedbackInvitation.Validity,
        };
        db.Add(invitation);

        notifications.Queue(
            RequestTemplate,
            new NotificationRecipient(patient.Phone, null, patient.Id, patient.FullName, CampaignService.LanguageCode(patient.PreferredLanguage)),
            new Dictionary<string, string> { ["link"] = PublicLinks.Feedback(baseUrl, token) },
            [NotificationChannel.Sms],
            TimeSpan.FromDays(3),
            form.FacilityId);
        return invitation.Id;
    }

    private static Expression<Func<FeedbackInvitation, bool>> SameStay(Guid formId, Guid patientId, Guid? visitId, Guid? admissionId) =>
        admissionId is { } admission
            ? i => i.FormId == formId && i.PatientId == patientId && i.AdmissionId == admission
            : i => i.FormId == formId && i.PatientId == patientId && i.VisitId == visitId;
}
