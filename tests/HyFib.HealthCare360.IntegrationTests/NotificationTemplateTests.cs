using System.Net;
using HyFib.HealthCare360.Platform.Notifications;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Notification templates over the real API: an update keeps the template's channel, so its rules (the SMS limit) apply
/// whatever channel the request names. The template made here has a key of its own.
/// </summary>
public sealed class NotificationTemplateTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_sms_template_keeps_the_sms_limit_when_an_update_names_another_channel()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var key = "test.limit_" + Guid.NewGuid().ToString("N")[..8];
        var created = await admin.PostAsync<Created>("/api/v1/admin/notification-templates", new SaveNotificationTemplateRequest(
            key, NotificationChannel.Sms, "en", null, null, "Dear {{name}}, your appointment is confirmed.", null, null, null, true, null));

        using var response = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/notification-templates/{created.Id}", new SaveNotificationTemplateRequest(
            key, NotificationChannel.Email, "en", null, null, new string('a', 1500), null, null, null, true, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("SMS templates must be at most 1000 characters");
    }

    private sealed record Created(Guid Id);
}
