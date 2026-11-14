using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A card/UPI sale whose upload answer was lost (INT-POS): it may still be on the terminal, so it keeps a second sale off
/// that terminal for the terminal's auto-cancel window, can be cleared early by a cashier who sees the terminal shows no
/// sale, and lapses with its outcome unknown once the window has passed. The sale row itself is saved before the terminal
/// is called, so one the server dropped is a pending row with a placeholder id.
/// </summary>
public sealed class PosUnconfirmedTests
{
    // 10:30 in India: 05:00 UTC.
    private static readonly DateTimeOffset Sent = new(2026, 11, 14, 5, 0, 0, TimeSpan.Zero);

    private static PosTransaction Sale(PosTransactionStatus status = PosTransactionStatus.Unconfirmed, string transactionId = "unconfirmed-1") => new()
    {
        FacilityId = TestData.FacilityId,
        TerminalId = "OPD-1",
        TransactionId = transactionId,
        Amount = 1250m,
        Status = status,
        CreatedAt = Sent,
    };

    [Theory]
    [InlineData(5, 7)]
    [InlineData(1, 3)]
    [InlineData(30, 32)]
    [InlineData(0, 3)]
    [InlineData(90, 32)]
    public void The_window_is_the_auto_cancel_minutes_the_terminal_is_told_plus_a_margin(int autoCancelMinutes, int expectedMinutes) =>
        PosUnconfirmed.Window(autoCancelMinutes).ShouldBe(TimeSpan.FromMinutes(expectedMinutes));

    [Fact]
    public void An_unconfirmed_sale_keeps_the_terminal_busy_until_its_window_has_passed()
    {
        var window = PosUnconfirmed.Window(5);
        var sale = Sale();

        sale.BlocksTerminal(Sent.AddMinutes(6), window).ShouldBeTrue();
        sale.BlocksTerminal(Sent.AddMinutes(7).AddSeconds(-1), window).ShouldBeTrue();
        sale.BlocksTerminal(Sent.AddMinutes(7), window).ShouldBeFalse();
    }

    [Theory]
    [InlineData(PosTransactionStatus.Approved)]
    [InlineData(PosTransactionStatus.Declined)]
    [InlineData(PosTransactionStatus.Cancelled)]
    [InlineData(PosTransactionStatus.TimedOut)]
    [InlineData(PosTransactionStatus.Lapsed)]
    [InlineData(PosTransactionStatus.AwaitingCustomer)]
    public void A_sale_the_terminal_answered_for_or_that_ended_does_not_keep_it_busy(PosTransactionStatus status) =>
        Sale(status, "OPD-1:987654:125000").BlocksTerminal(Sent.AddMinutes(1), PosUnconfirmed.Window(5)).ShouldBeFalse();

    [Fact]
    public void A_sale_saved_before_the_call_whose_answer_never_arrived_keeps_the_terminal_busy_too()
    {
        var pending = Sale(PosTransactionStatus.Initiated, PosUnconfirmed.PendingPrefix + "abc");

        pending.IsAwaitingProvider.ShouldBeTrue();
        pending.BlocksTerminal(Sent.AddMinutes(1), PosUnconfirmed.Window(5)).ShouldBeTrue();
        Sale(PosTransactionStatus.Initiated, "OPD-1:987654:125000").IsAwaitingProvider.ShouldBeFalse();
    }

    [Fact]
    public void A_pending_sale_the_server_dropped_becomes_unconfirmed_with_a_generated_id()
    {
        var pending = Sale(PosTransactionStatus.Initiated, PosUnconfirmed.PendingPrefix + "abc");

        pending.MarkUnconfirmed(PosUnconfirmed.UnconfirmedMessage("HC360 stopped."));

        pending.Status.ShouldBe(PosTransactionStatus.Unconfirmed);
        pending.TransactionId.ShouldStartWith(PosUnconfirmed.IdPrefix);
        pending.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void An_approval_is_never_downgraded_to_unconfirmed()
    {
        var approved = Sale(PosTransactionStatus.Approved, "OPD-1:987654:125000");

        approved.MarkUnconfirmed("late");

        approved.Status.ShouldBe(PosTransactionStatus.Approved);
    }

    [Fact]
    public void A_second_push_inside_the_window_is_refused_with_what_to_check_and_until_when()
    {
        var error = PosUnconfirmed.Refusal(Sale(), PosUnconfirmed.Window(5));

        error.Code.ShouldBe("pos.terminal_unconfirmed");
        error.Type.ShouldBe(ErrorType.BusinessRule);
        error.Message.ShouldContain("₹1,250");
        error.Message.ShouldContain("at 10:30");
        error.Message.ShouldContain("check the terminal's screen", Case.Insensitive);
        error.Message.ShouldContain("record the payment from the slip");
        error.Message.ShouldContain("wait until 10:37");
    }

    [Fact]
    public void The_cashier_who_sees_no_sale_on_the_terminal_clears_it_and_the_terminal_is_free()
    {
        var sale = Sale();

        sale.ClearUnconfirmed(Sent.AddMinutes(2), "Terminal screen shows the idle menu.");

        sale.Status.ShouldBe(PosTransactionStatus.Cancelled);
        sale.IsComplete.ShouldBeTrue();
        sale.CompletedAt.ShouldBe(Sent.AddMinutes(2));
        sale.BlocksTerminal(Sent.AddMinutes(3), PosUnconfirmed.Window(5)).ShouldBeFalse();
    }

    [Fact]
    public void Only_an_unconfirmed_sale_can_be_cleared_or_lapsed()
    {
        var approved = Sale(PosTransactionStatus.Approved, "OPD-1:987654:125000");

        approved.ClearUnconfirmed(Sent, "x");
        approved.Lapse(Sent, "x");

        approved.Status.ShouldBe(PosTransactionStatus.Approved);
    }

    [Fact]
    public void A_sale_that_lapsed_after_the_window_ends_with_its_outcome_unknown()
    {
        var sale = Sale();
        var now = Sent.AddMinutes(8);

        sale.Lapse(now, PosUnconfirmed.LapsedMessage(sale.CreatedAt));

        sale.Status.ShouldBe(PosTransactionStatus.Lapsed);
        sale.IsComplete.ShouldBeTrue();
        sale.CompletedAt.ShouldBe(now);
        sale.Message.ShouldNotBeNull().ShouldContain("outcome is unknown");
        sale.Message.ShouldContain("10:30");
        sale.BlocksTerminal(now, PosUnconfirmed.Window(5)).ShouldBeFalse();
    }

    [Fact]
    public void An_approval_is_final_even_for_an_unconfirmed_sale()
    {
        var sale = Sale();

        sale.Apply(new PosTransactionResult(sale.TransactionId, PosTransactionStatus.Approved, "000123", "4242", "627318291028", "TXN APPROVED"), Sent.AddMinutes(1));

        // An approval is final even for an unconfirmed sale: the accountants' record, not a lost answer, decides what it was.
        sale.Status.ShouldBe(PosTransactionStatus.Approved);
    }
}
