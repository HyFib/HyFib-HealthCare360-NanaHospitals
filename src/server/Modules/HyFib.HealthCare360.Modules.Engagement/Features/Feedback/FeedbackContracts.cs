using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Services;

namespace HyFib.HealthCare360.Modules.Engagement.Features.Feedback;

public sealed record FeedbackFormDto(
    Guid Id,
    string Code,
    string Title,
    FeedbackFormType Type,
    IReadOnlyList<FeedbackAspect> Aspects,
    bool IncludesNps,
    bool AllowsComment,
    string? IntroText,
    string? ThankYouText,
    Guid? DepartmentId,
    string PublicToken,
    string? QrUrl,
    bool IsActive,
    uint Version);

public sealed record SaveFeedbackFormRequest(
    string Code,
    string Title,
    FeedbackFormType Type,
    IReadOnlyList<FeedbackAspect>? Aspects,
    bool IncludesNps,
    bool AllowsComment,
    string? IntroText,
    string? ThankYouText,
    Guid? DepartmentId,
    bool IsActive,
    uint? Version);

/// <summary>1–5 per aspect; leave out what was not asked or not answered.</summary>
public sealed record RatingsInput(int? Doctor, int? Nursing, int? FrontDesk, int? Billing, int? Cleanliness, int? Food, int? Overall)
{
    public FeedbackRatings ToRatings() => new(Doctor, Nursing, FrontDesk, Billing, Cleanliness, Food, Overall);

    public static RatingsInput From(FeedbackRatings r) => new(r.Doctor, r.Nursing, r.FrontDesk, r.Billing, r.Cleanliness, r.Food, r.Overall);
}

/// <summary>What a patient answers (QR, kiosk, personal link).</summary>
public sealed record SubmitFeedbackRequest(RatingsInput? Ratings, int? Nps, string? Comment, bool WantsCallback, string? ContactPhone)
{
    public FeedbackSubmission ToSubmission() => new(Ratings?.ToRatings() ?? FeedbackRatings.None, Nps, Comment, ContactPhone, WantsCallback);
}

/// <summary>Feedback taken by staff on the patient's behalf (for example during a follow-up call).</summary>
public sealed record StaffFeedbackRequest(
    Guid FormId,
    Guid PatientId,
    Guid? VisitId,
    RatingsInput? Ratings,
    int? Nps,
    string? Comment,
    bool WantsCallback,
    string? ContactPhone)
{
    public FeedbackSubmission ToSubmission() => new(Ratings?.ToRatings() ?? FeedbackRatings.None, Nps, Comment, ContactPhone, WantsCallback);
}

/// <summary>
/// Sends a personal feedback link for one of the patient's visits at this facility. In-patient stays are invited
/// automatically on discharge.
/// </summary>
public sealed record SendFeedbackLinkRequest(Guid PatientId, Guid VisitId, Guid? FormId);

/// <summary>What a public page may show: the form only, never anything about a patient.</summary>
public sealed record PublicFeedbackForm(string Title, string? IntroText, IReadOnlyList<FeedbackAspect> Aspects, bool IncludesNps, bool AllowsComment, string Hospital);

public sealed record FeedbackReceipt(string ThankYou, string? ComplaintNumber);

public sealed record FeedbackResponseListItem(
    Guid Id,
    DateTimeOffset SubmittedAt,
    Guid FormId,
    string FormTitle,
    FeedbackChannel Channel,
    Guid? DepartmentId,
    decimal? AverageRating,
    int? Nps,
    Sentiment Sentiment,
    bool IsLowScore,
    bool HasComment,
    bool IsAnonymous,
    string? ComplaintNumber);

public sealed record FeedbackResponseDetail(
    Guid Id,
    Guid FormId,
    string FormTitle,
    FeedbackChannel Channel,
    DateTimeOffset SubmittedAt,
    Guid? PatientId,
    string? Uhid,
    string? PatientName,
    Guid? VisitId,
    Guid? AdmissionId,
    Guid? DepartmentId,
    RatingsInput Ratings,
    int? Nps,
    string? Comment,
    Sentiment Sentiment,
    int SentimentScore,
    bool IsLowScore,
    bool WantsCallback,
    string? ContactPhone,
    Guid? ComplaintId,
    string? ComplaintNumber);

internal static class FeedbackRules
{
    public const int MaxAspects = 7;

    public static bool ValidCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) && code.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public sealed class SaveFeedbackFormRequestValidator : AbstractValidator<SaveFeedbackFormRequest>
{
    public SaveFeedbackFormRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32).Must(FeedbackRules.ValidCode).WithMessage("Use letters, digits, '-' or '_'.");
        RuleFor(x => x.Title).NotEmpty().SafeText(150);
        RuleFor(x => x.IntroText).SafeText(500);
        RuleFor(x => x.ThankYouText).SafeText(500);
        RuleFor(x => x.Aspects).Must(a => a is null || (a.Count <= FeedbackRules.MaxAspects && a.Distinct().Count() == a.Count))
            .WithMessage("List each rated aspect once.");
        RuleForEach(x => x.Aspects).IsInEnum();
        RuleFor(x => x).Must(x => (x.Aspects?.Count ?? 0) > 0 || x.IncludesNps || x.AllowsComment).WithName("aspects")
            .WithMessage("Ask at least one question.");
    }
}

public sealed class RatingsInputValidator : AbstractValidator<RatingsInput>
{
    public RatingsInputValidator()
    {
        RuleFor(x => x.Doctor).InclusiveBetween(1, 5);
        RuleFor(x => x.Nursing).InclusiveBetween(1, 5);
        RuleFor(x => x.FrontDesk).InclusiveBetween(1, 5);
        RuleFor(x => x.Billing).InclusiveBetween(1, 5);
        RuleFor(x => x.Cleanliness).InclusiveBetween(1, 5);
        RuleFor(x => x.Food).InclusiveBetween(1, 5);
        RuleFor(x => x.Overall).InclusiveBetween(1, 5);
    }
}

public sealed class SubmitFeedbackRequestValidator : AbstractValidator<SubmitFeedbackRequest>
{
    public SubmitFeedbackRequestValidator()
    {
        RuleFor(x => x.Ratings!).SetValidator(new RatingsInputValidator()).When(x => x.Ratings is not null);
        RuleFor(x => x.Nps).InclusiveBetween(0, 10);
        RuleFor(x => x.Comment).SafeText(2000);
        RuleFor(x => x.ContactPhone).PhoneNumber();
        RuleFor(x => x.ContactPhone).NotEmpty().When(x => x.WantsCallback).WithMessage("Leave a phone number so that we can call you back.");
    }
}

public sealed class StaffFeedbackRequestValidator : AbstractValidator<StaffFeedbackRequest>
{
    public StaffFeedbackRequestValidator()
    {
        RuleFor(x => x.FormId).NotEmpty();
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.Ratings!).SetValidator(new RatingsInputValidator()).When(x => x.Ratings is not null);
        RuleFor(x => x.Nps).InclusiveBetween(0, 10);
        RuleFor(x => x.Comment).SafeText(2000);
        RuleFor(x => x.ContactPhone).PhoneNumber();
    }
}

public sealed class SendFeedbackLinkRequestValidator : AbstractValidator<SendFeedbackLinkRequest>
{
    public SendFeedbackLinkRequestValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.VisitId).NotEmpty().WithMessage("Choose the visit the feedback is about.");
    }
}
