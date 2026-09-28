using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HyFib.HealthCare360.Modules.SupportServices.Services;

/// <summary>
/// Daily meal-plan generation for every facility: today's plan (with diet charges when enabled) and tomorrow's, so the
/// kitchen can prepare the early-morning round. It runs at start-up and hourly; generation is idempotent, so each run
/// only picks up diet changes, admissions and discharges since the last one.
/// </summary>
internal sealed class DailyMealPlanJob(IServiceScopeFactory scopeFactory, IOptions<PlatformOptions> options, TimeProvider time, ILogger<DailyMealPlanJob> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.RunsJobs)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            var today = IndiaClock.LocalDate(time.GetUtcNow());
            foreach (var date in new[] { today, today.AddDays(1) })
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<MealPlanService>().GenerateAsync(date, stoppingToken);
                    if (result.MealsAdded > 0 || result.MealsCancelled > 0 || result.ChargesPosted > 0)
                    {
                        logger.LogInformation("Meal plan {Date}: {Added} added, {Cancelled} cancelled, {Charges} diet charges posted",
                            date, result.MealsAdded, result.MealsCancelled, result.ChargesPosted);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Meal plan generation for {Date} failed", date);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
