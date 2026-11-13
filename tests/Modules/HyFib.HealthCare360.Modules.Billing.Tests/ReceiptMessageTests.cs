using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Billing.Tests;

/// <summary>
/// A receipt goes by SMS, and on WhatsApp too unless the payment settles a bill that goes there itself: the patient gets
/// one WhatsApp message for a payment, not two.
/// </summary>
public sealed class ReceiptMessageTests
{
    [Fact]
    public void A_payment_that_leaves_something_due_sends_its_receipt_by_sms_and_on_whatsapp() =>
        ReceiptService.ReceiptChannels(billOnWhatsApp: false).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp]);

    [Fact]
    public void A_payment_whose_bill_goes_on_whatsapp_sends_its_receipt_by_sms_alone() =>
        ReceiptService.ReceiptChannels(billOnWhatsApp: true).ShouldBe([NotificationChannel.Sms]);
}
