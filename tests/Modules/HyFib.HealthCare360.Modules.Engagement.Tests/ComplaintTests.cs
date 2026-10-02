using HyFib.HealthCare360.Modules.Engagement.Domain;

namespace HyFib.HealthCare360.Modules.Engagement.Tests;

public sealed class ComplaintTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 6, 0, 0, TimeSpan.Zero);

    private static Complaint Registered(ComplaintPriority priority = ComplaintPriority.Normal)
    {
        var complaint = new Complaint { ComplaintNumber = "NHCMP2600001", Description = "Long wait at billing", Priority = priority, Status = ComplaintStatus.Open };
        complaint.Register(Now);
        return complaint;
    }

    [Theory]
    [InlineData(ComplaintPriority.High, 24)]
    [InlineData(ComplaintPriority.Normal, 72)]
    public void The_resolution_deadline_follows_the_priority(ComplaintPriority priority, int hours)
    {
        var complaint = Registered(priority);

        complaint.RaisedAt.ShouldBe(Now);
        complaint.DueAt.ShouldBe(Now.AddHours(hours));
    }

    [Fact]
    public void Raising_the_priority_tightens_the_deadline_and_lowering_it_never_extends_it()
    {
        var complaint = Registered();

        complaint.ChangePriority(ComplaintPriority.High);
        complaint.DueAt.ShouldBe(Now.AddHours(24));

        complaint.ChangePriority(ComplaintPriority.Normal);
        complaint.DueAt.ShouldBe(Now.AddHours(24));
    }

    [Fact]
    public void Resolving_needs_a_resolution_and_records_the_turnaround()
    {
        var complaint = Registered();

        complaint.MoveTo(ComplaintStatus.InProgress, null, Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        complaint.MoveTo(ComplaintStatus.Resolved, " ", Now.AddHours(5)).Error!.Fields!.ShouldContainKey("note");
        complaint.MoveTo(ComplaintStatus.Resolved, " Apologised; billing queue now has a second counter ", Now.AddHours(5)).IsSuccess.ShouldBeTrue();

        complaint.Resolution.ShouldBe("Apologised; billing queue now has a second counter");
        complaint.TurnaroundTime.ShouldBe(TimeSpan.FromHours(5));
        complaint.MoveTo(ComplaintStatus.Closed, null, Now.AddHours(6)).IsSuccess.ShouldBeTrue();
        complaint.ClosedAt.ShouldBe(Now.AddHours(6));
    }

    [Fact]
    public void Reopening_a_resolved_complaint_needs_a_reason_and_is_counted()
    {
        var complaint = Registered();
        complaint.MoveTo(ComplaintStatus.Resolved, "Called the patient", Now.AddHours(2));

        complaint.MoveTo(ComplaintStatus.InProgress, null, Now.AddHours(3)).IsFailure.ShouldBeTrue();
        complaint.MoveTo(ComplaintStatus.InProgress, "Patient not satisfied", Now.AddHours(3)).IsSuccess.ShouldBeTrue();

        complaint.ReopenCount.ShouldBe(1);
        complaint.ResolvedAt.ShouldBeNull();
        complaint.TurnaroundTime.ShouldBeNull();
    }

    [Theory]
    [InlineData(ComplaintStatus.Open, ComplaintStatus.Closed)]
    [InlineData(ComplaintStatus.InProgress, ComplaintStatus.Open)]
    [InlineData(ComplaintStatus.Open, ComplaintStatus.Open)]
    public void Invalid_transitions_are_refused(ComplaintStatus from, ComplaintStatus to)
    {
        var complaint = Registered();
        complaint.Status = from;

        complaint.MoveTo(to, "note", Now).Error!.Code.ShouldBe("complaint.invalid_transition");
    }

    [Fact]
    public void Only_unresolved_complaints_past_the_deadline_are_overdue()
    {
        var complaint = Registered(ComplaintPriority.High);

        complaint.IsOverdue(Now.AddHours(23)).ShouldBeFalse();
        complaint.IsOverdue(Now.AddHours(25)).ShouldBeTrue();

        complaint.MoveTo(ComplaintStatus.Resolved, "Done", Now.AddHours(26));
        complaint.IsOverdue(Now.AddHours(30)).ShouldBeFalse();
    }

    [Fact]
    public void A_complaint_is_escalated_once_when_it_passes_its_deadline_unresolved()
    {
        var complaint = Registered(ComplaintPriority.High);

        complaint.Escalate(Now.AddHours(23)).ShouldBeFalse("It is still within its 24 hours.");
        complaint.Escalate(Now.AddHours(25)).ShouldBeTrue();
        complaint.EscalatedAt.ShouldBe(Now.AddHours(25));

        complaint.Escalate(Now.AddHours(30)).ShouldBeFalse("A complaint is escalated once.");
        complaint.EscalatedAt.ShouldBe(Now.AddHours(25));
    }

    [Fact]
    public void A_resolved_complaint_is_never_escalated_and_reopening_does_not_escalate_it_again()
    {
        var resolved = Registered();
        resolved.MoveTo(ComplaintStatus.Resolved, "Apologised and refunded the charge", Now.AddHours(1)).IsSuccess.ShouldBeTrue();
        resolved.Escalate(Now.AddDays(5)).ShouldBeFalse();
        resolved.EscalatedAt.ShouldBeNull();

        var reopened = Registered();
        reopened.Escalate(Now.AddDays(4)).ShouldBeTrue();
        reopened.MoveTo(ComplaintStatus.Resolved, "Apologised", Now.AddDays(4).AddHours(1));
        reopened.MoveTo(ComplaintStatus.InProgress, "The patient says the bill is still wrong", Now.AddDays(5)).IsSuccess.ShouldBeTrue();
        reopened.Escalate(Now.AddDays(6)).ShouldBeFalse();
    }
}
