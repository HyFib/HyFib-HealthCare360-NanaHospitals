using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.Modules.Diagnostics.Tests;

/// <summary>
/// The "report ready" notice (<see cref="ReportDelivery"/>): the report's PDF goes to the patient's mobile number only once
/// the number is verified, and to the patient's e-mail address as before.
/// </summary>
public sealed class ReportDeliveryTests
{
    private static readonly NotificationDocument Pdf = new("diagnostics.lab-report", Guid.CreateVersion7());

    [Fact]
    public void To_an_unverified_number_the_notice_goes_without_the_pdf_and_the_email_keeps_it()
    {
        var messages = ReportDelivery.Messages(toNumber: true, toEmail: true, Pdf, numberVerified: false);

        messages.Count.ShouldBe(2);
        messages[0].Channels.ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp]);
        messages[0].Document.ShouldBeNull("a digit typed wrong at the desk would send the report to a stranger");
        messages[1].Channels.ShouldBe([NotificationChannel.Email]);
        messages[1].Document.ShouldBe(Pdf);
    }

    [Fact]
    public void To_an_unverified_number_alone_the_notice_goes_without_the_pdf()
    {
        var message = ReportDelivery.Messages(toNumber: true, toEmail: false, Pdf, numberVerified: false).ShouldHaveSingleItem();

        message.Channels.ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp]);
        message.Document.ShouldBeNull();
    }

    [Fact]
    public void To_a_verified_number_the_pdf_goes_with_the_whatsapp_message_and_the_email_as_before()
    {
        var message = ReportDelivery.Messages(toNumber: true, toEmail: true, Pdf, numberVerified: true).ShouldHaveSingleItem();

        message.Channels.ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Email]);
        message.Document.ShouldBe(Pdf);
    }

    [Fact]
    public void With_no_number_to_send_to_the_email_carries_the_pdf()
    {
        // A protected record on a shared number gets no SMS or WhatsApp: the e-mail to the patient's own address still goes.
        var message = ReportDelivery.Messages(toNumber: false, toEmail: true, Pdf, numberVerified: false).ShouldHaveSingleItem();

        message.Channels.ShouldBe([NotificationChannel.Email]);
        message.Document.ShouldBe(Pdf);
    }

    [Fact]
    public void With_pdfs_switched_off_nothing_carries_one()
    {
        var message = ReportDelivery.Messages(toNumber: true, toEmail: true, null, numberVerified: true).ShouldHaveSingleItem();

        message.Channels.ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Email]);
        message.Document.ShouldBeNull();
    }
}
