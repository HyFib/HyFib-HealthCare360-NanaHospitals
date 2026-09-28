using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.HR.Services;

/// <summary>
/// Daily licence/certification expiry reminders (HR-08): 30 days, 7 days and on expiry. Each stage alerts once
/// (the stage is stored on the credential and committed before anyone is notified), HR gets one summary per
/// facility and each employee is told about their own credential. Respects <see cref="PlatformOptions.RunsJobs"/>.
/// </summary>
internal sealed class CredentialExpiryAlertJob(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    IOptions<PlatformOptions> options,
    ILogger<CredentialExpiryAlertJob> logger) : BackgroundService
{
    private static readonly TimeOnly RunAfter = new(8, 0);
    private const int MaxNamesInSummary = 10;
    private DateOnly? _lastRun;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30), time);
        do
        {
            var now = time.LocalNow();
            var today = DateOnly.FromDateTime(now);
            if (_lastRun == today || TimeOnly.FromDateTime(now) < RunAfter)
            {
                continue;
            }

            try
            {
                await RunAsync(today, stoppingToken);
                _lastRun = today;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Credential expiry reminders failed; they will be retried");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private sealed record DueCredential(Credential Credential, CredentialExpiryStage Stage, string EmployeeName, string EmployeeCode, Guid? UserId, Guid FacilityId);

    private async Task RunAsync(DateOnly today, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        List<DueCredential> due;
        await using (var transaction = await JobLock.TryAcquireAsync(db, JobLock.CredentialExpiry, ct))
        {
            if (transaction is null)
            {
                return;
            }

            var horizon = today.AddDays(CredentialExpiryPolicy.FirstReminderDays);
            var rows = await (
                    from c in db.Set<Credential>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
                    join e in db.Set<Employee>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]) on c.EmployeeId equals e.Id
                    where e.Status != EmployeeStatus.Separated && c.ExpiresOn != null && c.ExpiresOn <= horizon
                    select new { Credential = c, e.FullName, e.EmployeeCode, e.UserId, e.FacilityId })
                .ToListAsync(ct);

            due = rows
                .Select(r => new DueCredential(r.Credential, CredentialExpiryPolicy.StageFor(r.Credential.ExpiresOn, today), r.FullName, r.EmployeeCode, r.UserId, r.FacilityId))
                .Where(r => CredentialExpiryPolicy.ShouldAlert(r.Stage, r.Credential.LastAlertStage))
                .ToList();
            foreach (var item in due)
            {
                item.Credential.MarkAlerted(item.Stage, today);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        foreach (var item in due.Where(d => d.UserId is not null))
        {
            await notifications.NotifyUsersAsync([item.UserId!.Value], Title(item), Describe(item), "/hr/me/credentials", "hr",
                item.Stage == CredentialExpiryStage.DueIn30Days ? "info" : "warning", ct);
        }

        foreach (var facility in due.GroupBy(d => d.FacilityId))
        {
            var items = facility.OrderBy(d => d.Credential.ExpiresOn).ToList();
            var expired = items.Count(d => d.Stage == CredentialExpiryStage.Expired);
            var body = string.Join("; ", items.Take(MaxNamesInSummary).Select(Describe))
                       + (items.Count > MaxNamesInSummary ? $"; and {items.Count - MaxNamesInSummary} more." : ".");
            await notifications.NotifyPermissionHoldersAsync(HRPermissions.CredentialsManage, facility.Key,
                expired > 0 ? $"{expired} staff credential(s) have expired" : $"{items.Count} staff credential(s) expire soon",
                body, "/hr/credentials?expiringWithinDays=30", "hr", expired > 0 ? "critical" : "warning", ct);
        }

        if (due.Count > 0)
        {
            logger.LogInformation("Sent {Count} credential expiry reminder(s)", due.Count);
        }
    }

    private static string Title(DueCredential item) => item.Stage switch
    {
        CredentialExpiryStage.Expired => $"Your {item.Credential.Name} has expired",
        _ => $"Your {item.Credential.Name} expires on {item.Credential.ExpiresOn:dd MMM yyyy}",
    };

    private static string Describe(DueCredential item) => item.Stage == CredentialExpiryStage.Expired
        ? $"{item.EmployeeName} ({item.EmployeeCode}) — {item.Credential.Name} expired on {item.Credential.ExpiresOn:dd MMM yyyy}"
        : $"{item.EmployeeName} ({item.EmployeeCode}) — {item.Credential.Name} expires on {item.Credential.ExpiresOn:dd MMM yyyy}";
}
