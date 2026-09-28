using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Engagement.Services;

/// <summary>What the patient answered.</summary>
public sealed record FeedbackSubmission(FeedbackRatings Ratings, int? Nps, string? Comment, string? ContactPhone, bool WantsCallback);

/// <summary>Where the feedback came from and whom it concerns (all optional for anonymous QR/kiosk feedback).</summary>
public sealed record FeedbackContext(FeedbackChannel Channel, Guid? PatientId, Guid? VisitId, Guid? AdmissionId, Guid? DepartmentId, Guid? InvitationId);

public sealed record FeedbackOutcome(Guid ResponseId, bool IsLowScore, Sentiment Sentiment, string? ComplaintNumber, string ThankYou);

/// <summary>
/// Records feedback from every channel (FB-01..03): checks answers against the form, scores sentiment, flags low scores
/// and turns them into complaints automatically, and acknowledges patients who asked for a call-back.
/// </summary>
public sealed class FeedbackService(HospitalDbContext db, ComplaintService complaints, INotificationService notifications, TimeProvider time)
{
    public const string DefaultThankYou = "Thank you for your feedback. It helps us care for you better.";
    public const string ComplaintAcknowledgedTemplate = "complaint.acknowledged";

    /// <summary>Answers must fit the form: rated aspects only, NPS and comment only when asked, and at least one answer.</summary>
    public static Result ValidateAgainstForm(FeedbackForm form, FeedbackSubmission submission)
    {
        var allowed = form.AspectList.ToHashSet();
        var given = submission.Ratings.Given;
        if (given.Any(g => !allowed.Contains(g.Aspect)))
        {
            return Error.Field("ratings", "Rate only the aspects on this form.");
        }

        if (given.Any(g => g.Rating is < 1 or > 5))
        {
            return Error.Field("ratings", "Ratings are from 1 to 5.");
        }

        if (submission.Nps is { } nps && (!form.IncludesNps || nps is < 0 or > 10))
        {
            return Error.Field("nps", form.IncludesNps ? "The recommendation score is from 0 to 10." : "This form does not ask for a recommendation score.");
        }

        if (!string.IsNullOrWhiteSpace(submission.Comment) && !form.AllowsComment)
        {
            return Error.Field("comment", "This form does not take comments.");
        }

        if (given.Count == 0 && submission.Nps is null && string.IsNullOrWhiteSpace(submission.Comment))
        {
            return Error.Field("ratings", "Answer at least one question.");
        }

        if (submission.WantsCallback && Phone.Normalize(submission.ContactPhone) is null)
        {
            return Error.Field("contactPhone", "Leave a phone number so that we can call you back.");
        }

        return Result.Success();
    }

    public async Task<Result<FeedbackOutcome>> SubmitAsync(FeedbackForm form, FeedbackSubmission submission, FeedbackContext context, CancellationToken ct)
    {
        var check = ValidateAgainstForm(form, submission);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var now = time.GetUtcNow();
        var sentiment = SentimentAnalyzer.Analyze(submission.Comment, submission.Ratings, submission.Nps);
        var isLow = LowScoreRule.IsLow(submission.Ratings, submission.Nps);
        var contactPhone = submission.WantsCallback ? Phone.Normalize(submission.ContactPhone) : null;
        var response = new FeedbackResponse
        {
            FacilityId = form.FacilityId,
            FormId = form.Id,
            PatientId = context.PatientId,
            VisitId = context.VisitId,
            AdmissionId = context.AdmissionId,
            DepartmentId = context.DepartmentId ?? form.DepartmentId,
            Channel = context.Channel,
            Nps = submission.Nps,
            Comment = string.IsNullOrWhiteSpace(submission.Comment) ? null : submission.Comment.Trim(),
            Sentiment = sentiment.Sentiment,
            SentimentScore = sentiment.Score,
            IsLowScore = isLow,
            SubmittedAt = now,
            ContactPhone = contactPhone,
            WantsCallback = submission.WantsCallback,
            InvitationId = context.InvitationId,
        };
        response.SetRatings(submission.Ratings);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Add(response);

        Complaint? complaint = null;
        if (isLow)
        {
            var lowAspects = LowScoreRule.LowAspects(submission.Ratings);
            complaint = await complaints.AddAsync(new Complaint
            {
                FacilityId = form.FacilityId,
                Source = ComplaintSource.Feedback,
                FeedbackResponseId = response.Id,
                PatientId = context.PatientId,
                DepartmentId = response.DepartmentId,
                Category = lowAspects.Count > 0 ? lowAspects[0].ToString() : "Overall",
                Description = Describe(form, submission, lowAspects),
                ContactPhone = contactPhone,
                Priority = LowScoreRule.PriorityFor(submission.Ratings, submission.Nps),
            }, ct);
            response.ComplaintId = complaint.Id;

            if (contactPhone is not null)
            {
                notifications.Queue(ComplaintAcknowledgedTemplate, new NotificationRecipient(contactPhone, null, context.PatientId, "Patient"),
                    new Dictionary<string, string> { ["reference"] = complaint.ComplaintNumber, ["name"] = "Patient" }, [NotificationChannel.Sms], TimeSpan.FromDays(1));
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (complaint is not null)
        {
            await complaints.NotifyNewAsync(complaint, ct);
        }

        return new FeedbackOutcome(response.Id, isLow, sentiment.Sentiment, complaint?.ComplaintNumber,
            string.IsNullOrWhiteSpace(form.ThankYouText) ? DefaultThankYou : form.ThankYouText);
    }

    private static string Describe(FeedbackForm form, FeedbackSubmission submission, IReadOnlyList<FeedbackAspect> lowAspects)
    {
        var parts = new List<string> { $"Low score on \"{form.Title}\"." };
        if (lowAspects.Count > 0)
        {
            parts.Add("Rated low: " + string.Join(", ", lowAspects.Select(a => $"{a} {submission.Ratings.For(a)}/5")) + ".");
        }

        if (submission.Nps is { } nps)
        {
            parts.Add($"Recommendation score {nps}/10.");
        }

        if (!string.IsNullOrWhiteSpace(submission.Comment))
        {
            parts.Add("Comment: " + submission.Comment.Trim());
        }

        var text = string.Join(' ', parts);
        return text.Length <= 4000 ? text : text[..4000];
    }
}
