using HyFib.HealthCare360.Modules.Engagement.Domain;
using HyFib.HealthCare360.Modules.Engagement.Features.Campaigns;
using HyFib.HealthCare360.Modules.Engagement.Features.Complaints;
using HyFib.HealthCare360.Modules.Engagement.Features.Feedback;
using HyFib.HealthCare360.Modules.Engagement.Features.Leads;
using HyFib.HealthCare360.Modules.Engagement.Features.Referrals;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class ValidatorTests
{
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Time = new FixedTime(new DateTimeOffset(2026, 9, 26, 6, 0, 0, TimeSpan.Zero));

    private static SaveLeadRequest Lead(string name = "Ravi Kumar", string? phone = "9876543210", string? email = null) =>
        new(name, phone, email, Gender.Male, 45, "Chennai", LeadSource.HealthCamp, "Anna Nagar camp", null, null, null, "Master Health Checkup", null, null, null, false, null);

    [Fact]
    public void A_lead_needs_a_name_and_a_way_to_reach_them()
    {
        var validator = new SaveLeadRequestValidator();

        validator.Validate(Lead()).IsValid.ShouldBeTrue();
        validator.Validate(Lead(phone: null, email: "ravi@example.com")).IsValid.ShouldBeTrue();
        validator.Validate(Lead(phone: null)).IsValid.ShouldBeFalse();
        validator.Validate(Lead(name: "Ravi <script>")).IsValid.ShouldBeFalse();
        validator.Validate(Lead(phone: "12345")).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Conversion_needs_exactly_one_patient_choice_and_a_birth_date_or_age_for_new_patients()
    {
        var validator = new ConvertLeadRequestValidator(Time);

        validator.Validate(new ConvertLeadRequest(Guid.NewGuid(), null, null)).IsValid.ShouldBeTrue();
        validator.Validate(new ConvertLeadRequest(null, new NewPatientDetails(Gender.Male, null, 45, false), null)).IsValid.ShouldBeTrue();
        validator.Validate(new ConvertLeadRequest(null, null, null)).IsValid.ShouldBeFalse();
        validator.Validate(new ConvertLeadRequest(Guid.NewGuid(), new NewPatientDetails(Gender.Male, null, 45, false), null)).IsValid.ShouldBeFalse();
        validator.Validate(new ConvertLeadRequest(null, new NewPatientDetails(Gender.Male, null, null, false), null)).IsValid.ShouldBeFalse();
        validator.Validate(new ConvertLeadRequest(null, new NewPatientDetails(Gender.Male, new DateOnly(2027, 1, 1), null, false), null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Campaign_segments_must_be_consistent_and_sms_text_short()
    {
        var validator = new SaveCampaignRequestValidator();
        var segment = new SegmentInput(30, 60, null, "Chennai", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30), null);

        validator.Validate(new SaveCampaignRequest("Heart month", CampaignChannel.Sms, "campaign.message", "Free ECG this week", segment, null)).IsValid.ShouldBeTrue();
        validator.Validate(new SaveCampaignRequest("Heart month", CampaignChannel.Sms, "campaign.message", new string('x', 481), segment, null)).IsValid.ShouldBeFalse();
        validator.Validate(new SaveCampaignRequest("Heart month", CampaignChannel.WhatsApp, "campaign.message", new string('x', 481), segment, null)).IsValid.ShouldBeTrue();
        validator.Validate(new SaveCampaignRequest("Heart month", CampaignChannel.Sms, "campaign.message", "Free ECG", segment with { MinAge = 70 }, null))
            .IsValid.ShouldBeFalse();
        validator.Validate(new SaveCampaignRequest("Heart month", CampaignChannel.Sms, "campaign.message", "Free ECG",
            segment with { LastVisitFrom = new DateOnly(2026, 7, 1) }, null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Public_feedback_is_bounded_and_a_call_back_needs_a_number()
    {
        var validator = new SubmitFeedbackRequestValidator();
        var ratings = new RatingsInput(5, null, null, null, null, null, 4);

        validator.Validate(new SubmitFeedbackRequest(ratings, 9, "Very caring staff", false, null)).IsValid.ShouldBeTrue();
        validator.Validate(new SubmitFeedbackRequest(ratings with { Doctor = 6 }, 9, null, false, null)).IsValid.ShouldBeFalse();
        validator.Validate(new SubmitFeedbackRequest(ratings, 11, null, false, null)).IsValid.ShouldBeFalse();
        validator.Validate(new SubmitFeedbackRequest(ratings, null, null, true, null)).IsValid.ShouldBeFalse();
        validator.Validate(new SubmitFeedbackRequest(ratings, null, "bad\u0007text", false, null)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Feedback_forms_ask_at_least_one_question_once()
    {
        var validator = new SaveFeedbackFormRequestValidator();

        validator.Validate(Form("OPD", [FeedbackAspect.Doctor])).IsValid.ShouldBeTrue();
        validator.Validate(Form("OPD", [FeedbackAspect.Doctor, FeedbackAspect.Doctor])).IsValid.ShouldBeFalse();
        validator.Validate(Form("OPD", [], nps: false, comment: false)).IsValid.ShouldBeFalse();
        validator.Validate(Form("OPD FORM", [FeedbackAspect.Doctor])).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Staff_complaints_are_not_disguised_as_feedback_complaints()
    {
        var validator = new CreateComplaintRequestValidator();

        validator.Validate(Complaint(ComplaintSource.WalkIn)).IsValid.ShouldBeTrue();
        validator.Validate(Complaint(ComplaintSource.Feedback)).IsValid.ShouldBeFalse();
        validator.Validate(Complaint(ComplaintSource.Phone) with { Description = " " }).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Referring_doctors_have_bounded_alternate_names()
    {
        var validator = new SaveReferringDoctorRequestValidator();

        validator.Validate(Doctor(["Dr RK", "R. Kumar"])).IsValid.ShouldBeTrue();
        validator.Validate(Doctor(Enumerable.Range(1, 11).Select(i => $"Name {i}").ToList())).IsValid.ShouldBeFalse();
        validator.Validate(Doctor([""])).IsValid.ShouldBeFalse();
    }

    private static SaveFeedbackFormRequest Form(string code, IReadOnlyList<FeedbackAspect> aspects, bool nps = true, bool comment = true) =>
        new(code, "Out-patient feedback", FeedbackFormType.OP, aspects, nps, comment, null, null, null, true, null);

    private static CreateComplaintRequest Complaint(ComplaintSource source) =>
        new(source, null, null, "Billing", "Waited 90 minutes at the billing counter", "Meena", "9876543210", ComplaintPriority.Normal, null);

    private static SaveReferringDoctorRequest Doctor(IReadOnlyList<string> alternateNames) =>
        new("Dr. Ravi Kumar", "Kumar Clinic", "044-24567890", null, "General Medicine", "Chennai", "TNMC 12345", alternateNames, null, true, null);
}
