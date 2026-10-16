using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.Billing.Services;

/// <summary>Per-facility settings of the terminal sale re-check (<c>billing.pos.recheck</c>).</summary>
public sealed record PosRecheckSettings
{
    public const string Key = "billing.pos.recheck";

    /// <summary>A sale still waiting for the customer after this many minutes is cancelled on the terminal and marked timed out.</summary>
    public int TimeoutMinutes { get; init; } = 15;

    public int EffectiveTimeoutMinutes => Math.Clamp(TimeoutMinutes, 2, 240);

    public static async Task<PosRecheckSettings> ReadAsync(ISettingsService settings, Guid facilityId, CancellationToken ct)
    {
        try
        {
            return await settings.GetAsync<PosRecheckSettings>(Key, facilityId, ct) ?? new PosRecheckSettings();
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed setting must not stop the re-check: the defaults apply until it is corrected.
            return new PosRecheckSettings();
        }
    }
}

/// <summary>
/// Terminal sales the counter stopped polling (INT-POS): the screen was closed, the browser crashed or the cashier
/// moved on while the customer was still paying. Each sale still open, or approved without its receipt, is asked about
/// again; an approval is recorded once, and a sale nobody paid within the facility's timeout is cancelled on the terminal
/// and marked timed out. Background work has no signed-in facility, so every sale is read across facilities and asked
/// about with its own facility's terminal settings.
/// </summary>
public sealed class PosRecheckRunner(
    HospitalDbContext db,
    PosService pos,
    ISettingsService settings,
    TimeProvider time,
    ILogger<PosRecheckRunner> logger)
{
    /// <summary>The counter polls a fresh sale every few seconds; the job leaves it to the counter for this long.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    /// <summary>Sales older than this are no longer asked about: the acquirer's settlement report is the record then.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(2);

    private const int Batch = 200;

    /// <summary>Asks the terminal about the open sales due a check; returns how many were asked about.</summary>
    public async Task<int> RecheckSalesAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var timeouts = new Dictionary<Guid, TimeSpan>();
        var checkedCount = 0;
        foreach (var id in await DueSalesAsync(now, ct))
        {
            // Loaded one at a time: a sale that fails clears the unit of work, and must not take the others with it.
            var sale = await db.Set<PosTransaction>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (sale is null || (sale.IsComplete && (sale.Status != PosTransactionStatus.Approved || sale.ReceiptId is not null)))
            {
                continue;
            }

            if (!timeouts.TryGetValue(sale.FacilityId, out var timeout))
            {
                timeout = TimeSpan.FromMinutes((await PosRecheckSettings.ReadAsync(settings, sale.FacilityId, ct)).EffectiveTimeoutMinutes);
                timeouts[sale.FacilityId] = timeout;
            }

            checkedCount++;
            try
            {
                await pos.RecheckAsync(sale, timeout, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A concurrent poll from the counter, or the terminal not answering: the next run asks again.
                logger.LogWarning(ex, "Could not re-check terminal sale {TransactionId}", sale.TransactionId);
                db.ChangeTracker.Clear();
            }
        }

        return checkedCount;
    }

    /// <summary>Open sales started between <see cref="Window"/> and <see cref="Grace"/> ago, and approved ones without a receipt, oldest first.</summary>
    internal async Task<IReadOnlyList<Guid>> DueSalesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var after = now - Window;
        var before = now - Grace;
        return await db.Set<PosTransaction>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(s => s.CreatedAt > after && s.CreatedAt <= before)
            .Where(s => s.Status == PosTransactionStatus.Initiated || s.Status == PosTransactionStatus.AwaitingCustomer
                        || (s.Status == PosTransactionStatus.Approved && s.ReceiptId == null))
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.Id)
            .Take(Batch)
            .ToListAsync(ct);
    }
}

/// <summary>
/// The terminal sale re-check (INT-POS): every minute it asks the terminal about sales the counter left open (see
/// <see cref="PosRecheckRunner"/>). Runs only on nodes that run jobs (<see cref="PlatformOptions.RunsJobs"/>); a sale's
/// receipt is recorded once whoever asks, so a second worker or the counter polling at the same time does no harm.
/// </summary>
internal sealed class PosRecheckJob(
    IServiceScopeFactory scopeFactory,
    IOptions<PlatformOptions> options,
    TimeProvider time,
    ILogger<PosRecheckJob> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PosRecheckRunner>().RecheckSalesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The terminal sale re-check failed; it runs again on the next tick");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
