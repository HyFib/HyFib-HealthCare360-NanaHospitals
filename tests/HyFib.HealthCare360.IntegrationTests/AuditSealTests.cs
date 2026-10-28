using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the keyed seals, the anchors and the auditor's export add to the audit chain (AuditTrailTests has the chain and the
/// triggers). Someone who can write the database, the triggers turned off, can change a record and compute the SHA-256
/// chain again from it: the chain then holds, but the seals, made with a key that is not in the database, do not; nor do
/// the anchors kept outside the database when the chain is cut short. The export checks on its own, without the database.
/// </summary>
public sealed class AuditSealTests(HospitalServer server)
{
    /// <summary>The sealer's advisory lock (<see cref="AuditChainService"/>): held, it seals nothing, so no record links to a forged one.</summary>
    private const long SealerLock = 4_247_360;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_sealed_record_carries_this_servers_seal()
    {
        await RecordAndSealAsync(2);
        await using var scope = server.Services.CreateAsyncScope();
        var key = scope.ServiceProvider.GetRequiredService<AuditSealKey>();
        var latest = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.ChainIndex != null).OrderByDescending(e => e.ChainIndex).Take(20).ToListAsync(Ct);

        latest.ShouldAllBe(e => e.Mac != null && key.Sealed(e.Hash!, e.Mac));
    }

    [Fact]
    public async Task A_record_changed_and_the_chain_computed_again_without_the_key_does_not_verify()
    {
        var first = (await RecordAndSealAsync(3))[0].ChainIndex!.Value;

        // From here the chain's end stays where it is: nothing is sealed after a forged record, so putting it back restores it.
        await using var hold = await HoldSealerAsync();
        var chain = await ChainFromAsync(first);
        var position = chain[0].ChainIndex!.Value;
        try
        {
            // The forger keeps the seals as they were: they cannot make new ones.
            await RewriteAsync(chain, "Looked up the allergy history", keepSeals: true);
            var kept = await VerifyAsync();
            kept.Valid.ShouldBeFalse();
            kept.FirstInvalidChainIndex.ShouldBe(position);
            kept.Message.ShouldBe($"The seal of the audit record at position {position} does not match. Records may have been altered.");

            // Or takes them off.
            await RewriteAsync(chain, "Looked up the allergy history", keepSeals: false);
            var dropped = await VerifyAsync();
            dropped.Valid.ShouldBeFalse();
            dropped.FirstInvalidChainIndex.ShouldBe(position);
            dropped.Message.ShouldBe($"The audit record at position {position} has lost its seal. Records may have been altered.");
        }
        finally
        {
            await PutBackAsync(chain);
        }

        (await VerifyAsync()).Valid.ShouldBeTrue("Put back as it was, the chain verifies again.");
    }

    [Fact]
    public async Task The_chain_is_anchored_outside_the_database_and_a_chain_cut_short_or_made_again_does_not_match()
    {
        await RecordAndSealAsync(1);
        var anchors = server.Services.GetRequiredService<AuditAnchors>();
        AuditAnchor anchor;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            anchor = (await scope.ServiceProvider.GetRequiredService<AuditChainService>().AnchorIfDueAsync(TimeSpan.Zero, Ct)).ShouldNotBeNull();
        }

        anchors.Last().ShouldBe(anchor);
        var verified = await VerifyAsync();
        verified.Valid.ShouldBeTrue(verified.Message);
        verified.AnchorsChecked.ShouldBeGreaterThanOrEqualTo(1);
        verified.LastAnchor.ShouldBe(anchor);

        var original = await File.ReadAllTextAsync(anchors.Path, Ct);
        try
        {
            // Records anchored once are gone: the chain ends before an anchor.
            await File.AppendAllTextAsync(anchors.Path, JsonSerializer.Serialize(anchor with { ChainIndex = anchor.ChainIndex + 1_000_000 }, Json) + "\n", Ct);
            var cut = await VerifyAsync();
            cut.Valid.ShouldBeFalse();
            cut.Message.ShouldStartWith($"The audit chain ends at position ");
            cut.Message.ShouldContain("was anchored outside the database. Records have been removed");

            // The record at an anchored position is not the one anchored: the chain was made again.
            await File.WriteAllTextAsync(anchors.Path, original + JsonSerializer.Serialize(anchor with { Hash = new string('A', 64) }, Json) + "\n", Ct);
            var remade = await VerifyAsync();
            remade.Valid.ShouldBeFalse();
            remade.FirstInvalidChainIndex.ShouldBe(anchor.ChainIndex);
            remade.Message.ShouldBe($"The audit record at position {anchor.ChainIndex} is not the one anchored outside the database. The chain has been made again.");
        }
        finally
        {
            await File.WriteAllTextAsync(anchors.Path, original, Ct);
        }

        (await VerifyAsync()).Valid.ShouldBeTrue();
    }

    [Fact]
    public async Task An_auditor_checks_an_export_without_the_database_and_finds_a_changed_record()
    {
        var recorded = await RecordAndSealAsync(3);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuditChainService>().AnchorIfDueAsync(TimeSpan.Zero, Ct);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var from = recorded.Min(e => e.OccurredAt).AddMinutes(-1);
        var to = DateTimeOffset.UtcNow.AddMinutes(1);
        using var response = await admin.SendRawAsync(HttpMethod.Get, $"/api/v1/admin/audit/export?from={Uri.EscapeDataString(from.ToString("O"))}&to={Uri.EscapeDataString(to.ToString("O"))}");
        response.EnsureSuccessStatusCode();
        response.Content.Headers.ContentDisposition?.FileName.ShouldNotBeNull().ShouldStartWith("hc360-audit-");
        var export = (await response.Content.ReadFromJsonAsync<AuditExport>(Json, Ct)).ShouldNotBeNull();

        export.Format.ShouldBe(AuditExport.FormatName);
        export.Events.Select(e => e.Id).ShouldContain(recorded[1].Id);
        export.Events.ShouldAllBe(e => e.Mac != null);
        var intact = AuditExportVerifier.Verify(export);
        intact.Valid.ShouldBeTrue(intact.Message);
        intact.EventsChecked.ShouldBe(export.Events.Count);

        // A record changed in the file breaks it there.
        var position = export.Events.Single(e => e.Id == recorded[1].Id).ChainIndex;
        var changed = export with
        {
            Events = [.. export.Events.Select(e => e.ChainIndex == position ? e with { Summary = "Nothing happened" } : e)],
        };
        var broken = AuditExportVerifier.Verify(changed);
        broken.Valid.ShouldBeFalse();
        broken.Message.ShouldBe($"The chain is broken at position {position}: the record there was changed, removed or moved.");

        // So does an export made again in full, when the auditor holds an anchor of their own from before.
        var remade = AuditExportVerifier.Verify(export, [new AuditAnchor(position, new string('B', 64), null, DateTimeOffset.UtcNow.AddDays(-1))]);
        remade.Valid.ShouldBeFalse();
        remade.Message.ShouldStartWith($"The record at position {position} is not the one anchored on");

        // Exporting it is itself in the audit trail.
        await using var check = server.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .AnyAsync(e => e.Action == "audit.exported" && e.UserName == HospitalServer.Users.HospitalAdmin && e.OccurredAt >= from, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_check_that_finds_the_chain_broken_turns_System_health_critical_until_one_passes()
    {
        var first = (await RecordAndSealAsync(3))[0].ChainIndex!.Value;
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await using var hold = await HoldSealerAsync();
        var chain = await ChainFromAsync(first);
        try
        {
            await RewriteAsync(chain, "Looked up the allergy history", keepSeals: true);
            var check = await VerifyAsync();
            check.Valid.ShouldBeFalse();

            var warning = (await HealthWarningsAsync(admin)).Where(w => w.Code == "audit.chain_broken").ShouldHaveSingleItem();
            warning.Severity.ShouldBe("critical");
            warning.Message.ShouldContain(check.Message!);
        }
        finally
        {
            await PutBackAsync(chain);
        }

        (await VerifyAsync()).Valid.ShouldBeTrue();
        (await HealthWarningsAsync(admin)).ShouldNotContain(w => w.Code == "audit.chain_broken");
    }

    [Fact]
    public async Task A_broken_chain_is_told_to_administrators_once_while_it_lasts_and_again_when_it_comes_back()
    {
        var first = (await RecordAndSealAsync(3))[0].ChainIndex!.Value;
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var alerts = server.Services.GetRequiredService<SystemHealthAlerts>();
        await using var hold = await HoldSealerAsync();
        var chain = await ChainFromAsync(first);
        try
        {
            await RewriteAsync(chain, "Looked up the allergy history", keepSeals: true);
            var broken = (await VerifyAsync()).Message!;
            await alerts.CheckAsync(Ct);
            await alerts.CheckAsync(Ct);
            (await AlertsAsync(admin, broken)).ShouldBe(1, "told once while it lasts");

            await PutBackAsync(chain);
            (await VerifyAsync()).Valid.ShouldBeTrue();
            await alerts.CheckAsync(Ct);
            await RewriteAsync(chain, "Looked up the allergy history", keepSeals: true);
            (await VerifyAsync()).Valid.ShouldBeFalse();
            await alerts.CheckAsync(Ct);
            (await AlertsAsync(admin, broken)).ShouldBe(2, "told again when it comes back");
        }
        finally
        {
            await PutBackAsync(chain);
        }

        (await VerifyAsync()).Valid.ShouldBeTrue();
        await alerts.CheckAsync(Ct);
    }

    [Fact]
    public async Task Only_whoever_may_read_the_audit_trail_exports_it()
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var now = DateTimeOffset.UtcNow;
        using var refused = await doctor.SendRawAsync(HttpMethod.Get,
            $"/api/v1/admin/audit/export?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.ToString("O"))}");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<IReadOnlyList<HealthWarning>> HealthWarningsAsync(ApiClient admin) =>
        (await admin.GetAsync<HealthWarnings>("/api/v1/admin/system/health")).Warnings;

    /// <summary>The critical System health alerts <paramref name="admin"/> has that say <paramref name="message"/>.</summary>
    private static async Task<int> AlertsAsync(ApiClient admin, string message) =>
        (await admin.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
        .Count(n => n.Title == "System health: critical" && n.Severity == "critical" && n.Body?.Contains(message, StringComparison.Ordinal) == true);

    private sealed record HealthWarnings(IReadOnlyList<HealthWarning> Warnings);

    /// <summary>The integrity check as Administration › Audit log runs it.</summary>
    private async Task<AuditVerification> VerifyAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        return await admin.GetAsync<AuditVerification>("/api/v1/admin/audit/verify");
    }

    /// <summary>
    /// Holds the sealer's lock on a connection of its own, not from the pool: a pooled connection goes back to the pool when
    /// disposed with its session, and the lock, still held. Closing this one ends the session and lets the lock go.
    /// </summary>
    private async Task<NpgsqlConnection> HoldSealerAsync()
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(OwnerConnectionString()) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT pg_advisory_lock({SealerLock})", connection);
        await command.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    /// <summary>Records <paramref name="count"/> events and waits until they are sealed (the server's sealer may get there first).</summary>
    private async Task<List<AuditEvent>> RecordAndSealAsync(int count)
    {
        var events = Enumerable.Range(1, count).Select(n => new AuditEvent
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Category = AuditCategories.PhiAccess,
            Action = "patient.accessed",
            EntityType = "Patient",
            PatientId = Guid.CreateVersion7(),
            UserId = Guid.CreateVersion7(),
            UserName = "doctor",
            Summary = $"Viewed the discharge summary ({n} of {count})",
        }).ToList();
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            db.Set<AuditEvent>().AddRange(events);
            await db.SaveChangesAsync(Ct);
        }

        var ids = events.Select(e => e.Id).ToList();
        for (var attempt = 0; ; attempt++)
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AuditChainService>().SealPendingAsync(Ct);
            var stored = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                .Where(e => ids.Contains(e.Id)).OrderBy(e => e.ChainIndex).ToListAsync(Ct);
            if (stored.All(e => e.Hash is not null))
            {
                return stored;
            }

            attempt.ShouldBeLessThan(300, "The audit records were not sealed.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }
    }

    /// <summary>The sealed records from <paramref name="position"/> to the end, as stored.</summary>
    private async Task<List<AuditEvent>> ChainFromAsync(long position)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.ChainIndex >= position).OrderBy(e => e.ChainIndex).ToListAsync(Ct);
    }

    /// <summary>
    /// As someone who can turn the triggers off: changes the first record's summary and computes the SHA-256 chain again
    /// from it to the end, which needs no key. The seals are kept as they were, or taken off.
    /// </summary>
    private Task RewriteAsync(IReadOnlyList<AuditEvent> chain, string summary, bool keepSeals) =>
        BehindTheTriggersAsync(chain, (e, i, previous) =>
        {
            if (i == 0)
            {
                e.Summary = summary;
            }

            e.PreviousHash = previous;
            e.Hash = AuditEvent.ComputeHash(previous, e.CanonicalContent());
            if (!keepSeals)
            {
                e.Mac = null;
            }

            return e.Hash;
        });

    private Task PutBackAsync(IReadOnlyList<AuditEvent> chain) =>
        BehindTheTriggersAsync(chain, (e, i, _) =>
        {
            var original = chain[i];
            e.Summary = original.Summary;
            e.PreviousHash = original.PreviousHash;
            e.Hash = original.Hash;
            e.Mac = original.Mac;
            return e.Hash!;
        });

    private async Task BehindTheTriggersAsync(IReadOnlyList<AuditEvent> chain, Func<AuditEvent, int, string?, string> change)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlAsync($"SET LOCAL session_replication_role = replica", Ct);
        string? previous = chain[0].PreviousHash;
        for (var i = 0; i < chain.Count; i++)
        {
            var stored = await db.Set<AuditEvent>().SingleAsync(e => e.Id == chain[i].Id, Ct);
            previous = change(stored, i, previous);
        }

        await db.SaveChangesAsync(Ct);
        await transaction.CommitAsync(Ct);
    }

    /// <summary>The test server's own connection, which signs in as a superuser and owns the tables.</summary>
    private string OwnerConnectionString() => server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital")!;
}
