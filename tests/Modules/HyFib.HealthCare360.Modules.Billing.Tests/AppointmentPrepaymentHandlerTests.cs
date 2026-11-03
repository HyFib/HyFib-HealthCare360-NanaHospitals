using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Billing.Services.EventHandlers;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Platform.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// Scheduling's prepayment events become Billing's outbox messages (OA-03): a booking's payment is receipted as an
/// advance, and a second payment, taken on an order the booking had before, is receipted with its refund asked for.
/// </summary>
public sealed class AppointmentPrepaymentHandlerTests
{
    private static readonly Guid AppointmentId = Guid.CreateVersion7();
    private static readonly DateTimeOffset PaidAt = TestData.Now.AddHours(-3);

    [Fact]
    public async Task A_second_payment_is_queued_as_a_second_payment_with_what_the_gateway_took()
    {
        var outbox = Substitute.For<IOutbox>();

        await new AppointmentPaidTwiceHandler(Advances(outbox)).HandleAsync(
            new AppointmentPaidTwice(AppointmentId, "NHAP260000042", TestData.PatientId, TestData.FacilityId, 550m, "razorpay", "order_A", "pay_A", PaidAt),
            TestContext.Current.CancellationToken);

        outbox.Received(1).Enqueue(
            AdvanceService.PrepaymentMessage,
            new AppointmentPrepayment(
                AppointmentId, "NHAP260000042", TestData.PatientId, TestData.FacilityId, 550m, "razorpay", "order_A", "pay_A", PaidAt, SecondPayment: true),
            TestData.FacilityId);
    }

    [Fact]
    public async Task The_bookings_own_payment_is_not_queued_as_a_second_one()
    {
        var outbox = Substitute.For<IOutbox>();

        await new AppointmentPaymentCapturedHandler(Advances(outbox)).HandleAsync(
            new AppointmentPaymentCaptured(
                AppointmentId, "NHAP260000042", TestData.PatientId, Guid.CreateVersion7(), TestData.FacilityId, 600m, "razorpay", "order_B", "pay_B", PaidAt),
            TestContext.Current.CancellationToken);

        outbox.Received(1).Enqueue(
            AdvanceService.PrepaymentMessage,
            new AppointmentPrepayment(AppointmentId, "NHAP260000042", TestData.PatientId, TestData.FacilityId, 600m, "razorpay", "order_B", "pay_B", PaidAt),
            TestData.FacilityId);
    }

    // Queueing uses the outbox alone.
    private static AdvanceService Advances(IOutbox outbox) => new(null!, null!, null!, outbox, null!, NullLogger<AdvanceService>.Instance);
}
