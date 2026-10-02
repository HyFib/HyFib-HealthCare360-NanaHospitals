using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

/// <summary>The follow-ups CRM-02 starts by itself: their keys, what settles them, and when the patient is reminded.</summary>
public sealed class FollowUpAutomationTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static FollowUpTask NewTask(FollowUpType type, DateOnly due, FollowUpStatus status = FollowUpStatus.Open) =>
        new() { PatientId = Guid.NewGuid(), Type = type, DueDate = due, Status = status, Source = FollowUpSource.System };

    [Fact]
    public void Each_source_has_its_own_key_and_a_vaccination_key_names_its_dose()
    {
        var id = Guid.NewGuid();
        string[] keys =
        [
            FollowUpAutomation.ConsultationReference(id), FollowUpAutomation.ImmunizationReference(id),
            FollowUpAutomation.ChronicReviewReference(id), FollowUpAutomation.HealthCheckupReference(id),
        ];

        keys.ShouldBeUnique();
        keys.ShouldAllBe(k => k.Length <= 100);
        FollowUpAutomation.ImmunizationOf(FollowUpAutomation.ImmunizationReference(id)).ShouldBe(id);
        FollowUpAutomation.ImmunizationOf(FollowUpAutomation.ConsultationReference(id)).ShouldBeNull();
        FollowUpAutomation.ImmunizationOf("immunization:not-a-dose").ShouldBeNull();
        FollowUpAutomation.ImmunizationOf(null).ShouldBeNull();
    }

    [Theory]
    [InlineData(-31, false)]
    [InlineData(-30, true)]
    [InlineData(0, true)]
    [InlineData(10, true)]
    public void A_visit_or_booking_settles_a_task_from_thirty_days_before_it_is_due(int daysFromDue, bool settles) =>
        FollowUpAutomation.IsAround(Today, Today.AddDays(daysFromDue)).ShouldBe(settles);

    [Theory]
    [InlineData(FollowUpType.PostConsultation, true)]
    [InlineData(FollowUpType.ChronicReview, true)]
    [InlineData(FollowUpType.Vaccination, false)]
    [InlineData(FollowUpType.HealthCheckupDue, false)]
    [InlineData(FollowUpType.PostDischarge, false)]
    public void Any_visit_settles_reviews_but_a_vaccination_needs_its_dose_and_a_check_up_the_check_up(FollowUpType type, bool closes) =>
        FollowUpAutomation.ClosesOnVisit(type).ShouldBe(closes);

    [Fact]
    public void The_chronic_review_follows_its_interval_unless_the_doctor_already_asked_to_see_the_patient_later()
    {
        var review = FollowUpAutomation.ChronicReviewDate(Today, 3);

        review.ShouldBe(new DateOnly(2027, 1, 2));
        FollowUpAutomation.NeedsChronicReview(null, review).ShouldBeTrue();
        FollowUpAutomation.NeedsChronicReview(Today.AddDays(14), review).ShouldBeTrue("A review in two weeks does not end the chronic-care schedule.");
        FollowUpAutomation.NeedsChronicReview(review, review).ShouldBeFalse();
        FollowUpAutomation.NeedsChronicReview(review.AddMonths(3), review).ShouldBeFalse();
    }

    [Fact]
    public void The_next_check_up_keeps_to_the_last_day_of_a_shorter_month() =>
        FollowUpAutomation.HealthCheckupRecallDate(new DateOnly(2028, 2, 29), 12).ShouldBe(new DateOnly(2029, 2, 28));

    [Theory]
    [InlineData("OPV", "opv", "Oral polio vaccine", "Polio drops", true)]
    [InlineData("OPV", "IPV", "Polio", "Polio", false)]
    [InlineData(null, "MR", "Measles-Rubella", " measles-rubella ", true)]
    [InlineData(null, null, "Hepatitis B", "Typhoid", false)]
    public void Doses_are_of_the_same_vaccine_by_card_code_or_else_by_name(string? code, string? otherCode, string name, string otherName, bool same) =>
        FollowUpAutomation.IsSameVaccine(name, code, otherName, otherCode).ShouldBe(same);

    [Fact]
    public void The_reminder_goes_once_from_the_lead_days_until_the_due_date()
    {
        var task = NewTask(FollowUpType.Vaccination, Today.AddDays(2));

        FollowUpAutomation.IsReminderDue(task, Today, 2).ShouldBeTrue();
        FollowUpAutomation.IsReminderDue(task, Today, 1).ShouldBeFalse("Two days ahead is too early for a one-day lead.");
        FollowUpAutomation.IsReminderDue(task, Today.AddDays(2), 2).ShouldBeTrue("On the due date itself.");
        FollowUpAutomation.IsReminderDue(task, Today.AddDays(3), 2).ShouldBeFalse("Once the date has passed, it is a call for the team.");

        task.ReminderSentAt = new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero);
        FollowUpAutomation.IsReminderDue(task, Today, 2).ShouldBeFalse("Reminded once, by staff or by the job.");
    }

    [Theory]
    [InlineData(FollowUpType.PostDischarge)]
    [InlineData(FollowUpType.PaymentDue)]
    [InlineData(FollowUpType.Feedback)]
    public void Calls_the_hospital_makes_send_the_patient_no_reminder(FollowUpType type) =>
        FollowUpAutomation.IsReminderDue(NewTask(type, Today), Today, 2).ShouldBeFalse();

    [Fact]
    public void Only_open_tasks_send_a_reminder()
    {
        FollowUpAutomation.IsReminderDue(NewTask(FollowUpType.PostConsultation, Today, FollowUpStatus.InProgress), Today, 2).ShouldBeTrue();
        FollowUpAutomation.IsReminderDue(NewTask(FollowUpType.PostConsultation, Today, FollowUpStatus.Completed), Today, 2).ShouldBeFalse();
        FollowUpAutomation.IsReminderDue(NewTask(FollowUpType.PostConsultation, Today, FollowUpStatus.Cancelled), Today, 2).ShouldBeFalse();
    }
}
