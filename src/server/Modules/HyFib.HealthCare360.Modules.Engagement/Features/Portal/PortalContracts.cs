using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Feedback;
using HyFib.HealthCare360.Modules.Engagement.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Portal;

public sealed record PortalFeedbackForm(Guid Id, string Title, string Hospital, string? IntroText, IReadOnlyList<FeedbackAspect> Aspects, bool IncludesNps, bool AllowsComment);

/// <summary>Feedback from the patient portal (PP-05) for a patient the signed-in user may act for.</summary>
public sealed record PortalFeedbackRequest(
    Guid PatientId,
    Guid FormId,
    Guid? VisitId,
    RatingsInput? Ratings,
    int? Nps,
    string? Comment,
    bool WantsCallback,
    string? ContactPhone)
{
    public FeedbackSubmission ToSubmission() => new(Ratings?.ToRatings() ?? FeedbackRatings.None, Nps, Comment, ContactPhone, WantsCallback);
}

public sealed record PortalFeedbackItem(
    Guid Id,
    DateTimeOffset SubmittedAt,
    string FormTitle,
    decimal? AverageRating,
    int? Nps,
    string? ComplaintNumber,
    ComplaintStatus? ComplaintStatus);

/// <summary>Communication choices (PP-06): campaigns and offers, and service messages such as reminders and feedback links.</summary>
/// <param name="WhatsApp">Get messages on WhatsApp on the patient's number; null leaves it as it is.</param>
/// <summary>
/// The choices the patient just made; a choice left out (null) stays as it is. Each switch saves at once and sends only
/// itself, so a page showing an older state never undoes a choice made meanwhile on another device (REL-04).
/// </summary>
public sealed record CommunicationPreferencesRequest(Guid PatientId, bool? Marketing = null, bool? Updates = null, bool? WhatsApp = null);

public sealed class PortalFeedbackRequestValidator : AbstractValidator<PortalFeedbackRequest>
{
    public PortalFeedbackRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.FormId).NotEmpty();
        RuleFor(x => x.Ratings!).SetValidator(new RatingsInputValidator()).When(x => x.Ratings is not null);
        RuleFor(x => x.Nps).InclusiveBetween(0, 10);
        RuleFor(x => x.Comment).SafeText(2000);
        RuleFor(x => x.ContactPhone).PhoneNumber();
    }
}

public sealed class CommunicationPreferencesRequestValidator : AbstractValidator<CommunicationPreferencesRequest>
{
    public CommunicationPreferencesRequestValidator() => RuleFor(x => x.PatientId).NotEmpty();
}
