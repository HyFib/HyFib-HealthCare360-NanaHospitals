using System.Diagnostics;
using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Seed;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// <c>loadtest patients --count N</c>: adds synthetic patients for a load test (<see cref="LoadTestPatients"/>), on a test
/// server set up with demonstration data. The load test itself is <c>tools/HyFib.HealthCare360.LoadTest</c>
/// (docs/testing/LOAD-TESTS.md).
/// </summary>
internal static class LoadTestCli
{
    public static async Task<int> AddPatientsAsync(string count)
    {
        if (!long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            await Console.Error.WriteLineAsync($"'{count}' is not a number of patients.");
            return 2;
        }

        await using var app = CatalogueCli.BuildApplication();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        try
        {
            if ((await db.Database.GetPendingMigrationsAsync()).Any())
            {
                await Console.Error.WriteLineAsync(
                    "The database is not up to date with this version of HC360. Start the HC360 service once (it updates the database), then run this again.");
                return 3;
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"The HC360 database could not be reached: {ex.Message}");
            return 3;
        }

        var stopwatch = Stopwatch.StartNew();
        var progress = new ConsoleProgress(total =>
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {total:N0} synthetic patients ({stopwatch.Elapsed:mm\\:ss})")));
        var result = await scope.ServiceProvider.GetRequiredService<LoadTestPatients>().AddAsync(number, progress, CancellationToken.None);
        if (result.IsFailure)
        {
            await Console.Error.WriteLineAsync(result.Error!.Message);
            return 1;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Added {number:N0} synthetic patients in {stopwatch.Elapsed:mm\\:ss}; there are {result.Value:N0} now (UHIDs {LoadTestPatients.UhidPrefix}…)."));
        return 0;
    }

    /// <summary>Reports at once, in order: <see cref="Progress{T}"/> would report later, on another thread.</summary>
    private sealed class ConsoleProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
