using HyFib.HealthCare360.Modules.Scheduling.Domain;
using static HyFib.HealthCare360.Modules.Scheduling.Tests.TestSchedules;

namespace HyFib.HealthCare360.Modules.Scheduling.Tests;

/// <summary>
/// A patient who starts a booking's online payment again (OA-03): the order it replaces is remembered, and a payment
/// taken on it later pays the booking when nothing else has, or is a second payment, raised for Billing to keep as an
/// advance and refund.
/// </summary>
public sealed class ReplacedPaymentOrderTests
{
    private static readonly DateTimeOffset Start = Ist(Monday, 10, 0);
    private static readonly DateTimeOffset FirstTry = Start.AddDays(-2);
    private static readonly DateTimeOffset SecondTry = FirstTry.AddMinutes(10);

    [Fact]
    public void Starting_the_payment_again_remembers_the_order_it_replaces()
    {
        var booking = Booked(Start, AppointmentChannel.Portal);
        booking.AttachPaymentOrder("razorpay", "order_A", FirstTry).IsSuccess.ShouldBeTrue();
        booking.ReplacedOrders.ShouldBeEmpty("The first order replaces nothing.");

        booking.AttachPaymentOrder("razorpay", "order_B", SecondTry).IsSuccess.ShouldBeTrue();

        booking.GatewayOrderId.ShouldBe("order_B");
        booking.ReplacedOrders.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            o => o.OrderId.ShouldBe("order_A"),
            o => o.AppointmentId.ShouldBe(booking.Id),
            o => o.FacilityId.ShouldBe(booking.FacilityId),
            o => o.Provider.ShouldBe("razorpay"),
            o => o.ReplacedAt.ShouldBe(SecondTry),
            o => o.GatewayPaymentId.ShouldBeNull());
    }

    [Fact]
    public void The_same_order_attached_again_replaces_nothing()
    {
        var booking = Booked(Start, AppointmentChannel.Portal);
        booking.AttachPaymentOrder("razorpay", "order_A", FirstTry);

        booking.AttachPaymentOrder("razorpay", "order_A", SecondTry).IsSuccess.ShouldBeTrue();

        booking.ReplacedOrders.ShouldBeEmpty();
    }

    [Fact]
    public void A_payment_on_the_replaced_order_pays_a_booking_nothing_else_has_paid()
    {
        var booking = Restarted();
        var first = booking.ReplacedOrders.Single();
        booking.ClearDomainEvents();

        var taken = booking.TakeReplacedOrderPayment(first, "pay_A", 600m, SecondTry.AddMinutes(5), SecondTry.AddMinutes(6));

        taken.Value.ShouldBe(ReplacedOrderPayment.PaidTheBooking);
        booking.ShouldSatisfyAllConditions(
            b => b.PaymentStatus.ShouldBe(AppointmentPaymentStatus.PaidOnline),
            b => b.GatewayOrderId.ShouldBe("order_A"),
            b => b.GatewayPaymentId.ShouldBe("pay_A"),
            b => b.AmountPaid.ShouldBe(600m),
            b => b.PaidAt.ShouldBe(SecondTry.AddMinutes(5), "Paid when the gateway took the money."),
            b => b.Status.ShouldBe(AppointmentStatus.Confirmed));

        // The order that had replaced it can still be paid, so it is remembered in its place.
        booking.ReplacedOrders.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            o => o.OrderId.ShouldBe("order_B"),
            o => o.ReplacedAt.ShouldBe(SecondTry.AddMinutes(6)),
            o => o.GatewayPaymentId.ShouldBeNull());
        booking.DomainEvents.OfType<AppointmentPaymentCaptured>().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.GatewayOrderId.ShouldBe("order_A"),
            e => e.GatewayPaymentId.ShouldBe("pay_A"));
        booking.DomainEvents.OfType<AppointmentPaidTwice>().ShouldBeEmpty();
    }

    [Fact]
    public void A_payment_on_the_replaced_order_of_a_booking_already_paid_is_a_second_payment()
    {
        var booking = Restarted();
        booking.MarkPaidOnline("order_B", "pay_B", 600m, SecondTry.AddMinutes(2)).IsSuccess.ShouldBeTrue();
        var first = booking.ReplacedOrders.Single();
        booking.ClearDomainEvents();

        var taken = booking.TakeReplacedOrderPayment(first, "pay_A", 600m, SecondTry.AddMinutes(9), SecondTry.AddMinutes(10));

        taken.Value.ShouldBe(ReplacedOrderPayment.PaidTwice);
        booking.GatewayPaymentId.ShouldBe("pay_B", "The booking keeps the payment that paid it.");
        booking.GatewayOrderId.ShouldBe("order_B");
        first.ShouldSatisfyAllConditions(
            o => o.GatewayPaymentId.ShouldBe("pay_A"),
            o => o.AmountPaid.ShouldBe(600m),
            o => o.PaidAt.ShouldBe(SecondTry.AddMinutes(9)));
        booking.DomainEvents.OfType<AppointmentPaidTwice>().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.AppointmentId.ShouldBe(booking.Id),
            e => e.AppointmentNumber.ShouldBe(booking.Number),
            e => e.PatientId.ShouldBe(booking.PatientId),
            e => e.FacilityId.ShouldBe(booking.FacilityId),
            e => e.Amount.ShouldBe(600m),
            e => e.Provider.ShouldBe("razorpay"),
            e => e.GatewayOrderId.ShouldBe("order_A"),
            e => e.GatewayPaymentId.ShouldBe("pay_A"),
            e => e.PaidAt.ShouldBe(SecondTry.AddMinutes(9)));
        booking.DomainEvents.OfType<AppointmentPaymentCaptured>().ShouldBeEmpty();
    }

    [Fact]
    public void Once_the_first_order_paid_the_booking_paying_the_one_that_replaced_it_is_a_second_payment()
    {
        var booking = Restarted();
        booking.TakeReplacedOrderPayment(booking.ReplacedOrders.Single(), "pay_A", 600m, SecondTry.AddMinutes(5), SecondTry.AddMinutes(6));
        var second = booking.ReplacedOrders.Single();
        booking.ClearDomainEvents();

        booking.TakeReplacedOrderPayment(second, "pay_B", 600m, SecondTry.AddMinutes(7), SecondTry.AddMinutes(8)).Value
            .ShouldBe(ReplacedOrderPayment.PaidTwice);

        booking.GatewayPaymentId.ShouldBe("pay_A");
        booking.DomainEvents.OfType<AppointmentPaidTwice>().ShouldHaveSingleItem().GatewayOrderId.ShouldBe("order_B");
    }

    [Fact]
    public void Recording_a_second_payment_again_changes_nothing_and_another_on_the_same_order_is_refused()
    {
        var booking = Restarted();
        booking.MarkPaidOnline("order_B", "pay_B", 600m, SecondTry.AddMinutes(2));
        var first = booking.ReplacedOrders.Single();
        booking.TakeReplacedOrderPayment(first, "pay_A", 600m, SecondTry.AddMinutes(9), SecondTry.AddMinutes(10));
        booking.ClearDomainEvents();

        booking.TakeReplacedOrderPayment(first, "pay_A", 600m, SecondTry.AddMinutes(9), SecondTry.AddHours(1)).Value
            .ShouldBe(ReplacedOrderPayment.AlreadyRecorded);
        booking.TakeReplacedOrderPayment(first, "pay_X", 600m, SecondTry.AddMinutes(11), SecondTry.AddHours(1)).Error!.Code
            .ShouldBe("payment.order_paid");

        booking.DomainEvents.ShouldBeEmpty();
        first.GatewayPaymentId.ShouldBe("pay_A");
    }

    [Fact]
    public void The_payment_the_booking_already_holds_is_not_counted_as_a_second_one()
    {
        var booking = Restarted();
        booking.MarkPaidOnline("order_B", "pay_B", 600m, SecondTry.AddMinutes(2));
        var first = booking.ReplacedOrders.Single();
        booking.ClearDomainEvents();

        booking.TakeReplacedOrderPayment(first, "pay_B", 600m, SecondTry.AddMinutes(2), SecondTry.AddHours(1)).Value
            .ShouldBe(ReplacedOrderPayment.AlreadyRecorded);

        first.GatewayPaymentId.ShouldBeNull();
        booking.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void A_payment_on_another_bookings_order_is_refused()
    {
        var booking = Restarted();
        var other = Restarted("NHAP260000002");

        booking.TakeReplacedOrderPayment(other.ReplacedOrders.Single(), "pay_A", 600m, SecondTry, SecondTry).Error!.Code
            .ShouldBe("payment.order_mismatch");

        booking.PaymentStatus.ShouldBe(AppointmentPaymentStatus.Unpaid);
        booking.DomainEvents.OfType<AppointmentPaymentCaptured>().ShouldBeEmpty();
    }

    [Fact]
    public void A_cancelled_booking_paid_on_its_replaced_order_records_the_money_for_billing_to_refund()
    {
        var booking = Restarted();
        booking.Cancel(CancellationReason.PatientRequest, null, CancellationSource.Patient, null, SecondTry.AddMinutes(3)).IsSuccess.ShouldBeTrue();
        booking.ClearDomainEvents();

        booking.TakeReplacedOrderPayment(booking.ReplacedOrders.Single(), "pay_A", 600m, SecondTry.AddMinutes(5), SecondTry.AddMinutes(6)).Value
            .ShouldBe(ReplacedOrderPayment.PaidTheBooking);

        booking.Status.ShouldBe(AppointmentStatus.Cancelled, "Money taken does not reopen a cancelled booking.");
        booking.PaymentStatus.ShouldBe(AppointmentPaymentStatus.PaidOnline);
        booking.DomainEvents.OfType<AppointmentPaymentCaptured>().ShouldHaveSingleItem().GatewayPaymentId.ShouldBe("pay_A");
    }

    /// <summary>A portal booking whose patient started the payment (order A), then started it again (order B).</summary>
    private static Appointment Restarted(string number = "NHAP260000001")
    {
        var booking = Booked(Start, AppointmentChannel.Portal, number: number);
        booking.AttachPaymentOrder("razorpay", "order_A", FirstTry).IsSuccess.ShouldBeTrue();
        booking.AttachPaymentOrder("razorpay", "order_B", SecondTry).IsSuccess.ShouldBeTrue();
        return booking;
    }
}
