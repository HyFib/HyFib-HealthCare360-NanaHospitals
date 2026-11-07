using System.Text;
using HyFib.HealthCare360.Platform.Auditing;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// ADR-015: how long the audit trail is kept (permanently, or 8 years at the least), from which month a purge may remove
/// it, which months it may remove (whole ones at the start of the chain, every record sealed), and the export a purge
/// writes and checks a record at a time. AuditRetentionTests in the integration tests make the purge against PostgreSQL.
/// </summary>
public sealed class AuditRetentionTests
{
    private static readonly DateOnly Cutoff = new(2018, 11, 1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void The_period_is_permanent_or_8_years_at_the_least()
    {
        AuditRetention.Problem(null).ShouldBeNull("permanently");
        AuditRetention.Problem(8).ShouldBeNull();
        AuditRetention.Problem(99).ShouldBeNull();
        AuditRetention.Problem(7).ShouldNotBeNull().ShouldContain("books of account and their audit trail are kept 8 years (Companies Act 2013 s.128(5))");
        AuditRetention.Problem(100).ShouldNotBeNull();

        AuditRetention.Effective(null).ShouldBeNull("nothing stored: permanently");
        AuditRetention.Effective(5).ShouldBe(8, "a period stored below the minimum is not applied below it");
        AuditRetention.Effective(10).ShouldBe(10);
        AuditRetention.Effective(500).ShouldBe(99);
        AuditRetention.Words(null).ShouldBe("permanently");
        AuditRetention.Words(8).ShouldBe("for 8 years");
    }

    [Theory]
    [InlineData("2026-11-07T10:00:00Z", 8, "2018-11-01")]
    [InlineData("2026-11-01T00:00:00Z", 8, "2018-11-01")]
    [InlineData("2026-10-31T23:59:59Z", 10, "2016-10-01")]
    [InlineData("2028-02-29T12:00:00Z", 8, "2020-02-01")]
    public void The_cutoff_is_the_period_back_rounded_down_to_the_month(string now, int years, string cutoff) =>
        AuditRetention.CutoffMonth(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture), years)
            .ShouldBe(DateOnly.Parse(cutoff, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Only_monthly_partitions_have_a_month()
    {
        AuditRetention.MonthOfPartition("audit_events_2015_03").ShouldBe(new DateOnly(2015, 3, 1));
        AuditRetention.MonthOfPartition("audit_events_default").ShouldBeNull();
        AuditRetention.MonthOfPartition("audit_events_2015_13").ShouldBeNull();
        AuditRetention.MonthOfPartition("notification_logs_2015_03").ShouldBeNull();
    }

    [Fact]
    public void Whole_months_at_the_start_of_the_chain_every_record_sealed_may_go()
    {
        AuditRetention.PurgeProblem(Facts()).ShouldBeNull();

        // After an earlier purge the chain starts after its boundary.
        AuditRetention.PurgeProblem(Facts() with { FirstRemoved = 101, LastRemoved = 400, Rows = 300, FirstKept = 401, ExpectedFirst = 101 }).ShouldBeNull();
    }

    [Fact]
    public void Months_that_are_not_the_chains_start_are_refused_with_the_reason()
    {
        AuditRetention.PurgeProblem(Facts() with { Months = 0, Rows = 0, FirstRemoved = null, LastRemoved = null })
            .ShouldNotBeNull().ShouldContain("No month of the audit trail is entirely before November 2018");
        AuditRetention.PurgeProblem(Facts() with { Unsealed = 2 }).ShouldNotBeNull().ShouldContain("not sealed yet");
        AuditRetention.PurgeProblem(Facts() with { Rows = 0, FirstRemoved = null, LastRemoved = null }).ShouldNotBeNull().ShouldContain("hold no records");
        AuditRetention.PurgeProblem(Facts() with { InDefaultBefore = 4 }).ShouldNotBeNull().ShouldContain("default partition");
        AuditRetention.PurgeProblem(Facts() with { FirstKept = null }).ShouldNotBeNull().ShouldContain("Nothing sealed would be left");

        // A record sealed late in an old month (a clock far behind): the months are not the chain's start.
        AuditRetention.PurgeProblem(Facts() with { LastRemoved = 5_000, Rows = 101 }).ShouldNotBeNull()
            .ShouldContain("only months at the start of the chain can be purged");
        AuditRetention.PurgeProblem(Facts() with { LastRemoved = 100, FirstKept = 100 }).ShouldNotBeNull()
            .ShouldContain("only months at the start of the chain can be purged");

        // Positions missing: from the start, or between.
        AuditRetention.PurgeProblem(Facts() with { FirstRemoved = 2, Rows = 99 }).ShouldNotBeNull().ShouldContain("positions are missing");
        AuditRetention.PurgeProblem(Facts() with { Rows = 99 }).ShouldNotBeNull().ShouldContain("positions are missing");
    }

    [Fact]
    public void The_general_settings_do_not_set_the_period()
    {
        AuditRetention.SettingProblem(AuditRetention.YearsSetting).ShouldNotBeNull();
        AuditRetention.SettingProblem("PRIVACY.RETENTION.AUDIT").ShouldNotBeNull();
        AuditRetention.SettingProblem("privacy.retention.finance").ShouldBeNull();
    }

    [Fact]
    public void A_purge_record_is_sealed_over_every_column()
    {
        var purge = Purge();
        var sealedHash = purge.ContentHash();
        Action<AuditPurge>[] changes =
        [
            p => p.Id = Guid.CreateVersion7(),
            p => p.PurgedAt = p.PurgedAt.AddSeconds(1),
            p => p.RetentionYears = 9,
            p => p.CutoffMonth = p.CutoffMonth.AddMonths(1),
            p => p.Months = "2015-03",
            p => p.RowCount++,
            p => p.FirstChainIndex++,
            p => p.BoundaryChainIndex++,
            p => p.BoundaryHash = new string('C', 64),
            p => p.ExportFile = "other.json",
            p => p.ExportSha256 = new string('D', 64),
            p => p.ApprovalId = Guid.CreateVersion7(),
            p => p.RequestedBy = Guid.CreateVersion7(),
            p => p.RequestedByName = "Someone else",
            p => p.ApprovedBy = Guid.CreateVersion7(),
            p => p.ApprovedByName = "Someone else",
        ];

        for (var i = 0; i < changes.Length; i++)
        {
            var changed = Purge();
            changes[i](changed);
            changed.ContentHash().ShouldNotBe(sealedHash, $"Change {i + 1} of {changes.Length} went unnoticed.");
        }

        purge.MonthList().ShouldBe([new DateOnly(2015, 3, 1), new DateOnly(2015, 4, 1)]);
    }

    [Fact]
    public async Task An_export_written_a_record_at_a_time_checks_a_record_at_a_time_as_it_does_whole()
    {
        var events = Chain(5);
        var anchors = new[] { new AuditAnchor(3, events[2].Hash, null, DateTimeOffset.UtcNow) };
        var bytes = await WriteAsync(events, anchors);

        var whole = AuditExportVerifier.Verify(JsonSerializerRead(bytes));
        var streamed = await AuditExportVerifier.VerifyAsync(new MemoryStream(bytes), ct: Ct);
        streamed.ShouldBe(whole);
        streamed.Valid.ShouldBeTrue(streamed.Message);
        (streamed.EventsChecked, streamed.AnchorsChecked).ShouldBe((5L, 1));

        // Read in pieces smaller than a record, as from a slow disk.
        (await AuditExportVerifier.VerifyAsync(new TrickleStream(bytes, 7), ct: Ct)).ShouldBe(whole);
    }

    [Fact]
    public async Task A_changed_export_fails_alike_whole_or_a_record_at_a_time()
    {
        var events = Chain(4);
        var changed = events.Select(e => e.ChainIndex == 2 ? e with { Summary = "Nothing happened" } : e).ToList();
        var bytes = await WriteAsync(changed, []);
        var streamed = await AuditExportVerifier.VerifyAsync(new MemoryStream(bytes), ct: Ct);
        streamed.ShouldBe(AuditExportVerifier.Verify(JsonSerializerRead(bytes)));
        streamed.Message.ShouldBe("The chain is broken at position 2: the record there was changed, removed or moved.");

        // An anchor of the auditor's own that does not match.
        var remade = await AuditExportVerifier.VerifyAsync(new MemoryStream(await WriteAsync(events, [])), [new AuditAnchor(3, new string('B', 64), null, DateTimeOffset.UtcNow)], Ct);
        remade.Valid.ShouldBeFalse();
        remade.Message.ShouldStartWith("The record at position 3 is not the one anchored on");

        // Cut short.
        var whole = await WriteAsync(events, []);
        var cut = await AuditExportVerifier.VerifyAsync(new MemoryStream(whole[..(whole.Length / 2)]), ct: Ct);
        cut.Valid.ShouldBeFalse();
        cut.Message.ShouldBe("The export ends part-way: it was cut short.");

        // Not an export.
        (await AuditExportVerifier.VerifyAsync(new MemoryStream(Encoding.UTF8.GetBytes("{\"format\":\"something/1\",\"fromIndex\":1,\"toIndex\":1,\"events\":[]}")), ct: Ct))
            .Message.ShouldBe("This is not an HC360 audit export (something/1).");
    }

    private static AuditPurgeFacts Facts() => new(Cutoff, Months: 2, Rows: 100, Unsealed: 0, FirstRemoved: 1, LastRemoved: 100, FirstKept: 101, ExpectedFirst: 1,
        InDefaultBefore: 0);

    private static AuditPurge Purge() => new()
    {
        Id = Guid.Parse("0193a0d1-0000-7000-8000-000000000001"),
        PurgedAt = new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero),
        RetentionYears = 8,
        CutoffMonth = Cutoff,
        Months = "2015-03,2015-04",
        RowCount = 3,
        FirstChainIndex = 1,
        BoundaryChainIndex = 3,
        BoundaryHash = new string('A', 64),
        ExportFile = "hc360-audit-purge-1-3-20261107100000.json",
        ExportSha256 = new string('B', 64),
        ApprovalId = Guid.Parse("0193a0d1-0000-7000-8000-000000000002"),
        RequestedBy = Guid.Parse("0193a0d1-0000-7000-8000-000000000003"),
        RequestedByName = "Super Administrator",
        ApprovedBy = Guid.Parse("0193a0d1-0000-7000-8000-000000000004"),
        ApprovedByName = "Second administrator",
    };

    /// <summary>A sealed chain from position 1, as exported.</summary>
    private static List<AuditExportEvent> Chain(int count)
    {
        var events = new List<AuditExportEvent>();
        string? previous = null;
        for (var i = 1; i <= count; i++)
        {
            var e = new AuditEvent
            {
                Id = 100 + i,
                OccurredAt = new DateTimeOffset(2015, 3, i, 9, 0, 0, TimeSpan.Zero),
                Category = AuditCategories.PhiAccess,
                Action = "patient.accessed",
                UserName = "doctor",
                Summary = $"Viewed the discharge summary ({i})",
                ChainIndex = i,
                PreviousHash = previous,
            };
            e.Hash = AuditEvent.ComputeHash(previous, e.CanonicalContent());
            previous = e.Hash;
            events.Add(AuditExportEvent.From(e));
        }

        return events;
    }

    private static async Task<byte[]> WriteAsync(IReadOnlyList<AuditExportEvent> events, IReadOnlyList<AuditAnchor> anchors)
    {
        using var stream = new MemoryStream();
        (await AuditExportWriter.WriteAsync(stream, DateTimeOffset.UtcNow, "admin", events[0].ChainIndex, events[^1].ChainIndex, anchors, events.ToAsyncEnumerable(),
            Ct)).ShouldBe(events.Count);
        return stream.ToArray();
    }

    private static AuditExport JsonSerializerRead(byte[] bytes) =>
        System.Text.Json.JsonSerializer.Deserialize<AuditExport>(bytes, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    /// <summary>A stream that gives at most a few bytes a read.</summary>
    private sealed class TrickleStream(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
