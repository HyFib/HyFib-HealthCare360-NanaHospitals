using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class FollowUpTaskTests
{
    private static readonly DateOnly Today = new(2026, 9, 26);
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);

    private static FollowUpTask NewTask(FollowUpType type = FollowUpType.PostDischarge) =>
        new() { PatientId = Guid.NewGuid(), Type = type, DueDate = Today, Status = FollowUpStatus.Open };

    [Fact]
    public void Reaching_the_patient_completes_the_task()
    {
        var task = NewTask();

        task.RecordAttempt(FollowUpOutcome.Reached, " Recovering well ", null, Today, Now).IsSuccess.ShouldBeTrue();

        task.Status.ShouldBe(FollowUpStatus.Completed);
        task.CompletedAt.ShouldBe(Now);
        task.Attempts.ShouldBe(1);
        task.OutcomeNotes.ShouldBe("Recovering well");
    }

    [Fact]
    public void An_unanswered_call_is_retried_tomorrow_by_default()
    {
        var task = NewTask();

        task.RecordAttempt(FollowUpOutcome.NotReachable, null, null, Today, Now);

        task.Status.ShouldBe(FollowUpStatus.InProgress);
        task.DueDate.ShouldBe(Today.AddDays(1));
        task.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void Call_back_later_uses_the_date_the_patient_asked_for()
    {
        var task = NewTask();

        task.RecordAttempt(FollowUpOutcome.CallBackLater, null, Today.AddDays(5), Today, Now);

        task.DueDate.ShouldBe(Today.AddDays(5));
        task.Status.ShouldBe(FollowUpStatus.InProgress);
    }

    [Fact]
    public void After_three_unanswered_calls_the_task_closes_as_not_reachable()
    {
        var task = NewTask();

        task.RecordAttempt(FollowUpOutcome.NotReachable, null, null, Today, Now);
        task.RecordAttempt(FollowUpOutcome.NotReachable, null, null, Today, Now);
        task.RecordAttempt(FollowUpOutcome.NotReachable, null, null, Today, Now);

        task.Attempts.ShouldBe(FollowUpTask.MaxAttempts);
        task.Status.ShouldBe(FollowUpStatus.Completed);
        task.Outcome.ShouldBe(FollowUpOutcome.NotReachable);
    }

    [Fact]
    public void Call_back_requests_never_exhaust_the_attempts()
    {
        var task = NewTask();

        for (var i = 0; i < 4; i++)
        {
            task.RecordAttempt(FollowUpOutcome.CallBackLater, null, null, Today, Now);
        }

        task.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void The_next_call_cannot_be_in_the_past_and_closed_tasks_take_no_calls()
    {
        var task = NewTask();

        task.RecordAttempt(FollowUpOutcome.CallBackLater, null, Today.AddDays(-1), Today, Now).Error!.Fields!.ShouldContainKey("nextDueDate");
        task.Attempts.ShouldBe(0);

        task.Cancel("Duplicate task").IsSuccess.ShouldBeTrue();
        task.RecordAttempt(FollowUpOutcome.Reached, null, null, Today, Now).Error!.Code.ShouldBe("followup.closed");
        task.Cancel("Again").IsFailure.ShouldBeTrue();
    }

    [Theory]
    [InlineData(FollowUpType.PostConsultation, true)]
    [InlineData(FollowUpType.Vaccination, true)]
    [InlineData(FollowUpType.ChronicReview, true)]
    [InlineData(FollowUpType.HealthCheckupDue, true)]
    [InlineData(FollowUpType.PostDischarge, false)]
    [InlineData(FollowUpType.PaymentDue, false)]
    [InlineData(FollowUpType.Feedback, false)]
    public void Only_come_back_tasks_close_when_an_appointment_is_booked(FollowUpType type, bool closes) =>
        FollowUpTask.ClosesOnBooking(type).ShouldBe(closes);

    [Fact]
    public void Completing_as_booked_is_not_a_call_attempt()
    {
        var task = NewTask(FollowUpType.ChronicReview);

        task.CompleteAsBooked("Appointment booked for 01 Oct 2026 10:30 (Online).", Now).IsSuccess.ShouldBeTrue();

        task.Status.ShouldBe(FollowUpStatus.Completed);
        task.Outcome.ShouldBe(FollowUpOutcome.AppointmentBooked);
        task.Attempts.ShouldBe(0);
        task.CompleteAsBooked("again", Now).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void A_task_is_overdue_only_while_open_and_past_its_due_date()
    {
        var task = NewTask();
        task.DueDate = Today.AddDays(-1);

        task.IsOverdue(Today).ShouldBeTrue();
        task.Cancel("Patient moved away");
        task.IsOverdue(Today).ShouldBeFalse();
    }
}
