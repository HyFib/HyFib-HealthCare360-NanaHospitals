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

    [Theory]
    [InlineData(GatewayPaymentStatus.Created)]
    [InlineData(GatewayPaymentStatus.Pending)]
    [InlineData(GatewayPaymentStatus.Failed)]
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
