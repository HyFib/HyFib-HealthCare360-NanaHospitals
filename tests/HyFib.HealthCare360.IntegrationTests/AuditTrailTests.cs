using System.Diagnostics;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The audit trail's tamper evidence (SRS SEC-04, ADR-010) in the real database. The triggers refuse every change to a
/// sealed record, a delete and a truncate, even from the database owner (the test server signs in as a superuser), and
/// a record waiting for its seal can only be sealed. The SHA-256 chain over the sealed records verifies, and a record
/// changed, moved or removed by someone who turned the triggers off breaks the chain where it was.
/// </summary>
public sealed class AuditTrailTests(HospitalServer server)
{
    // Inserted in a transaction that is rolled back, so the server's sealer never sees it.
    private const string Pending =
        "INSERT INTO audit.audit_events (occurred_at, category, action, severity, user_name, summary) " +
        "VALUES (now(), 'security', 'test.pending', 'Critical', 'nurse', 'Waiting for its seal')";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sealed_record_cannot_be_changed_deleted_or_truncated_even_by_the_database_owner()
    {
        var id = (await RecordAndSealAsync(1))[0].Id;

        (await RefusalAsync($"UPDATE audit.audit_events SET summary = 'Nothing happened' WHERE id = {id}"))
            .ShouldBe("sealed audit events cannot be modified");
        (await RefusalAsync($"UPDATE audit.audit_events SET hash = NULL, chain_index = NULL, previous_hash = NULL WHERE id = {id}"))
            .ShouldBe("sealed audit events cannot be modified", "Not even to seal it again.");
        (await RefusalAsync($"DELETE FROM audit.audit_events WHERE id = {id}")).ShouldBe("audit events are append-only");
        (await RefusalAsync("SET LOCAL lock_timeout = '10s'", "TRUNCATE audit.audit_events")).ShouldBe("audit events are append-only");
    }

    [Fact]
    public async Task A_record_waiting_for_its_seal_can_only_be_sealed_whole()
    {
        (await RefusalAsync(Pending, "UPDATE audit.audit_events SET summary = 'Changed' WHERE id = lastval()"))
            .ShouldBe("audit events can only be sealed completely, not edited");
        (await RefusalAsync(Pending, "DELETE FROM audit.audit_events WHERE id = lastval()")).ShouldBe("audit events are append-only");
        (await RefusalAsync("INSERT INTO audit.audit_events (occurred_at, category, action, severity, hash) VALUES (now(), 'security', 'test.forged', 'Info', repeat('0', 64))"))
            .ShouldBe("audit events are sealed by the server, never inserted sealed");

        // A seal is the hash, the chain position and the link together (the link itself is checked at commit), and it
        // changes nothing else: not even a column outside the hash, such as who did it or how severe it was.
        const string Seal = "hash = repeat('0', 64), chain_index = 9000000000000000, previous_hash = repeat('1', 64)";
        (await RefusalAsync(Pending, $"UPDATE audit.audit_events SET {Seal} WHERE id = lastval()")).ShouldBeNull();
        (await RefusalAsync(Pending, "UPDATE audit.audit_events SET hash = repeat('0', 64) WHERE id = lastval()"))
            .ShouldBe("audit events can only be sealed completely, not edited");
        foreach (var change in new[] { "user_name = 'someone else'", "severity = 'Info'", "ip_address = '203.0.113.9'" })
        {
            (await RefusalAsync(Pending, $"UPDATE audit.audit_events SET {Seal}, {change} WHERE id = lastval()"))
                .ShouldBe("audit events can only be sealed completely, not edited", change);
        }
    }

    [Fact]
    public async Task The_sealed_trail_verifies_intact()
    {
        var recorded = await RecordAndSealAsync(3);

        var verification = await VerifyAsync();

        verification.Valid.ShouldBeTrue(verification.Message);
        verification.FirstInvalidChainIndex.ShouldBeNull();
        verification.CheckedCount.ShouldBeGreaterThanOrEqualTo(recorded.Max(e => e.ChainIndex!.Value), "Every sealed record up to these was checked.");
    }

    [Fact]
    public async Task A_change_to_any_part_of_a_record_behind_the_triggers_breaks_the_chain_where_it_was_made()
    {
        // In the middle of the three: the sealer only ever adds after the newest record.
        var original = (await RecordAndSealAsync(3))[1];
        var position = original.ChainIndex!.Value;
        var changes = new (string What, Action<AuditEvent> Change)[]
        {
            ("occurred_at", e => e.OccurredAt = e.OccurredAt.AddMinutes(-30)),
            ("category", e => e.Category = AuditCategories.Data),
            ("action", e => e.Action = "patient.accessed"),
            ("severity", e => e.Severity = AuditSeverity.Info),
            ("entity_type", e => e.EntityType = "Visit"),
            ("entity_id", e => e.EntityId = Guid.CreateVersion7().ToString()),
            ("patient_id", e => e.PatientId = Guid.CreateVersion7()),
            ("user_id", e => e.UserId = Guid.CreateVersion7()),
            ("user_name", e => e.UserName = "someone else"),
            ("facility_id", e => e.FacilityId = Guid.CreateVersion7()),
            ("device_id", e => e.DeviceId = Guid.CreateVersion7()),
            ("session_id", e => e.SessionId = Guid.CreateVersion7()),
            ("ip_address", e => e.IpAddress = "203.0.113.9"),
            ("correlation_id", e => e.CorrelationId = Guid.CreateVersion7().ToString("N")),
            ("summary", e => e.Summary = "Opened the record of a patient under their care"),
            ("details", e => e.Details = "{\"reason\": \"Routine review\"}"),
            ("previous_hash", e => e.PreviousHash = new string('0', 64)),
            ("hash", e => e.Hash = new string('0', 64)),
            ("mac", e => e.Mac = new string('0', 64)),
            ("chain_index, moved out of the chain", e => e.ChainIndex = -e.ChainIndex),
        };

        var unnoticed = new List<string>();
        foreach (var (what, change) in changes)
        {
            await BehindTheTriggersAsync(original.Id, change);
            try
            {
                var verification = await VerifyAsync();
                var expected = what == "mac"
                    ? $"The seal of the audit record at position {position} does not match. Records may have been altered."
                    : $"The audit chain is broken at position {position}. Records may have been altered or removed.";
                if (verification.Valid || verification.FirstInvalidChainIndex != position || verification.Message != expected)
                {
                    unnoticed.Add($"{what}: {verification.Message}");
                }
            }
            finally
            {
                await BehindTheTriggersAsync(original.Id, e => PutBack(e, original));
            }
        }

        unnoticed.ShouldBeEmpty($"Verification should report each change at position {position}:\n{string.Join('\n', unnoticed)}");
        (await VerifyAsync()).Valid.ShouldBeTrue("Put back as it was, the record verifies again.");
    }

    [Fact]
    public async Task A_record_removed_behind_the_triggers_breaks_the_chain_where_it_was()
    {
        var removed = (await RecordAndSealAsync(3))[1];
        var position = removed.ChainIndex!.Value;

        await RemoveBehindTheTriggersAsync(removed.Id);
        try
        {
            var verification = await VerifyAsync();
            verification.Valid.ShouldBeFalse();
            verification.FirstInvalidChainIndex.ShouldBe(position);
            verification.CheckedCount.ShouldBe(position - 1, "Everything before it still verifies.");
            verification.Message.ShouldBe($"The audit chain is broken at position {position}. Records may have been altered or removed.");
        }
        finally
        {
            await InsertBehindTheTriggersAsync(removed);
        }

        (await VerifyAsync()).Valid.ShouldBeTrue("Put back as it was, the record verifies again.");
    }

    /// <summary>The integrity check as Administration › Audit log runs it.</summary>
    private async Task<AuditVerification> VerifyAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        return await admin.GetAsync<AuditVerification>("/api/v1/admin/audit/verify");
    }

    /// <summary>
    /// Records break-glass events with every column filled in, waits until they are sealed (the server's sealer may get
    /// there first), and returns them as stored.
    /// </summary>
    private async Task<List<AuditEvent>> RecordAndSealAsync(int count)
    {
        var events = Enumerable.Range(1, count).Select(n => new AuditEvent
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditCategories.Security,
            Action = "patient.break_glass",
            Severity = AuditSeverity.Critical,
            EntityType = "Patient",
            EntityId = Guid.CreateVersion7().ToString(),
            PatientId = Guid.CreateVersion7(),
            UserId = Guid.CreateVersion7(),
            UserName = "doctor",
            FacilityId = Guid.CreateVersion7(),
            DeviceId = Guid.CreateVersion7(),
            SessionId = Guid.CreateVersion7(),
            IpAddress = "10.20.30.40",
            CorrelationId = Guid.CreateVersion7().ToString("N"),
            Summary = $"Unconscious in casualty, needs the allergy history ({n} of {count})",
            Details = "{\"reason\": \"Unconscious in casualty\"}",
        }).ToList();

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Set<AuditEvent>().AddRange(events);
            await db.SaveChangesAsync(Ct);
        }

        var ids = events.Select(e => e.Id).ToList();
        var waited = Stopwatch.StartNew();
        while (true)
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AuditChainService>().SealPendingAsync(Ct);
            var stored = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                .Where(e => ids.Contains(e.Id)).OrderBy(e => e.ChainIndex).ToListAsync(Ct);
            if (stored.All(e => e.Hash is not null))
            {
                return stored;
            }

            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(1), "The audit records were not sealed.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }

    /// <summary>
    /// Runs <paramref name="statements"/> in one transaction as the database owner, and rolls it back whatever happens,
    /// so a trigger that failed to refuse one changes nothing. Returns why PostgreSQL refused one, or null when it took
    /// them all.
    /// </summary>
    private async Task<string?> RefusalAsync(params string[] statements)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString());
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

    /// <summary>
    /// Changes the record as someone who can turn the triggers off would: the database owner, here a superuser, whose
    /// session runs as a replica for this transaction only, and a replica runs no ordinary trigger. The time it happened is
    /// part of the record's key (the trail is partitioned by month of it, SCAL-04), which EF Core never changes: SQL does.
    /// </summary>
    private async Task BehindTheTriggersAsync(long id, Action<AuditEvent> change)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"SET LOCAL session_replication_role = replica", Ct);
        var stored = await db.Set<AuditEvent>().AsNoTracking().SingleAsync(e => e.Id == id, Ct);
        var occurredAt = stored.OccurredAt;
        change(stored);
        if (stored.OccurredAt != occurredAt)
        {
            await db.Database.ExecuteSqlAsync($"UPDATE audit.audit_events SET occurred_at = {stored.OccurredAt} WHERE id = {id}", Ct);
        }

        db.Update(stored);
        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    private async Task RemoveBehindTheTriggersAsync(long id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"SET LOCAL session_replication_role = replica", Ct);
        await db.Database.ExecuteSqlAsync($"DELETE FROM audit.audit_events WHERE id = {id}", Ct);
        await transaction.CommitAsync(Ct);
    }

    /// <summary>Puts a removed record back exactly as it was, sealed, with its own id.</summary>
    private async Task InsertBehindTheTriggersAsync(AuditEvent e)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"SET LOCAL session_replication_role = replica", Ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO audit.audit_events (id, occurred_at, category, action, severity, entity_type, entity_id, patient_id, user_id,
                user_name, facility_id, device_id, session_id, ip_address, correlation_id, summary, details, chain_index, previous_hash, hash, mac)
            OVERRIDING SYSTEM VALUE
            VALUES ({e.Id}, {e.OccurredAt}, {e.Category}, {e.Action}, {e.Severity.ToString()}, {e.EntityType}, {e.EntityId}, {e.PatientId},
                {e.UserId}, {e.UserName}, {e.FacilityId}, {e.DeviceId}, {e.SessionId}, {e.IpAddress}, {e.CorrelationId}, {e.Summary},
                CAST({e.Details} AS jsonb), {e.ChainIndex}, {e.PreviousHash}, {e.Hash}, {e.Mac})
            """, Ct);
        await transaction.CommitAsync(Ct);
    }

    private static void PutBack(AuditEvent stored, AuditEvent original)
    {
        stored.OccurredAt = original.OccurredAt;
        stored.Category = original.Category;
        stored.Action = original.Action;
        stored.Severity = original.Severity;
        stored.EntityType = original.EntityType;
        stored.EntityId = original.EntityId;
        stored.PatientId = original.PatientId;
        stored.UserId = original.UserId;
        stored.UserName = original.UserName;
        stored.FacilityId = original.FacilityId;
        stored.DeviceId = original.DeviceId;
        stored.SessionId = original.SessionId;
        stored.IpAddress = original.IpAddress;
        stored.CorrelationId = original.CorrelationId;
        stored.Summary = original.Summary;
        stored.Details = original.Details;
        stored.ChainIndex = original.ChainIndex;
        stored.PreviousHash = original.PreviousHash;
        stored.Hash = original.Hash;
        stored.Mac = original.Mac;
    }

    /// <summary>The test server's own connection, which signs in as a superuser and owns the tables.</summary>
    private string OwnerConnectionString() => server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital")!;
}
