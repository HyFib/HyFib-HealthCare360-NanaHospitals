using System.Globalization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.Extensions.Logging;
using Quartz;

namespace HyFib.HealthCare360.Platform.Jobs;

/// <summary>
/// Each time Quartz fires a job at a set time (the nightly backup at 02:00), the hospital claims that time once, for every
/// server (<see cref="IClusterStore.TryAddAsync"/>, keyed by the trigger and the time it was due), and the job runs only
/// on the server that claimed it. One server holds the job lease at a time (<see cref="Hosting.JobLeadership"/>), so this
/// only matters when that fails: two servers that both think they run the jobs, or one that takes over just as the last
/// one fired, run each due time once between them. A fire that cannot be claimed because the database cannot be reached
/// does not run either: the job would have nothing to work on.
/// </summary>
internal sealed class JobSlotClaim(IClusterStore store, ILogger<JobSlotClaim> logger) : ITriggerListener
{
    /// <summary>How long a claim is kept: longer than any job's time between two fires could be mistaken for another.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromDays(2);

    public string Name => nameof(JobSlotClaim);

    /// <summary>The key a due time is claimed under.</summary>
    internal static string Key(TriggerKey trigger, DateTimeOffset due) =>
        string.Create(CultureInfo.InvariantCulture, $"job-slot:{trigger.Group}.{trigger.Name}:{due.UtcDateTime:yyyyMMddTHHmmssZ}");

    public async ValueTask<bool> VetoJobExecution(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(context);
        var due = context.ScheduledFireTimeUtc ?? context.FireTimeUtc;
        try
        {
            if (await store.TryAddAsync(Key(trigger.Key, due), Kept, cancellationToken))
            {
                return false;
            }

            logger.LogWarning("{Trigger} due at {Due:u} already ran on another server: not run again here", trigger.Key, due);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Trigger} due at {Due:u} could not be claimed for the hospital: not run", trigger.Key, due);
            return true;
        }
    }

    public ValueTask TriggerFired(ITrigger trigger, IJobExecutionContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask TriggerMisfired(ITrigger trigger, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask TriggerComplete(
        ITrigger trigger, IJobExecutionContext context, SchedulerInstruction triggerInstructionCode, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
