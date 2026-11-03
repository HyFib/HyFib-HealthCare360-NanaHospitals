using HyFib.HealthCare360.Modules.Billing.Domain;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A chargeback case: what the bank's notice says and the date to answer by, reminders as the date comes near (kept
/// apart from the case, <see cref="PaymentDisputeReminder"/>), the evidence sent, and the two ends — won (by an accountant, or by the gateway returning the money) and lost (only by a
/// reversal someone approved). A closed case takes no more changes.
/// </summary>
public sealed class PaymentDisputeTests
{
    private static readonly DateOnly Today = new(2026, 11, 3);
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(5.5));

    private static PaymentDispute Case() => new()
    {
        Number = "NHCB2600001",
        ReceiptNumber = "NHRC260000042",
        Amount = 1_500m,
        Mode = PaymentMode.Card,
        Status = PaymentDisputeStatus.Open,
        OpenedAt = Now,
    };

    [Fact]
    public void The_notice_gives_the_reason_and_the_date_to_answer_by()
    {
        var dispute = Case();

        dispute.Describe(" 10.4 ", "  Card holder says the payment was not theirs ", Today.AddDays(7)).IsSuccess.ShouldBeTrue();

        dispute.ShouldSatisfyAllConditions(
            d => d.ReasonCode.ShouldBe("10.4"),
            d => d.Reason.ShouldBe("Card holder says the payment was not theirs"),
            d => d.RespondBy.ShouldBe(Today.AddDays(7)),
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open));
    }

    [Fact]
    public void Blank_words_are_kept_as_nothing_and_long_ones_are_cut()
    {
        var dispute = Case();

        dispute.Describe("   ", new string('x', PaymentDispute.NoteMaxLength + 20), null);

        dispute.ReasonCode.ShouldBeNull();
        dispute.Reason!.Length.ShouldBe(PaymentDispute.NoteMaxLength);
    }

    [Fact]
    public void A_case_is_reminded_once_a_day_from_two_days_before_its_deadline()
    {
        var dispute = Case();
        var reminder = new PaymentDisputeReminder();
        dispute.Describe(null, null, Today.AddDays(PaymentDispute.RemindDays + 1));
        reminder.IsDue(dispute, Today).ShouldBeFalse();

        dispute.Describe(null, null, Today.AddDays(PaymentDispute.RemindDays));
        reminder.IsDue(dispute, Today).ShouldBeTrue();

        reminder.Reminded(Today, dispute.RespondBy!.Value);
        reminder.IsDue(dispute, Today).ShouldBeFalse();
        reminder.IsDue(dispute, Today.AddDays(1)).ShouldBeTrue();
    }

    [Fact]
    public void A_late_case_is_still_reminded()
    {
        var dispute = Case();
        dispute.Describe(null, null, Today.AddDays(-3));

        new PaymentDisputeReminder().IsDue(dispute, Today).ShouldBeTrue();
    }

    [Fact]
    public void A_new_deadline_is_reminded_again_the_same_day()
    {
        var dispute = Case();
        var reminder = new PaymentDisputeReminder();
        dispute.Describe(null, null, Today.AddDays(1));
        reminder.Reminded(Today, Today.AddDays(1));

        dispute.Describe(null, null, Today);

        reminder.IsDue(dispute, Today).ShouldBeTrue();
    }

    [Fact]
    public void The_same_deadline_said_again_is_not_reminded_twice_a_day()
    {
        var dispute = Case();
        var reminder = new PaymentDisputeReminder();
        dispute.Describe(null, null, Today.AddDays(1));
        reminder.Reminded(Today, Today.AddDays(1));

        dispute.Describe("4837", "No authorisation", Today.AddDays(1));

        reminder.IsDue(dispute, Today).ShouldBeFalse();
        (reminder.RemindedOn, reminder.RemindedFor).ShouldBe((Today, Today.AddDays(1)));
    }

    [Fact]
    public void Without_a_deadline_or_once_answered_nothing_is_reminded()
    {
        var dispute = Case();
        var reminder = new PaymentDisputeReminder();
        reminder.IsDue(dispute, Today).ShouldBeFalse();

        dispute.Describe(null, null, Today);
        dispute.RecordEvidence(Today, "Bill, receipt and the patient's signed consent to the bank", "Asha", Today);

        reminder.IsDue(dispute, Today).ShouldBeFalse();
    }

    [Fact]
    public void The_evidence_sent_waits_for_the_bank_to_decide()
    {
        var dispute = Case();

        dispute.RecordEvidence(Today.AddDays(-1), "  Bill and discharge summary to HDFC  ", "Asha", Today).IsSuccess.ShouldBeTrue();

        dispute.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Contested),
            d => d.EvidenceSentOn.ShouldBe(Today.AddDays(-1)),
            d => d.EvidenceNote.ShouldBe("Bill and discharge summary to HDFC"),
            d => d.EvidenceByName.ShouldBe("Asha"),
            d => d.IsClosed.ShouldBeFalse());
    }

    [Fact]
    public void Evidence_cannot_have_been_sent_after_today()
    {
        var dispute = Case();

        var result = dispute.RecordEvidence(Today.AddDays(1), "Bill to the bank", "Asha", Today);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Fields!.ShouldContainKey("sentOn");
        dispute.Status.ShouldBe(PaymentDisputeStatus.Open);
    }

    [Fact]
    public void Evidence_needs_words_on_what_was_sent()
    {
        var result = Case().RecordEvidence(Today, "  ", "Asha", Today);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Fields!.ShouldContainKey("note");
    }

    [Fact]
    public void An_accountant_records_a_win_in_words()
    {
        var dispute = Case();
        dispute.Win(" ", "Asha", Now).Error!.Fields!.ShouldContainKey("note");

        dispute.Win("HDFC credited the money back on 2 Nov", "Asha", Now).IsSuccess.ShouldBeTrue();

        dispute.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Won),
            d => d.IsClosed.ShouldBeTrue(),
            d => d.Outcome.ShouldBe("HDFC credited the money back on 2 Nov"),
            d => d.ClosedByName.ShouldBe("Asha"),
            d => d.ClosedAt.ShouldBe(Now));
    }

    [Fact]
    public void The_gateway_returning_the_money_wins_the_case_without_anyone()
    {
        var dispute = Case();
        var returned = Guid.CreateVersion7();

        dispute.WonByReturn("The gateway returned ₹1,500.00 in settlement setl_1", returned, Now).ShouldBeTrue();

        dispute.Status.ShouldBe(PaymentDisputeStatus.Won);
        dispute.ClosedByName.ShouldBeNull();
        dispute.ReturnLineId.ShouldBe(returned);
        dispute.Outcome.ShouldBe("The gateway returned ₹1,500.00 in settlement setl_1");
        dispute.WonByReturn("again", Guid.CreateVersion7(), Now).ShouldBeFalse();
        dispute.Outcome.ShouldBe("The gateway returned ₹1,500.00 in settlement setl_1");
        dispute.ReturnLineId.ShouldBe(returned);
    }

    [Fact]
    public void Asking_to_accept_the_loss_keeps_the_case_open_until_the_reversal_is_made()
    {
        var dispute = Case();
        var subject = Guid.CreateVersion7();

        dispute.LossAsked("Asha", "billing.receipt", subject, Now).IsSuccess.ShouldBeTrue();

        dispute.ShouldSatisfyAllConditions(
            d => d.LossAskedAt.ShouldBe(Now),
            d => d.LossAskedByName.ShouldBe("Asha"),
            d => d.LossSubjectType.ShouldBe("billing.receipt"),
            d => d.LossSubjectId.ShouldBe(subject),
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open),
            d => d.IsClosed.ShouldBeFalse());
    }

    [Fact]
    public void The_approved_reversal_loses_the_case_and_names_itself()
    {
        var dispute = Case();
        var reversal = Guid.CreateVersion7();

        dispute.Lose(reversal, "NHRR26000007", Now).ShouldBeTrue();

        dispute.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Lost),
            d => d.ReversalId.ShouldBe(reversal),
            d => d.ReversalNumber.ShouldBe("NHRR26000007"),
            d => d.Outcome.ShouldBe("Receipt NHRC260000042 reversed (NHRR26000007): the bill owes the money again."),
            d => d.ClosedByName.ShouldBeNull(),
            d => d.ClosedAt.ShouldBe(Now));
    }

    [Fact]
    public void A_won_case_is_not_lost_by_a_later_reversal()
    {
        var dispute = Case();
        dispute.Win("Bank dropped it", "Asha", Now);

        dispute.Lose(Guid.CreateVersion7(), "NHRR26000008", Now).ShouldBeFalse();

        dispute.Status.ShouldBe(PaymentDisputeStatus.Won);
        dispute.ReversalId.ShouldBeNull();
    }

    [Fact]
    public void A_closed_case_takes_no_more_changes()
    {
        var dispute = Case();
        dispute.Lose(Guid.CreateVersion7(), "NHRR26000009", Now);

        var refusals = new[]
        {
            dispute.Describe("10.4", "Fraud", Today),
            dispute.RecordEvidence(Today, "Bill to the bank", "Asha", Today),
            dispute.Win("Bank dropped it", "Asha", Now),
            dispute.LossAsked("Asha", "billing.receipt", Guid.CreateVersion7(), Now),
        };

        refusals.ShouldAllBe(r => r.IsFailure && r.Error!.Code == "billing.dispute_closed");
        refusals[0].Error!.Message.ShouldBe("Chargeback NHCB2600001 is closed: it was lost.");
        dispute.ReasonCode.ShouldBeNull();
        dispute.EvidenceNote.ShouldBeNull();
        dispute.LossAskedAt.ShouldBeNull();
        dispute.Status.ShouldBe(PaymentDisputeStatus.Lost);
    }

    [Fact]
    public void A_won_case_says_it_was_won()
    {
        var dispute = Case();
        dispute.WonByReturn("Returned", Guid.CreateVersion7(), Now);

        dispute.Describe(null, null, Today).Error!.Message.ShouldBe("Chargeback NHCB2600001 is closed: it was won.");
    }
}
