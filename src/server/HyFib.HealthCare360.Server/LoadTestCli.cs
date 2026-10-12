using System.Diagnostics;
using System.Globalization;
using HyFib.HealthCare360.Modules.Patients.Seed;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HyFib.HealthCare360.Server;

/// <summary>
/// <c>loadtest patients --count N</c> and <c>loadtest accounts --count N</c>: the synthetic patients and the staff accounts
/// a load test needs (<see cref="LoadTestPatients"/>, <see cref="LoadTestAccounts"/>), on a test server set up with
/// demonstration data. The load test itself is <c>tools/HyFib.HealthCare360.LoadTest</c> (docs/testing/LOAD-TESTS.md).
/// </summary>
internal static class LoadTestCli
{
    /// <summary>The accounts' password, as the load test reads it too: never on the command line.</summary>
    public const string PasswordVariable = "HC360_LOADTEST_PASSWORD";

    public static async Task<int> AddPatientsAsync(string count)
    {
        if (!long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            await Console.Error.WriteLineAsync($"'{count}' is not a number of patients.");
            return 2;
        }

        await using var app = CatalogueCli.BuildApplication();
        await using var scope = app.Services.CreateAsyncScope();
        if (await DatabaseProblemAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>()) is { } problem)
        {
            await Console.Error.WriteLineAsync(problem);
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

    /// <summary>Makes the accounts and prints their names, separated by commas, for the load test's --accounts.</summary>
    public static async Task<int> AddAccountsAsync(string count)
    {
        if (!int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            await Console.Error.WriteLineAsync($"'{count}' is not a number of accounts.");
            return 2;
        }

        if (Environment.GetEnvironmentVariable(PasswordVariable) is not { Length: > 0 } password)
        {
            await Console.Error.WriteLineAsync($"Give the accounts' password in the {PasswordVariable} environment variable, never on the command line.");
            return 2;
        }

        await using var app = CatalogueCli.BuildApplication();
        await using var scope = app.Services.CreateAsyncScope();
        if (await DatabaseProblemAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>()) is { } problem)
        {
            await Console.Error.WriteLineAsync(problem);
            return 3;
        }

        var result = await scope.ServiceProvider.GetRequiredService<LoadTestAccounts>().EnsureAsync(number, password, CancellationToken.None);
        if (result.IsFailure)
        {
            await Console.Error.WriteLineAsync(result.Error!.Message);
            return 1;
        }

        Console.WriteLine(string.Join(',', result.Value));
        return 0;
    }

    /// <summary>Why the database cannot be used now, or null when it can.</summary>
    private static async Task<string?> DatabaseProblemAsync(HospitalDbContext db)
    {
        try
        {
            return (await db.Database.GetPendingMigrationsAsync()).Any()
                ? "The database is not up to date with this version of HC360. Start the HC360 service once (it updates the database), then run this again."
                : null;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return $"The HC360 database could not be reached: {ex.Message}";
        }
    }

    /// <summary>Reports at once, in order: <see cref="Progress{T}"/> would report later, on another thread.</summary>
    private sealed class ConsoleProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
