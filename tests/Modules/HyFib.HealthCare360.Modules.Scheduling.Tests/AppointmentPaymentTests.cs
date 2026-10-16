using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Services;
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
        await gateway.Received(1).GetStatusAsync("order_1", appointment.FacilityId, Arg.Any<CancellationToken>());
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
        gateway.VerifySignature("order_7|pay_7", "sig", appointment.FacilityId).Returns(true);

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

    [Fact]
    public async Task An_unreachable_gateway_asks_the_patient_to_try_again()
    {
        var appointment = BookedWithOrder("order_3");
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetStatusAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

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
        await gateway.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Repeating_the_check_after_payment_is_harmless()
    {
        var appointment = BookedWithOrder("order_4");
        appointment.MarkPaidOnline("order_4", "pay_4", 600m, Start.AddDays(-1)).IsSuccess.ShouldBeTrue();
        var gateway = Substitute.For<IPaymentGateway>();

        var result = await Service(gateway).VerifyWithGatewayAsync(appointment, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await gateway.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
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
        gateway.GetStatusAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("offline"));

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

        await gateway.DidNotReceive().GetStatusAsync(Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    private static Appointment BookedWithOrder(string orderId)
    {
        var appointment = Booked(Start, AppointmentChannel.Portal);
        appointment.AttachPaymentOrder("simulator", orderId).IsSuccess.ShouldBeTrue();
        return appointment;
    }

    private static IPaymentGateway Gateway(PaymentStatusResult status)
    {
        var gateway = Substitute.For<IPaymentGateway>();
        gateway.GetStatusAsync(status.OrderId, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(status);
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
