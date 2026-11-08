using HyFib.HealthCare360.Modules.Inpatient.Domain;

namespace HyFib.HealthCare360.Modules.Inpatient.Tests;

/// <summary>The rules of NEWS2 escalation and of transfers between wards, without a database.</summary>
public sealed class WardCareRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 6, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(4, false, null)]
    [InlineData(3, true, EscalationLevel.Urgent)]
    [InlineData(5, false, EscalationLevel.Urgent)]
    [InlineData(6, true, EscalationLevel.Urgent)]
    [InlineData(7, false, EscalationLevel.Emergency)]
    [InlineData(12, true, EscalationLevel.Emergency)]
    public void An_escalation_is_raised_at_5_or_a_single_3_and_is_an_emergency_at_7(int score, bool single3, EscalationLevel? level) =>
        EarlyWarningEscalation.LevelFor(score, single3).ShouldBe(level);

    [Fact]
    public void Whoever_took_the_observations_does_not_record_the_response_and_it_is_recorded_once()
    {
        var nurse = Guid.NewGuid();
        var escalation = new EarlyWarningEscalation { VitalsRecordedBy = nurse, VitalsRecordedAt = Now, Summary = "NEWS2 7", Level = EscalationLevel.Emergency };
        escalation.Respond(nurse, "Nurse", false, EscalationOutcome.RapidResponseCalled, "Called", Now).Error!.Code.ShouldBe("escalation.self");

        escalation.Respond(Guid.NewGuid(), "Doctor", true, EscalationOutcome.RapidResponseCalled, " Called RRT ", Now.AddMinutes(12)).IsSuccess.ShouldBeTrue();
        (escalation.Status, escalation.MinutesToRespond, escalation.Response, escalation.RespondedByDoctor)
            .ShouldBe((EscalationStatus.Responded, 12, "Called RRT", true));
        escalation.Respond(Guid.NewGuid(), "Other", true, EscalationOutcome.TreatmentChanged, "Again", Now).Error!.Code.ShouldBe("escalation.responded");
    }

    [Theory]
    [InlineData(EscalationLevel.Urgent, 29, false)]
    [InlineData(EscalationLevel.Urgent, 30, true)]
    [InlineData(EscalationLevel.Emergency, 14, false)]
    [InlineData(EscalationLevel.Emergency, 15, true)]
    public void An_unanswered_escalation_is_due_a_reminder_after_30_minutes_urgent_and_15_emergency(EscalationLevel level, int minutes, bool due) =>
        new EarlyWarningEscalation { Level = level, RaisedAt = Now, VitalsRecordedAt = Now.AddHours(-6) }
            .IsReminderDue(Now.AddMinutes(minutes)).ShouldBe(due, "counted from when it was raised, not from vital signs entered late");

    [Fact]
    public void A_reminder_is_sent_once_and_never_after_a_response()
    {
        var reminded = new EarlyWarningEscalation { Level = EscalationLevel.Urgent, RaisedAt = Now, ReminderSentAt = Now.AddMinutes(30) };
        reminded.IsReminderDue(Now.AddHours(2)).ShouldBeFalse();

        var answered = new EarlyWarningEscalation { Level = EscalationLevel.Urgent, RaisedAt = Now, VitalsRecordedAt = Now };
        answered.Respond(Guid.NewGuid(), "Doctor", true, EscalationOutcome.TreatmentChanged, "Seen", Now.AddMinutes(10)).IsSuccess.ShouldBeTrue();
        answered.IsReminderDue(Now.AddHours(2)).ShouldBeFalse();
    }

    [Fact]
    public void The_receiving_ward_decides_never_whoever_asked()
    {
        var sender = Guid.NewGuid();
        var request = new TransferRequest { RequestedBy = sender, Status = TransferRequestStatus.Pending };
        request.CheckDecider(sender).Error!.Code.ShouldBe("transfer.self_accept");
        request.CheckDecider(Guid.NewGuid()).IsSuccess.ShouldBeTrue();

        request.Decline(Guid.NewGuid(), "In charge", "No bed", Now).IsSuccess.ShouldBeTrue();
        request.CheckDecider(Guid.NewGuid()).Error!.Code.ShouldBe("transfer.decided");
        request.Cancel(sender, "Nurse", "Again", Now).Error!.Code.ShouldBe("transfer.not_pending");
    }

    [Fact]
    public void An_urgent_move_is_accepted_within_the_hour_or_flagged_and_cannot_be_declined()
    {
        var request = new TransferRequest { IsUrgent = true, Status = TransferRequestStatus.MovedAwaitingAcceptance, TransferredAt = Now };
        request.AcceptanceDueAt.ShouldBe(Now.AddHours(1));
        request.IsAcceptanceOverdue(Now.AddMinutes(59)).ShouldBeFalse();
        request.IsAcceptanceOverdue(Now.AddMinutes(61)).ShouldBeTrue();
        request.Decline(Guid.NewGuid(), "In charge", "No", Now).Error!.Code.ShouldBe("transfer.not_pending");

        request.Accept(Guid.NewGuid(), "In charge", null, Now.AddMinutes(75), null);
        (request.Status, request.WasAcceptedLate, request.TransferredAt).ShouldBe((TransferRequestStatus.Completed, true, Now));
        request.IsAcceptanceOverdue(Now.AddHours(3)).ShouldBeFalse();
    }

    [Fact]
    public void A_care_plan_problem_keeps_its_interventions_and_is_resolved_once()
    {
        var problem = new CarePlanProblem { Interventions = [new CarePlanIntervention("Reposition", "Every 2 hours")] };
        problem.Interventions.ShouldHaveSingleItem().Frequency.ShouldBe("Every 2 hours");
        problem.Resolve("Nurse", " Healed ", Now).IsSuccess.ShouldBeTrue();
        problem.ResolutionNote.ShouldBe("Healed");
        problem.Resolve("Nurse", "Again", Now).Error!.Code.ShouldBe("care_plan.resolved");
        CarePlanProblems.All.Select(p => p.Code).ShouldBeUnique();
        CarePlanProblems.All.ShouldAllBe(p => p.Interventions.Count > 0);
    }
}
