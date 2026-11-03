using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Integrations;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A chargeback case: what the bank's notice says and the date to answer by, reminders as the date comes near (kept
/// apart from the case, <see cref="PaymentDisputeReminder"/>), the evidence sent, and the two ends — won (by an accountant, or by the gateway returning the money) and lost (only by a
/// reversal someone approved). A closed case takes no more changes. A case on a gateway payment follows the gateway's
/// own record of the dispute: its reason code, phase, status and deadline.
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

    private static GatewayDispute Gateway(GatewayDisputeStatus status, string word, DateTimeOffset? submittedAt = null) =>
        new("disp_AHfqOvkldwsbqt", "pay_EsyWjHrfzb59eR", 1_500m, "pre_arbitration", "pre_arbitration", status, word,
            DateTimeOffset.FromUnixTimeSeconds(1590604200), DateTimeOffset.FromUnixTimeSeconds(1590059211), submittedAt);

    [Fact]
    public void The_last_day_to_answer_is_the_day_before_the_gateways_midnight()
    {
        // Razorpay's documented dispute answers by 1590604200: midnight in India at the start of 28 May 2020.
        PaymentDisputeService.LastDay(DateTimeOffset.FromUnixTimeSeconds(1590604200)).ShouldBe(new DateOnly(2020, 5, 27));
        PaymentDisputeService.LastDay(new DateTimeOffset(2020, 5, 27, 23, 59, 0, TimeSpan.FromHours(5.5))).ShouldBe(new DateOnly(2020, 5, 27));
        PaymentDisputeService.LastDay(null).ShouldBeNull();
    }

    [Fact]
    public void A_gateway_dispute_gives_the_case_its_reason_code_phase_status_and_deadline()
    {
        var dispute = Case();
        dispute.Describe("10.4", "Card holder says the payment was not theirs", Today.AddDays(9));

        var changed = dispute.FollowGateway(Gateway(GatewayDisputeStatus.Open, "open"), Today.AddDays(5), null, Now);

        changed.ShouldBe("follows the gateway's dispute disp_AHfqOvkldwsbqt; the gateway says open; phase pre arbitration; reason code pre_arbitration; answer by "
                         + Today.AddDays(5).ToString("dd-MM-yyyy"));
        dispute.ShouldSatisfyAllConditions(
            d => d.GatewayDisputeId.ShouldBe("disp_AHfqOvkldwsbqt"),
            d => d.GatewayStatus.ShouldBe("open"),
            d => d.GatewayPhase.ShouldBe("pre_arbitration"),
            d => d.ReasonCode.ShouldBe("pre_arbitration"),
            d => d.Reason.ShouldBe("Card holder says the payment was not theirs", "the accountant's words are kept"),
            d => d.RespondBy.ShouldBe(Today.AddDays(5)),
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open));
    }

    [Fact]
    public void Reading_the_same_dispute_again_changes_nothing()
    {
        var dispute = Case();
        dispute.FollowGateway(Gateway(GatewayDisputeStatus.Open, "open"), Today.AddDays(5), null, Now);

        dispute.FollowGateway(Gateway(GatewayDisputeStatus.Open, "open"), Today.AddDays(5), null, Now.AddHours(1)).ShouldBeNull();
    }

    [Fact]
    public void Evidence_submitted_at_the_gateway_makes_the_case_contested()
    {
        var dispute = Case();

        dispute.FollowGateway(Gateway(GatewayDisputeStatus.UnderReview, "under_review"), Today.AddDays(5), Today.AddDays(-1), Now);

        dispute.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Contested),
            d => d.EvidenceSentOn.ShouldBe(Today.AddDays(-1)),
            d => d.EvidenceNote.ShouldBe("Submitted at the gateway."));
    }

    [Fact]
    public void Evidence_the_accountant_recorded_is_kept_when_the_gateway_reports_it_submitted()
    {
        var dispute = Case();
        dispute.RecordEvidence(Today.AddDays(-2), "Bill and consent uploaded to Razorpay", "Asha", Today);

        dispute.FollowGateway(Gateway(GatewayDisputeStatus.UnderReview, "under_review"), Today.AddDays(5), Today.AddDays(-1), Now);

        dispute.EvidenceSentOn.ShouldBe(Today.AddDays(-2));
        dispute.EvidenceNote.ShouldBe("Bill and consent uploaded to Razorpay");
    }

    [Fact]
    public void The_gateway_deciding_for_the_hospital_wins_the_case()
    {
        var dispute = Case();

        dispute.FollowGateway(Gateway(GatewayDisputeStatus.Won, "won"), Today.AddDays(5), null, Now)
            .ShouldEndWith("won: the gateway decided it for the hospital");

        dispute.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Won),
            d => d.ClosedByName.ShouldBeNull(),
            d => d.ClosedAt.ShouldBe(Now),
            d => d.Outcome.ShouldBe("The gateway decided the dispute for the hospital."));
    }

    [Fact]
    public void A_loss_the_gateway_decided_waits_for_someone_to_accept_it()
    {
        var dispute = Case();
        var reminder = new PaymentDisputeReminder();

        dispute.FollowGateway(Gateway(GatewayDisputeStatus.Lost, "lost"), Today, null, Now);

        dispute.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Open, "a loss is booked only by a reversal a second person approves"),
            d => d.DecidedAgainst.ShouldBeTrue(),
            d => d.ReversalId.ShouldBeNull());
        reminder.IsDue(dispute, Today).ShouldBeFalse("nothing is answered any more");
        dispute.Lose(Guid.CreateVersion7(), "NHRR26000010", Now).ShouldBeTrue();
        dispute.DecidedAgainst.ShouldBeFalse();
    }

    [Fact]
    public void A_closed_case_only_keeps_the_gateways_status()
    {
        var dispute = Case();
        dispute.Win("HDFC credited it back", "Asha", Now);

        dispute.FollowGateway(Gateway(GatewayDisputeStatus.Lost, "lost"), Today.AddDays(5), null, Now)
            .ShouldBe("follows the gateway's dispute disp_AHfqOvkldwsbqt; the gateway says lost; phase pre arbitration");

        dispute.ShouldSatisfyAllConditions(
            d => d.Status.ShouldBe(PaymentDisputeStatus.Won),
            d => d.RespondBy.ShouldBeNull(),
            d => d.ReasonCode.ShouldBeNull(),
            d => d.DecidedAgainst.ShouldBeFalse());
    }

    [Fact]
    public void The_deadline_of_a_case_following_a_gateway_dispute_is_the_gateways()
    {
        var dispute = Case();
        dispute.FollowGateway(Gateway(GatewayDisputeStatus.Open, "open"), Today.AddDays(5), null, Now);

        var refused = dispute.Describe("4837", "No authorisation", Today.AddDays(9));

        refused.Error!.Fields!.ShouldContainKey("respondBy");
        dispute.RespondBy.ShouldBe(Today.AddDays(5));
        dispute.Describe("4837", "No authorisation", Today.AddDays(5)).IsSuccess.ShouldBeTrue();
        dispute.Reason.ShouldBe("No authorisation");
    }

    [Fact]
    public void The_money_of_a_case_won_by_the_gateways_decision_comes_back_once()
    {
        var dispute = Case();
        var returned = Guid.CreateVersion7();
        dispute.Returned(returned).ShouldBeFalse("an open case is won by the return instead");
        dispute.FollowGateway(Gateway(GatewayDisputeStatus.Won, "won"), Today, null, Now);

        dispute.Returned(returned).ShouldBeTrue();
        dispute.Returned(Guid.CreateVersion7()).ShouldBeFalse();

        dispute.ReturnLineId.ShouldBe(returned);
        dispute.Outcome.ShouldBe("The gateway decided the dispute for the hospital.");
    }
}
