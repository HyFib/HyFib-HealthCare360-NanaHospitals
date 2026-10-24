using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The controlled-drug registers (D&amp;C Rules Schedule H1/X, NDPS) are append-only in the database: even its owner cannot
/// delete a line, or change one but for the patient link a merge of duplicate records moves; and each line is in the
/// sealed audit trail, so a line changed with the triggers turned off no longer matches its copy there.
/// </summary>
public sealed class ControlledDrugRegisterGuardTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_register_line_cannot_be_changed_or_deleted_and_is_in_the_audit_trail()
    {
        var line = new ControlledDrugRegisterEntry
        {
            FacilityId = Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            StoreId = Guid.CreateVersion7(),
            ItemId = Guid.CreateVersion7(),
            ItemName = "Morphine 10 mg injection",
            Schedule = DrugSchedule.X,
            IsControlled = true,
            BatchNumber = "MB2611",
            MovementType = StockMovementType.Issue,
            QuantityOut = 2,
            BalanceAfter = 18,
            PatientId = Guid.CreateVersion7(),
            PatientName = "Ravi Kumar",
            ReferenceModule = "pharmacy.sale",
            LedgerEntryId = Guid.CreateVersion7(),
        };
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Add(line);
            await db.SaveChangesAsync(Ct);
            (await db.Set<AuditEvent>().AsNoTracking()
                .AnyAsync(e => e.EntityType == nameof(ControlledDrugRegisterEntry) && e.EntityId == line.Id.ToString() && e.Action == "ControlledDrugRegisterEntry.create", Ct))
                .ShouldBeTrue("The line is in the audit trail.");
        }

        (await RefusalAsync($"UPDATE pharmacy.controlled_drug_register SET quantity_out = 1 WHERE id = '{line.Id}'"))
            .ShouldBe("pharmacy.controlled_drug_register is append-only: only a merge of patient records may move a line's patient link");
        (await RefusalAsync($"DELETE FROM pharmacy.controlled_drug_register WHERE id = '{line.Id}'"))
            .ShouldBe("pharmacy.controlled_drug_register is append-only: register lines are never deleted");
        (await RefusalAsync("SET LOCAL lock_timeout = '10s'", "TRUNCATE clinical.controlled_drug_register"))
            .ShouldBe("clinical.controlled_drug_register is append-only: register lines are never deleted");
        (await RefusalAsync($"UPDATE pharmacy.controlled_drug_register SET patient_id = '{Guid.CreateVersion7()}' WHERE id = '{line.Id}'"))
            .ShouldBeNull("A merge of duplicate records moves the patient link.");
    }

    /// <summary>Runs the statements as the database owner in a transaction that is always rolled back; returns why PostgreSQL refused one.</summary>
    private async Task<string?> RefusalAsync(params string[] statements)
    {
        await using var connection = new NpgsqlConnection(server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital"));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        try
        {
            foreach (var sql in statements)
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                await command.ExecuteNonQueryAsync(Ct);
            }

            return null;
        }
        catch (PostgresException refused)
        {
            return refused.MessageText;
        }
        finally
        {
            await transaction.RollbackAsync(Ct);
        }
    }
}
