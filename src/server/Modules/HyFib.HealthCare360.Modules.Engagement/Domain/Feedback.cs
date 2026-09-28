namespace HyFib.HealthCare360.Modules.Engagement.Domain;

public enum FeedbackFormType
{
    OP = 0,
    IP,
    Portal,
    General,
}

public enum FeedbackAspect
{
    Doctor = 0,
    Nursing,
    FrontDesk,
    Billing,
    Cleanliness,
    Food,
    Overall,
}

public enum FeedbackChannel
{
    Kiosk = 0,
    QrCode,
    Portal,
    SmsLink,
    Staff,
}

public enum Sentiment
{
    Negative = 0,
    Neutral,
    Positive,
}

/// <summary>
/// A feedback questionnaire (FB-01/02) placed at a point of care. Its public token goes into the QR code poster or
/// kiosk URL: it identifies only the form, never a patient.
/// </summary>
public sealed class FeedbackForm : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public FeedbackFormType Type { get; set; }

    /// <summary>Rated aspects (names of <see cref="FeedbackAspect"/>), each 1–5.</summary>
    public List<string> Aspects { get; set; } = [];

    public bool IncludesNps { get; set; } = true;
    public bool AllowsComment { get; set; } = true;
    public string? IntroText { get; set; }
    public string? ThankYouText { get; set; }

    /// <summary>Department the QR code is placed in (complaints from low scores are routed there).</summary>
    public Guid? DepartmentId { get; set; }

    public string PublicToken { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public IReadOnlyList<FeedbackAspect> AspectList =>
        Aspects.Select(a => Enum.TryParse<FeedbackAspect>(a, out var aspect) ? aspect : (FeedbackAspect?)null)
            .Where(a => a is not null)
            .Select(a => a!.Value)
            .Distinct()
            .ToList();
}

/// <summary>1–5 ratings per aspect; null where not asked or not answered.</summary>
public sealed record FeedbackRatings(int? Doctor, int? Nursing, int? FrontDesk, int? Billing, int? Cleanliness, int? Food, int? Overall)
{
    public static FeedbackRatings None { get; } = new(null, null, null, null, null, null, null);

    public int? For(FeedbackAspect aspect) => aspect switch
    {
        FeedbackAspect.Doctor => Doctor,
        FeedbackAspect.Nursing => Nursing,
        FeedbackAspect.FrontDesk => FrontDesk,
        FeedbackAspect.Billing => Billing,
        FeedbackAspect.Cleanliness => Cleanliness,
        FeedbackAspect.Food => Food,
        _ => Overall,
    };

    public IReadOnlyList<(FeedbackAspect Aspect, int Rating)> Given =>
        Enum.GetValues<FeedbackAspect>().Where(a => For(a) is not null).Select(a => (a, For(a)!.Value)).ToList();

    public decimal? Average => Given.Count == 0 ? null : Math.Round((decimal)Given.Average(g => g.Rating), 2, MidpointRounding.AwayFromZero);
}

/// <summary>A submitted feedback (FB-01..03), anonymous unless it came through the portal, a personal SMS link or staff.</summary>
public sealed class FeedbackResponse : AuditableEntity, IFacilityScoped
{
    public Guid FacilityId { get; set; }
    public Guid FormId { get; set; }
    public Guid? PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public Guid? DepartmentId { get; set; }
    public FeedbackChannel Channel { get; set; }
    public int? DoctorRating { get; set; }
    public int? NursingRating { get; set; }
    public int? FrontDeskRating { get; set; }
    public int? BillingRating { get; set; }
    public int? CleanlinessRating { get; set; }
    public int? FoodRating { get; set; }
    public int? OverallRating { get; set; }

    /// <summary>Net promoter score answer, 0–10.</summary>
    public int? Nps { get; set; }

    public string? Comment { get; set; }
    public Sentiment Sentiment { get; set; }
    public int SentimentScore { get; set; }
    public bool IsLowScore { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }

    /// <summary>Number the patient left for a call-back (only when they asked for one).</summary>
    public string? ContactPhone { get; set; }

    public bool WantsCallback { get; set; }
    public Guid? ComplaintId { get; set; }
    public Guid? InvitationId { get; set; }

    public FeedbackRatings Ratings => new(DoctorRating, NursingRating, FrontDeskRating, BillingRating, CleanlinessRating, FoodRating, OverallRating);

    public void SetRatings(FeedbackRatings ratings)
    {
        DoctorRating = ratings.Doctor;
        NursingRating = ratings.Nursing;
        FrontDeskRating = ratings.FrontDesk;
        BillingRating = ratings.Billing;
        CleanlinessRating = ratings.Cleanliness;
        FoodRating = ratings.Food;
        OverallRating = ratings.Overall;
    }
}

/// <summary>
/// A single-use personal feedback link sent after discharge (FB-02). Only the SHA-256 hash of the token is stored,
/// so a database reader cannot forge links; the link reveals nothing about the patient.
/// </summary>
[NotAudited]
public sealed class FeedbackInvitation : Entity, IFacilityScoped
{
    public static readonly TimeSpan Validity = TimeSpan.FromDays(14);

    public Guid FacilityId { get; set; }
    public Guid FormId { get; set; }
    public Guid PatientId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid? AdmissionId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }

    public bool IsUsable(DateTimeOffset now) => UsedAt is null && now < ExpiresAt;
}
