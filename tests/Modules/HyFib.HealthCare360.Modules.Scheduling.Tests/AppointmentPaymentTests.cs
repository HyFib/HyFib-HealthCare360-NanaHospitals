using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static HyFib.HealthCare360.Modules.Scheduling.Tests.TestSchedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

/// <summary>Server-to-server confirmation of portal prepayments (hosted payment pages and the training gateway).</summary>
public sealed class AppointmentPaymentTests
{
    private static readonly DateTimeOffset Start = Ist(Monday, 10, 0);

    [Fact]
    public async Task A_captured_order_is_recorded_and_confirms_the_booking()
    {
        var appointment = BookedWithOrder("order_1");
        var gateway = Gateway(new PaymentStatusResult("order_1", GatewayPaymentStatus.Captured, "pay_1", 600m, "upi", null));

        var result = await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        appointment.PaymentStatus.ShouldBe(AppointmentPaymentStatus.PaidOnline);
        appointment.GatewayPaymentId.ShouldBe("pay_1");
        appointment.Status.ShouldBe(AppointmentStatus.Confirmed);
        await gateway.Received(1).GetStatusAsync("order_1", Arg.Any<string?>(), appointment.FacilityId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_booking_is_paid_when_the_gateway_took_the_money_not_when_hc360_heard_of_it()
    {
        var appointment = BookedWithOrder("order_5");
        var capturedAt = Start.AddDays(-1).AddHours(-3);
        var gateway = Gateway(new PaymentStatusResult("order_5", GatewayPaymentStatus.Captured, "pay_5", 600m, "upi", null, capturedAt));

        (await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        appointment.PaidAt.ShouldBe(capturedAt);
        appointment.DomainEvents.OfType<AppointmentPaymentCaptured>().ShouldHaveSingleItem().PaidAt.ShouldBe(capturedAt);
    }

    [Fact]
    public async Task A_capture_time_ahead_of_the_servers_clock_is_not_trusted()
    {
        var appointment = BookedWithOrder("order_6");
        var gateway = Gateway(new PaymentStatusResult("order_6", GatewayPaymentStatus.Captured, "pay_6", 600m, "upi", null, Start.AddDays(2)));

        (await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        appointment.PaidAt.ShouldBe(Start.AddDays(-1));
    }

    [Theory]
    [InlineData(GatewayPaymentStatus.Expired)]
    [InlineData(GatewayPaymentStatus.Cancelled)]
    [InlineData(GatewayPaymentStatus.Refunded)]
    public async Task A_signed_confirmation_of_an_order_that_closed_unpaid_is_refused(GatewayPaymentStatus status)
    {
        var appointment = BookedWithOrder("order_7");
        var gateway = Gateway(new PaymentStatusResult("order_7", status, null, null, null, "Closed."));
        gateway.VerifySignature("order_7|pay_7", "sig", Arg.Any<string?>(), appointment.FacilityId).Returns(true);

        var result = await Service(gateway).ConfirmAsync(appointment, "order_7", "pay_7", "sig", TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("payment.not_captured");
        appointment.PaymentStatus.ShouldNotBe(AppointmentPaymentStatus.PaidOnline);
    }

    [Theory]
    [InlineData(GatewayPaymentStatus.Created)]
    [InlineData(GatewayPaymentStatus.Pending)]
    [InlineData(GatewayPaymentStatus.Failed)]
    [InlineData(GatewayPaymentStatus.Expired)]
    [InlineData(GatewayPaymentStatus.Cancelled)]
    public async Task An_order_that_is_not_captured_records_nothing(GatewayPaymentStatus status)
    {
        var appointment = BookedWithOrder("order_2");
        var gateway = Gateway(new PaymentStatusResult("order_2", status, null, null, null, null));

        var result = await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("payment.not_captured");
        appointment.PaymentStatus.ShouldNotBe(AppointmentPaymentStatus.PaidOnline);
    }

    /// <summary>
    /// Gap #35, decided by the hospital: a payment that failed, ran out of time or was given up on never costs the
    /// patient the slot. The booking stays booked and unpaid, its reminders still go out, and it can be paid again.
    /// </summary>
    [Theory]
    [InlineData(GatewayPaymentStatus.Created)]
    [InlineData(GatewayPaymentStatus.Failed)]
    [InlineData(GatewayPaymentStatus.Expired)]
    [InlineData(GatewayPaymentStatus.Cancelled)]
    public async Task A_payment_that_was_never_made_never_costs_the_patient_the_slot(GatewayPaymentStatus status)
    {
        var appointment = BookedWithOrder("order_8");
        appointment.ClearDomainEvents();
        var gateway = Gateway(new PaymentStatusResult("order_8", status, null, null, null, "Not paid."));

        (await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("payment.not_captured");

        appointment.Status.ShouldBe(AppointmentStatus.Booked);
        appointment.HoldsSeat.ShouldBeTrue();
        appointment.PaymentStatus.ShouldBe(AppointmentPaymentStatus.Unpaid);
        appointment.DomainEvents.OfType<AppointmentCancelled>().ShouldBeEmpty();
        ReminderPolicy.Decide(appointment.Status, appointment.StartsAt, appointment.BookedAt, null, null, Start.AddHours(-23)).SendDayBefore.ShouldBeTrue();
        appointment.AttachPaymentOrder("simulator", "order_9", Start.AddDays(-1)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task An_unreachable_gateway_asks_the_patient_to_try_again()
    {
        var appointment = BookedWithOrder("order_3");
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetStatusAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        var result = await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("payment.gateway_unavailable");
    }

    [Fact]
    public async Task Nothing_is_asked_when_no_payment_was_started()
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        var gateway = Substitute.For<IPaymentGateway>();

        var result = await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("payment.not_started");
        await gateway.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repeating_the_check_after_payment_is_harmless()
    {
        var appointment = BookedWithOrder("order_4");
        appointment.MarkPaidOnline("order_4", "pay_4", 600m, Start.AddDays(-1)).IsSuccess.ShouldBeTrue();
        var gateway = Substitute.For<IPaymentGateway>();

        var result = await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await gateway.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_order_paid_after_all_is_recorded_rather_than_replaced()
    {
        var appointment = BookedWithOrder("order_8");
        var gateway = Gateway(new PaymentStatusResult("order_8", GatewayPaymentStatus.Captured, "pay_8", 600m, "upi", null));

        var result = await Service(gateway).SettleOutstandingOrderAsync(appointment, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        appointment.PaymentStatus.ShouldBe(AppointmentPaymentStatus.PaidOnline);
        appointment.GatewayPaymentId.ShouldBe("pay_8");
    }

    [Fact]
    public async Task An_order_still_being_paid_is_not_replaced()
    {
        var appointment = BookedWithOrder("order_9");
        var gateway = Gateway(new PaymentStatusResult("order_9", GatewayPaymentStatus.Pending, null, null, null, null));

        var result = await Service(gateway).SettleOutstandingOrderAsync(appointment, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("payment.in_progress");
        appointment.GatewayOrderId.ShouldBe("order_9");
    }

    [Fact]
    public async Task No_new_order_is_made_while_the_gateway_cannot_say_what_became_of_the_last()
    {
        var appointment = BookedWithOrder("order_10");
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetStatusAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        var result = await Service(gateway).SettleOutstandingOrderAsync(appointment, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("payment.gateway_unavailable");
    }

    [Theory]
    [InlineData(GatewayPaymentStatus.Created)]
    [InlineData(GatewayPaymentStatus.Failed)]
    [InlineData(GatewayPaymentStatus.Expired)]
    [InlineData(GatewayPaymentStatus.Cancelled)]
    public async Task An_order_that_was_never_paid_may_be_replaced(GatewayPaymentStatus status)
    {
        var appointment = BookedWithOrder("order_11");
        var gateway = Gateway(new PaymentStatusResult("order_11", status, null, null, null, null));

        var result = await Service(gateway).SettleOutstandingOrderAsync(appointment, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        appointment.PaymentStatus.ShouldNotBe(AppointmentPaymentStatus.PaidOnline);
    }

    [Fact]
    public async Task The_gateway_is_not_asked_when_there_is_no_order_to_replace()
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        var gateway = Substitute.For<IPaymentGateway>();

        (await Service(gateway).SettleOutstandingOrderAsync(appointment, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        await gateway.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_gateway_is_asked_about_the_replaced_order_and_its_payment_pays_the_unpaid_booking()
    {
        var appointment = BookedWithOrder("order_12");
        appointment.AttachPaymentOrder("simulator", "order_13", Start.AddDays(-2).AddMinutes(5)).IsSuccess.ShouldBeTrue();
        var capturedAt = Start.AddDays(-1).AddHours(-2);
        var gateway = Gateway(new PaymentStatusResult("order_12", GatewayPaymentStatus.Captured, "pay_12", 600m, "upi", null, capturedAt));

        var taken = await Service(gateway).CheckReplacedOrderAsync(appointment, appointment.ReplacedOrders.Single(), TestContext.Current.CancellationToken);

        taken.Value.ShouldBe(ReplacedOrderPayment.PaidTheBooking);
        appointment.GatewayPaymentId.ShouldBe("pay_12");
        appointment.PaidAt.ShouldBe(capturedAt);
        await gateway.Received(1).GetStatusAsync("order_12", Arg.Any<string?>(), appointment.FacilityId, Arg.Any<CancellationToken>());
        await gateway.DidNotReceive().GetStatusAsync("order_13", Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_second_payment_is_what_the_gateway_took_when_it_took_it()
    {
        var appointment = BookedWithOrder("order_14");
        appointment.AttachPaymentOrder("simulator", "order_15", Start.AddDays(-2).AddMinutes(5));
        appointment.MarkPaidOnline("order_15", "pay_15", 600m, Start.AddDays(-2).AddMinutes(6)).IsSuccess.ShouldBeTrue();
        var capturedAt = Start.AddDays(-1).AddHours(-1);
        var gateway = Gateway(new PaymentStatusResult("order_14", GatewayPaymentStatus.Captured, "pay_14", 550m, "upi", null, capturedAt));
        var replaced = appointment.ReplacedOrders.Single();

        var taken = await Service(gateway).CheckReplacedOrderAsync(appointment, replaced, TestContext.Current.CancellationToken);

        taken.Value.ShouldBe(ReplacedOrderPayment.PaidTwice);
        replaced.AmountPaid.ShouldBe(550m, "The amount the gateway took, not the fee.");
        replaced.PaidAt.ShouldBe(capturedAt);
        appointment.GatewayPaymentId.ShouldBe("pay_15");
    }

    [Theory]
    [InlineData(GatewayPaymentStatus.Created)]
    [InlineData(GatewayPaymentStatus.Pending)]
    [InlineData(GatewayPaymentStatus.Expired)]
    public async Task A_replaced_order_nobody_paid_changes_nothing(GatewayPaymentStatus status)
    {
        var appointment = BookedWithOrder("order_16");
        appointment.AttachPaymentOrder("simulator", "order_17", Start.AddDays(-2).AddMinutes(5));
        var gateway = Gateway(new PaymentStatusResult("order_16", status, status == GatewayPaymentStatus.Pending ? "pay_16" : null, null, null, null));

        var taken = await Service(gateway).CheckReplacedOrderAsync(appointment, appointment.ReplacedOrders.Single(), TestContext.Current.CancellationToken);

        taken.Error!.Code.ShouldBe("payment.not_captured");
        appointment.PaymentStatus.ShouldBe(AppointmentPaymentStatus.Unpaid);
        appointment.ReplacedOrders.Single().GatewayPaymentId.ShouldBeNull();
    }

    [Fact]
    public async Task A_gateway_that_cannot_be_reached_records_nothing_about_a_replaced_order()
    {
        var appointment = BookedWithOrder("order_18");
        appointment.AttachPaymentOrder("simulator", "order_19", Start.AddDays(-2).AddMinutes(5));
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetStatusAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

        var taken = await Service(gateway).CheckReplacedOrderAsync(appointment, appointment.ReplacedOrders.Single(), TestContext.Current.CancellationToken);

        taken.Error!.Code.ShouldBe("payment.gateway_unavailable");
        appointment.PaymentStatus.ShouldBe(AppointmentPaymentStatus.Unpaid);
    }

    [Fact]
    public async Task A_second_payment_recorded_before_is_not_asked_about_again()
    {
        var appointment = BookedWithOrder("order_20");
        appointment.AttachPaymentOrder("simulator", "order_21", Start.AddDays(-2).AddMinutes(5));
        appointment.MarkPaidOnline("order_21", "pay_21", 600m, Start.AddDays(-2).AddMinutes(6));
        var replaced = appointment.ReplacedOrders.Single();
        appointment.TakeReplacedOrderPayment(replaced, "pay_20", 600m, Start.AddDays(-2).AddMinutes(7), Start.AddDays(-2).AddMinutes(8)).IsSuccess.ShouldBeTrue();
        var gateway = Substitute.For<IPaymentGateway>();

        var taken = await Service(gateway).CheckReplacedOrderAsync(appointment, replaced, TestContext.Current.CancellationToken);

        taken.Value.ShouldBe(ReplacedOrderPayment.AlreadyRecorded);
        await gateway.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    private static Appointment BookedWithOrder(string orderId)
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        appointment.AttachPaymentOrder("simulator", orderId, Start.AddDays(-2)).IsSuccess.ShouldBeTrue();
        return appointment;
    }

    private static IPaymentGateway Gateway(PaymentStatusResult status)
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetStatusAsync(status.OrderId, Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(status);
        return gateway;
    }

    // The database is used only to start a checkout, which these tests do not do.
    private static AppointmentPaymentService Service(IPaymentGateway gateway) =>
        new(null!, [gateway], new FixedTime(Start.AddDays(-1)), NullLogger<AppointmentPaymentService>.Instance);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
