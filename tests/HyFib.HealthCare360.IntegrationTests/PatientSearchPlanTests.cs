using System.Data.Common;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS PERF-02: patient search within 500 ms over a million patients. Each kind of term the front desk types is matched
/// by an index, never by reading the whole register: over a million patients a scan takes about 150 ms on its own and
/// queues behind the others under load (docs/testing/LOAD-TESTS.md has the measured run). The test asks PostgreSQL for the
/// plan of the search the server runs, with sequential scans turned off and the index of deleted records set aside (the
/// test's register is small enough that PostgreSQL would rather read it all through that one): each column the search
/// looks in must be an index condition of the plan, whatever the size of the table.
/// </summary>
public sealed class PatientSearchPlanTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("LT000012", "a UHID, or the start of one", new[] { "uhid" })]
    [InlineData("9000673056", "a mobile number, the patient's or their alternate", new[] { "phone", "alternate_phone" })]
    [InlineData("673056", "the last digits of a UHID or mobile number", new[] { "uhid", "phone" })]
    [InlineData("Ravi Kumar", "words of the name", new[] { "full_name" })]
    public async Task A_search_is_matched_by_an_index(string term, string kind, string[] columns)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IFieldProtector>();
        await using var command = db.Set<Patient>().AsNoTracking().Search(term, protector).Select(p => p.Id).CreateDbCommand();

        var plan = await PlanAsync(db, command);

        plan.ShouldNotContain("Seq Scan", Case.Insensitive, $"Searching by {kind} reads every patient:\n{plan}");
        var matched = plan.Split('\n').Where(line => line.TrimStart().StartsWith("Index Cond:", StringComparison.Ordinal)).ToList();
        columns.Where(column => !matched.Any(line => line.Contains($"({column})::text", StringComparison.Ordinal)))
            .ShouldBeEmpty($"Searching by {kind} checks these columns row by row, with no index:\n{plan}");
    }

    /// <summary>
    /// The plan PostgreSQL would run <paramref name="command"/> with, sequential scans and the deleted-records index set
    /// aside for it alone (the transaction is rolled back).
    /// </summary>
    private static async Task<string> PlanAsync(HospitalDbContext db, DbCommand command)
    {
        await db.Database.OpenConnectionAsync(Ct);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '5s'", Ct);
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off", Ct);
        await db.Database.ExecuteSqlRawAsync("DROP INDEX patients.ix_patients_is_deleted", Ct);
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "EXPLAIN " + command.CommandText;
        var lines = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync(Ct);
        return string.Join('\n', lines);
    }
}
