using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Schedules;
using HyFib.HealthCare360.Modules.Analytics.Services;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Scheduled report e-mails end to end (SRS BI-04): a schedule falls due, the job runs the report and renders the Excel
/// file, the e-mail handler hands a message with the file attached to the e-mail provider (the simulator) through the
/// outbox, a failed send is retried with back-off, and while it fails System health says so. The server sends nothing on
/// its own in the tests, so the job and each outbox message are run here, with the outbox's own retry policy.
/// </summary>
public sealed class ScheduledReportEmailTests(HospitalServer server)
{
    private const string Schedules = "/api/v1/analytics/schedules";
    private const string EmailSetting = "integrations.email";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_due_schedule_runs_renders_and_hands_the_mail_with_its_file_to_the_provider_retrying_a_failed_send()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var sysadmin = await server.SignInAsync(HospitalServer.Users.Admin);
        var emailBefore = await EmailSettingAsync(sysadmin);
        await using var putBack = new Cleanup(() => server.SaveSettingAsync(sysadmin, EmailSetting, new SaveSettingRequest(emailBefore?.DeepClone(), null, false, null)));

        // E-mail is switched off, so the first send fails as a provider that is down would.
        await server.SaveSettingAsync(sysadmin, EmailSetting, new SaveSettingRequest(JsonNode.Parse("""{"enabled":false}"""), null, false, null));

        var recipient = $"accounts-{Guid.NewGuid():N}@example.com";
        var schedule = await admin.PostAsync<ScheduleDto>(Schedules, new SaveScheduleRequest(
            "Monthly revenue " + Guid.NewGuid().ToString("N")[..6], "revenue-by-department", new() { ["period"] = "lastMonth" },
            ExportFormat.Xlsx, ScheduleFrequency.Monthly, "07:30", null, 1, [recipient]));
        await using var cleanup = new Cleanup(() => admin.SendRawAsync(HttpMethod.Delete, $"{Schedules}/{schedule.Id}?version={schedule.Version}"));
        schedule.NextRunAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow, "a new schedule waits for its first time");

        // The schedule falls due, and the job's minute comes round.
        await MakeDueAsync(schedule.Id);
        (await Job().RunDueAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);

        var delivery = (await admin.GetAsync<PagedResult<DeliveryDto>>($"{Schedules}/{schedule.Id}/deliveries")).Items.ShouldHaveSingleItem();
        delivery.Trigger.ShouldBe(DeliveryTrigger.Scheduled);
        delivery.Status.ShouldBe(DeliveryStatus.Ready, delivery.Error);
        delivery.Format.ShouldBe(ExportFormat.Xlsx);
        delivery.FileName.ShouldNotBeNull().ShouldStartWith("revenue-by-department_");
        delivery.FileName.ShouldEndWith(".xlsx");
        delivery.SizeBytes.ShouldBeGreaterThan(0);
        delivery.FileAvailable.ShouldBeTrue();
        var queued = delivery.Recipients.ShouldHaveSingleItem();
        queued.Email.ShouldBe(recipient);
        queued.Status.ShouldBe(RecipientStatus.Pending);

        var rescheduled = await admin.GetAsync<ScheduleDto>($"{Schedules}/{schedule.Id}");
        rescheduled.LastStatus.ShouldBe(ScheduleRunStatus.Succeeded);
        rescheduled.NextRunAt.ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow, "the run moved the schedule to its next time");

        // The rendered file is a real workbook (a zip), downloadable again from the delivery history.
        using (var file = await admin.SendRawAsync(HttpMethod.Get, $"/api/v1/analytics/deliveries/{delivery.Id}/file"))
        {
            file.IsSuccessStatusCode.ShouldBeTrue();
            var bytes = await file.Content.ReadAsByteArrayAsync(Ct);
            bytes.Length.ShouldBe((int)delivery.SizeBytes);
            bytes[..2].ShouldBe("PK"u8.ToArray());
        }

        var (recipientId, messageId) = await QueuedMessageAsync(delivery.Id);

        // First attempt: the provider refuses; the outbox keeps the message and tries again later.
        (await DispatchAsync(messageId)).ShouldBeFalse();
        var afterFailure = await MessageAsync(messageId);
        afterFailure.Status.ShouldBe(OutboxStatus.Pending);
        afterFailure.Attempts.ShouldBe(1);
        afterFailure.LastError.ShouldNotBeNullOrWhiteSpace();
        afterFailure.NextAttemptAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow, "retried with back-off, not at once");
        var failing = (await admin.GetAsync<PagedResult<DeliveryDto>>($"{Schedules}/{schedule.Id}/deliveries")).Items.Single().Recipients.Single();
        failing.Status.ShouldBe(RecipientStatus.Pending);
        failing.Attempts.ShouldBe(1);
        failing.LastError.ShouldNotBeNullOrWhiteSpace();

        // System health says so while it fails.
        var warning = (await sysadmin.GetAsync<SystemHealthReport>("/api/v1/admin/system/health")).Warnings.Single(w => w.Code == ReportEmailHealthWarnings.Code);
        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldContain("not sent yet after 1 attempt(s)");

        // The provider is back (the e-mail simulator); the retry falls due and sends the message with the file attached.
        await server.SaveSettingAsync(sysadmin, EmailSetting, new SaveSettingRequest(JsonNode.Parse("""{"enabled":true,"provider":"simulator"}"""), null, false, null));
        await MakeMessageDueAsync(messageId);
        (await DispatchAsync(messageId)).ShouldBeTrue();
        var sent = await MessageAsync(messageId);
        sent.Status.ShouldBe(OutboxStatus.Succeeded);
        sent.Attempts.ShouldBe(2);

        var delivered = (await admin.GetAsync<PagedResult<DeliveryDto>>($"{Schedules}/{schedule.Id}/deliveries")).Items.Single();
        delivered.Sent.ShouldBe(1);
        delivered.Pending.ShouldBe(0);
        delivered.Recipients.Single().Status.ShouldBe(RecipientStatus.Sent);
        delivered.Recipients.Single().Attempts.ShouldBe(2);

        var mail = (await sysadmin.GetAsync<PagedResult<SimulatorMessageDto>>(
            $"/api/v1/admin/integrations/simulator/messages?channel=Email&search=analytics-{recipientId:N}")).Items.ShouldHaveSingleItem();
        mail.Reference.ShouldBe($"analytics-{recipientId:N}");
        mail.Subject.ShouldNotBeNull().ShouldContain("Revenue by department");
        mail.Recipient.ShouldNotBe(recipient, "the simulator keeps the address masked");
        mail.Details.ShouldNotBeNull().ShouldContain($"{delivery.FileName} ({delivery.SizeBytes} bytes)");

        // Sending it again does nothing: the recipient was sent to once.
        await MakeMessageDueAsync(messageId, reopen: true);
        (await DispatchAsync(messageId)).ShouldBeTrue();
        (await sysadmin.GetAsync<PagedResult<SimulatorMessageDto>>(
            $"/api/v1/admin/integrations/simulator/messages?channel=Email&search=analytics-{recipientId:N}")).Items.Count.ShouldBe(1);
    }

    private ScheduledReportJob Job() => server.Services.GetServices<IHostedService>().OfType<ScheduledReportJob>().Single();

    private async Task MakeDueAsync(Guid scheduleId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var due = DateTimeOffset.UtcNow.AddMinutes(-1);
        (await db.Set<ReportSchedule>().IgnoreQueryFilters().Where(s => s.Id == scheduleId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.NextRunAt, due), Ct)).ShouldBe(1);
    }

    private async Task<(Guid RecipientId, Guid MessageId)> QueuedMessageAsync(Guid deliveryId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var recipientId = await db.Set<ReportDeliveryRecipient>().Where(r => r.DeliveryId == deliveryId).Select(r => r.Id).SingleAsync(Ct);
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == ScheduledReportEmailHandler.MessageType && m.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-10))
            .ToListAsync(Ct);
        var message = messages.Single(m => m.Read<ScheduledReportEmail>().RecipientId == recipientId);
        message.Status.ShouldBe(OutboxStatus.Pending);
        return (recipientId, message.Id);
    }

    private async Task<OutboxMessage> MessageAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking().SingleAsync(m => m.Id == id, Ct);
    }

    private async Task MakeMessageDueAsync(Guid id, bool reopen = false)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        await db.Set<OutboxMessage>().Where(m => m.Id == id).ExecuteUpdateAsync(
            u => u.SetProperty(m => m.NextAttemptAt, now).SetProperty(m => m.Status, m => reopen ? OutboxStatus.Pending : m.Status), Ct);
    }

    /// <summary>
    /// One outbox message delivered as the dispatcher delivers it (its handler in a scope of its own, success or a retry
    /// by <see cref="OutboxDispatcher"/>'s own policy), without touching the rest of the shared server's outbox.
    /// </summary>
    private async Task<bool> DispatchAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().Single(h => h.Type == ScheduledReportEmailHandler.MessageType);
        var message = await db.Set<OutboxMessage>().SingleAsync(m => m.Id == id, Ct);
        message.Status.ShouldBe(OutboxStatus.Pending);
        message.NextAttemptAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow, "only a due message is sent");
        var now = DateTimeOffset.UtcNow;
        bool succeeded;
        try
        {
            await handler.HandleAsync(message, Ct);
            message.Status = OutboxStatus.Succeeded;
            message.ProcessedAt = now;
            message.Attempts++;
            message.LastError = null;
            succeeded = true;
        }
        catch (InvalidOperationException ex)
        {
            OutboxDispatcher.Fail(message, now, ex.Message, permanent: false, TimeSpan.Zero);
            succeeded = false;
        }

        await db.SaveChangesAsync(Ct);
        return succeeded;
    }

    private static async Task<JsonNode?> EmailSettingAsync(ApiClient admin) =>
        (await admin.GetAsync<List<SettingDto>>("/api/v1/admin/settings?prefix=integrations.email"))
        .FirstOrDefault(s => s.Key == EmailSetting && s.FacilityId is null)?.Value;

    private sealed class Cleanup(Func<Task> action) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await action();
    }
}
