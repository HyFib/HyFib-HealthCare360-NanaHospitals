using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Retention;
using HyFib.HealthCare360.Modules.Privacy.Features.Settings;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// ADR-015, SRS SEC-04: the audit trail is kept permanently unless two super administrators (holders of
/// <c>admin.system.manage</c>) set a period, never fewer than 8 years: books of account and their audit trail are kept 8
/// years, and the trail records changes to bills and payments too. Nobody else changes it, nor around that path. Old months
/// go only by a purge another super administrator approves: whole months entirely before the cut-off at the start of the
/// chain, exported first in the auditor's format, recorded in a sealed purge record with an anchor outside the database,
/// then dropped; the chain verifies from where it then starts, and a purge record made up, or its anchor removed, does not.
/// The months purged are the oldest the server holds (<see cref="HospitalServer.OldAuditMonths"/>), chain positions 1 to 3.
/// </summary>
public sealed class AuditRetentionTests(HospitalServer server)
{
    private const string StatusPath = "/api/v1/admin/audit/retention";
    private const string ChangePath = StatusPath + "/change";
    private const string PurgePath = StatusPath + "/purge";

    /// <summary>The sealer's advisory lock (<see cref="AuditChainService"/>): held, it seals nothing.</summary>
    private const long SealerLock = 4_247_360;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_super_administrators_ask_for_a_period_and_never_for_fewer_than_8_years()
    {
        await KeepPermanentlyAsync();
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // The hospital administrator holds neither the setting nor its approval.
        using (var asked = await hospitalAdmin.SendRawAsync(HttpMethod.Post, ChangePath, new AskAuditRetentionRequest(10, "Keep ten years")))
        {
            asked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        using (var read = await hospitalAdmin.SendRawAsync(HttpMethod.Get, StatusPath))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // Nor around it: the retention schedule, which they keep, and the settings, which they may change.
        using (var schedule = await hospitalAdmin.SendRawAsync(HttpMethod.Put, "/api/v1/privacy/retention/audit", new UpdateRetentionRequest(10)))
        {
            schedule.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await schedule.Content.ReadAsStringAsync(Ct)).ShouldContain("Super administrators set how long the audit trail is kept");
        }

        using (var setting = await hospitalAdmin.SendRawAsync(HttpMethod.Put, $"/api/v1/admin/settings/{AuditRetention.YearsSetting}",
                   new SaveSettingRequest(JsonValue.Create(10), null, false, null)))
        {
            setting.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await setting.Content.ReadAsStringAsync(Ct)).ShouldContain("Super administrators set how long the audit trail is kept");
        }

        // A super administrator is refused fewer than 8 years, with the reason.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        using (var seven = await admin.SendRawAsync(HttpMethod.Post, ChangePath, new AskAuditRetentionRequest(7, "Seven years are enough")))
        {
            seven.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await seven.Content.ReadAsStringAsync(Ct)).ShouldContain("books of account and their audit trail are kept 8 years");
        }

        var status = await admin.GetAsync<AuditRetentionStatus>(StatusPath);
        status.Years.ShouldBeNull("still kept permanently");
        status.PendingChange.ShouldBeNull();
        (await PeriodAsync()).ShouldBeNull();
    }

    [Fact]
    public async Task A_period_takes_a_second_super_administrator_and_is_then_what_patients_are_told()
    {
        await KeepPermanentlyAsync();
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var second = await server.SignInAsync(HospitalServer.Users.SecondSystemAdmin);
        try
        {
            var asked = await admin.PostAsync<ApprovalRequestDto>(ChangePath, new AskAuditRetentionRequest(8, "Kept as long as the books of account"));
            asked.Kind.ShouldBe(AuditRetention.ChangeKind);
            asked.Status.ShouldBe(ApprovalStatus.Pending);
            asked.Details.ShouldContain(new ApprovalDetail("Kept now", "Permanently"));
            asked.Details.ShouldContain(new ApprovalDetail("Asked for", "For 8 years"));
            var cutoff = AuditRetention.CutoffMonth(DateTimeOffset.UtcNow, 8);
            asked.Details.Single(d => d.Label == "What it means").Value.ShouldContain($"the months before {AuditRetention.MonthWords(cutoff)}");

            // Nothing changes until another approves it: not its maker.
            using (var self = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            {
                self.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            var waiting = await admin.GetAsync<AuditRetentionStatus>(StatusPath);
            (waiting.Years, waiting.PendingChange?.ApprovalId).ShouldBe(((int?)null, (Guid?)asked.Id));
            (await PeriodAsync()).ShouldBeNull();

            await ApproveAsync(second, asked.Id);
            (await admin.GetAsync<AuditRetentionStatus>(StatusPath)).Years.ShouldBe(8);
            (await PeriodAsync()).ShouldBe(8);
            (await AuditedAsync(AuditRetention.ChangedAction, asked.RequestedAt)).ShouldBeTrue();

            // The retention schedule and the data summary say so.
            var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            var rule = (await hospitalAdmin.GetAsync<List<RetentionRuleDto>>("/api/v1/privacy/retention")).Single(r => r.Key == RetentionSchedule.Audit);
            (rule.Value, rule.SystemManaged, rule.Editable).ShouldBe(((int?)8, true, false));
            var summary = await SummaryAsync();
            summary.Periods.AuditYears.ShouldBe(8);
            summary.Categories.Where(c => c.RetentionKind == PersonalDataRetention.Audit).ShouldAllBe(c => c.Retention == "For 8 years, and never edited.");
            (await server.Anonymous(ownAddress: true).GetAsync<PublicPrivacyInfo>("/api/v1/public/privacy")).AccessLogYears.ShouldBe(8);

            // And back to permanently, two people again: asked by the second, approved by the first.
            var back = await second.PostAsync<ApprovalRequestDto>(ChangePath, new AskAuditRetentionRequest(null, "Keep it permanently again"));
            back.Details.ShouldContain(new ApprovalDetail("Asked for", "Permanently"));
            await ApproveAsync(admin, back.Id);
            (await PeriodAsync()).ShouldBeNull();
            (await SummaryAsync()).Categories.Where(c => c.RetentionKind == PersonalDataRetention.Audit)
                .ShouldAllBe(c => c.Retention == "Permanently, and never edited.");
            (await server.Anonymous(ownAddress: true).GetAsync<PublicPrivacyInfo>("/api/v1/public/privacy")).AccessLogYears.ShouldBeNull();
        }
        finally
        {
            await KeepPermanentlyAsync();
        }
    }

    [Fact]
    public async Task Nothing_is_purged_while_the_audit_trail_is_kept_permanently()
    {
        await KeepPermanentlyAsync();
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);

        var status = await admin.GetAsync<AuditRetentionStatus>(StatusPath);
        status.Purge.Months.ShouldBeEmpty();
        status.Purge.Problem.ShouldNotBeNull().ShouldContain("kept permanently");

        using var refused = await admin.SendRawAsync(HttpMethod.Post, PurgePath, new AskAuditPurgeRequest("Free some space"));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("kept permanently");
    }

    [Fact]
    public async Task A_purge_removes_the_whole_months_before_the_cutoff_exported_sealed_and_anchored_and_the_chain_verifies_from_there()
    {
        await KeepPermanentlyAsync();
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var second = await server.SignInAsync(HospitalServer.Users.SecondSystemAdmin);
        try
        {
            await SetPeriodAsync(admin, second, 8);
            await EnsureOldMonthsArePartitionedAsync();
            var auditor = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            (await auditor.GetAsync<AuditVerification>("/api/v1/admin/audit/verify")).Valid.ShouldBeTrue("the chain verifies before the purge");

            var plan = (await admin.GetAsync<AuditRetentionStatus>(StatusPath)).Purge;
            plan.Problem.ShouldBeNull(plan.Problem);
            plan.CutoffMonth.ShouldBe(AuditRetention.CutoffMonth(DateTimeOffset.UtcNow, 8));
            plan.Months.Select(m => m.Month).ShouldBe(HospitalServer.OldAuditMonths, "only the whole months before the cut-off");
            (plan.Rows, plan.FirstChainIndex, plan.BoundaryChainIndex).ShouldBe(((long)HospitalServer.OldAuditEvents, (long?)1, (long?)HospitalServer.OldAuditEvents));

            // Asked for, then a record comes into one of those months before the approval: the purge is refused.
            var stale = await admin.PostAsync<ApprovalRequestDto>(PurgePath, new AskAuditPurgeRequest("Older than the books of account"));
            stale.Kind.ShouldBe(AuditRetention.PurgeKind);
            stale.Details.ShouldContain(new ApprovalDetail("Records", "3"));
            await using (var hold = await HoldSealerAsync())
            {
                var late = await ScalarAsync<long>(
                    "INSERT INTO audit.audit_events (occurred_at, category, action, severity) VALUES ('2015-04-21 10:00:00+00', 'security', 'it.late', 'Info') RETURNING id");
                try
                {
                    var request = await second.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{stale.Id}");
                    using (var refused = await second.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{stale.Id}/approve", new ApproveRequest(null, request.Version)))
                    {
                        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                    }

                    var failed = await second.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{stale.Id}");
                    failed.Status.ShouldBe(ApprovalStatus.Failed);
                    failed.Outcome.ShouldNotBeNull().ShouldContain("nothing was removed");
                    (await PartitionExistsAsync(HospitalServer.OldAuditMonths[1])).ShouldBeTrue();
                }
                finally
                {
                    // Never sealed: taking it out leaves the chain as it was.
                    await BehindTheTriggersAsync($"DELETE FROM audit.audit_events WHERE id = {late} AND hash IS NULL");
                }
            }

            // Asked again, as the trail is now, and approved by the other: done.
            var asked = await admin.PostAsync<ApprovalRequestDto>(PurgePath, new AskAuditPurgeRequest("Older than the books of account"));
            var approved = await ApproveAsync(second, asked.Id);
            approved.Outcome.ShouldNotBeNull().ShouldContain("exported to hc360-audit-purge-1-3-");

            // The purge record, sealed with the audit seal key.
            AuditPurge purge;
            await using (var scope = server.Services.CreateAsyncScope())
            {
                purge = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditPurge>().AsNoTracking().SingleAsync(p => p.ApprovalId == asked.Id, Ct);
                scope.ServiceProvider.GetRequiredService<AuditSealKey>().Sealed(purge.ContentHash(), purge.Mac).ShouldBeTrue();
            }

            (purge.FirstChainIndex, purge.BoundaryChainIndex, purge.RowCount, purge.Months, purge.RetentionYears).ShouldBe((1L, 3L, 3L, "2015-03,2015-04", 8));
            purge.BoundaryHash.ShouldBe(plan.BoundaryHash);
            (purge.RequestedByName, purge.ApprovedBy).ShouldBe((asked.RequestedByName, approved.DecidedBy!.Value));

            // The export: in the auditor's format, outside the database, intact, the purge record's SHA-256.
            var path = Path.Combine(server.Services.GetRequiredService<DataDirectories>().AuditArchive, purge.ExportFile);
            File.Exists(path).ShouldBeTrue(path);
            await using (var file = File.OpenRead(path))
            {
                var check = await AuditExportVerifier.VerifyAsync(file, ct: Ct);
                check.Valid.ShouldBeTrue(check.Message);
                check.EventsChecked.ShouldBe(3);
            }

            await using (var file = File.OpenRead(path))
            {
                Convert.ToHexString(await SHA256.HashDataAsync(file, Ct)).ShouldBe(purge.ExportSha256);
            }

            var export = JsonSerializer.Deserialize<AuditExport>(await File.ReadAllTextAsync(path, Ct), Json).ShouldNotBeNull();
            (export.Format, export.FromIndex, export.ToIndex).ShouldBe((AuditExport.FormatName, 1L, 3L));
            export.Events.ShouldAllBe(e => e.Summary != null && e.Summary.EndsWith("(it: an old month)"));
            AuditExportVerifier.Verify(export).Valid.ShouldBeTrue();

            // The months are gone, the purge is in the audit trail, and the anchor of where the chain now starts is outside the database.
            foreach (var month in HospitalServer.OldAuditMonths)
            {
                (await PartitionExistsAsync(month)).ShouldBeFalse(PartitionedTables.Audit.PartitionFor(month));
            }

            (await AuditedAsync(AuditRetention.PurgedAction, asked.RequestedAt, AuditSeverity.Critical)).ShouldBeTrue();
            server.Services.GetRequiredService<AuditAnchors>().Read().Anchors.ShouldContain(a => a.ChainIndex == 3 && a.Hash == purge.BoundaryHash);

            // The chain verifies from position 4, and partition upkeep neither makes the months again nor fails on them.
            var verified = await VerifyAsync();
            verified.Valid.ShouldBeTrue(verified.Message);
            verified.PurgedThrough.ShouldBe(3);
            await using (var scope = server.Services.CreateAsyncScope())
            {
                if (await scope.ServiceProvider.GetRequiredService<PartitionMaintenance>().RunAsync(Ct) is { } passes)
                {
                    passes.Single(p => p.Table == PartitionedTables.AuditEvents).Failed.ShouldBeFalse();
                }
            }

            foreach (var month in HospitalServer.OldAuditMonths)
            {
                (await PartitionExistsAsync(month)).ShouldBeFalse();
            }

            (await admin.GetAsync<AuditRetentionStatus>(StatusPath)).LastPurge.ShouldNotBeNull().ExportFile.ShouldBe(purge.ExportFile);

            // A purge record made up to hide rows someone removed: it carries no seal of this server's key.
            var forged = new AuditPurge
            {
                PurgedAt = DateTimeOffset.UtcNow,
                RetentionYears = 8,
                CutoffMonth = purge.CutoffMonth,
                Months = "2016-01",
                RowCount = 10,
                FirstChainIndex = 4,
                BoundaryChainIndex = 1_000_000_000,
                BoundaryHash = new string('A', 64),
                ExportFile = "made-up.json",
                ExportSha256 = new string('B', 64),
                Mac = new string('0', 64),
            };
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                db.Add(forged);
                await db.SaveChangesAsync(Ct);
            }

            try
            {
                var made = await VerifyAsync();
                made.Valid.ShouldBeFalse();
                made.Message.ShouldBe("The record of the purge of audit positions 4 to 1,000,000,000 does not carry this server's seal. Records may have been removed and the purge record made up.");
            }
            finally
            {
                await BehindTheTriggersAsync($"DELETE FROM audit.audit_purges WHERE id = '{forged.Id}'");
            }

            (await VerifyAsync()).Valid.ShouldBeTrue();

            // The anchor of the boundary taken out of the anchor file: the purge can no longer be told from rows removed.
            var anchors = server.Services.GetRequiredService<AuditAnchors>();
            var original = await File.ReadAllTextAsync(anchors.Path, Ct);
            try
            {
                var kept = original.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)
                    && JsonSerializer.Deserialize<AuditAnchor>(line, Json) is not { ChainIndex: 3 });
                await File.WriteAllTextAsync(anchors.Path, string.Join('\n', kept) + "\n", Ct);
                var unanchored = await VerifyAsync();
                unanchored.Valid.ShouldBeFalse();
                unanchored.Message.ShouldBe("The record of the purge of audit positions 1 to 3 has no anchor of position 3 with its hash outside the database. Records may have been removed and the purge record made up.");
            }
            finally
            {
                await File.WriteAllTextAsync(anchors.Path, original, Ct);
            }

            (await VerifyAsync()).Valid.ShouldBeTrue();

            // Nothing older is left to purge.
            (await admin.GetAsync<AuditRetentionStatus>(StatusPath)).Purge.Problem.ShouldNotBeNull().ShouldContain("nothing to purge");
        }
        finally
        {
            await KeepPermanentlyAsync();
        }
    }

    /// <summary>Sets the period through the two-person change: asked by <paramref name="maker"/>, approved by <paramref name="checker"/>.</summary>
    private static async Task SetPeriodAsync(ApiClient maker, ApiClient checker, int years)
    {
        var asked = await maker.PostAsync<ApprovalRequestDto>(ChangePath, new AskAuditRetentionRequest(years, "Kept as long as the books of account"));
        await ApproveAsync(checker, asked.Id);
    }

    private static async Task<ApprovalRequestDto> ApproveAsync(ApiClient checker, Guid id)
    {
        var request = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{id}");
        request.CanDecide.ShouldBeTrue();
        var approved = await checker.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{id}/approve", new ApproveRequest("Agreed.", request.Version));
        approved.Status.ShouldBe(ApprovalStatus.Approved, approved.Outcome);
        return approved;
    }

    /// <summary>The test's own clean-up: the audit trail kept permanently again, as the other tests expect.</summary>
    private async Task KeepPermanentlyAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        if (await AuditRetention.YearsAsync(settings, CancellationToken.None) is not null)
        {
            await settings.SetAsync<int?>(AuditRetention.YearsSetting, null, cancellationToken: CancellationToken.None);
        }
    }

    private async Task<int?> PeriodAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await AuditRetention.YearsAsync(scope.ServiceProvider.GetRequiredService<ISettingsService>(), Ct);
    }

    /// <summary>The data summary of a patient of the demonstration data, as staff and the portal show it.</summary>
    private async Task<DataSummary> SummaryAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var patient = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().IgnoreQueryFilters().AsNoTracking()
            .OrderBy(p => p.CreatedAt).Select(p => p.Id).FirstAsync(Ct);
        return (await scope.ServiceProvider.GetRequiredService<PersonalDataInventory>().SummariseAsync(patient, Ct)).ShouldNotBeNull();
    }

    private async Task<bool> AuditedAsync(string action, DateTimeOffset since, AuditSeverity? severity = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .AnyAsync(e => e.Action == action && e.OccurredAt >= since && (severity == null || e.Severity == severity), Ct);
    }

    /// <summary>The integrity check, without recording it in the audit trail (a failed one would turn System health critical).</summary>
    private async Task<AuditVerification> VerifyAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuditChainService>().VerifyAsync(Ct);
    }

    /// <summary>
    /// The old months in partitions of their own, as the server made them at start-up; partition upkeep makes them if it
    /// has not yet (another node's pass may hold its lock for a moment).
    /// </summary>
    private async Task EnsureOldMonthsArePartitionedAsync()
    {
        for (var attempt = 0; !await AllPartitionedAsync(); attempt++)
        {
            attempt.ShouldBeLessThan(30, "the old months were not given partitions of their own");
            await using var scope = server.Services.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<PartitionMaintenance>().RunAsync(Ct) is null)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            }
        }

        async Task<bool> AllPartitionedAsync()
        {
            foreach (var month in HospitalServer.OldAuditMonths)
            {
                if (!await PartitionExistsAsync(month))
                {
                    return false;
                }
            }

            return true;
        }
    }

    private Task<bool> PartitionExistsAsync(DateOnly month) =>
        ScalarAsync<bool>($"SELECT to_regclass('audit.\"{PartitionedTables.Audit.PartitionFor(month)}\"') IS NOT NULL");

    /// <summary>
    /// Holds the sealer's lock on a connection of its own, not from the pool, so nothing is sealed until it is disposed
    /// (as AuditSealTests do).
    /// </summary>
    private async Task<NpgsqlConnection> HoldSealerAsync()
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(OwnerConnectionString()) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT pg_advisory_lock({SealerLock})", connection);
        await command.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    /// <summary>As someone who can turn the triggers off: the test putting back what it made.</summary>
    private async Task BehindTheTriggersAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString());
        await connection.OpenAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);
        await using (var replica = new NpgsqlCommand("SET LOCAL session_replication_role = replica", connection, transaction))
        {
            await replica.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await transaction.CommitAsync(CancellationToken.None);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(OwnerConnectionString());
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>The test server's own connection, which owns the tables.</summary>
    private string OwnerConnectionString() => server.Services.GetRequiredService<IConfiguration>().GetConnectionString("Hospital")!;
}
