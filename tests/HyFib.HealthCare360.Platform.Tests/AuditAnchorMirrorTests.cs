using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The audit chain's anchors copied off the server (CERT-In; SRS SEC-09): each is written to a second anchor file elsewhere
/// as it is made, one missed while that was away is copied once it is back, a copy that disagrees with the anchor file is
/// said loudly, and System health is told while a copy fails.
/// </summary>
public sealed class AuditAnchorMirrorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-anchors-" + Guid.NewGuid().ToString("N"));

    public AuditAnchorMirrorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Each_anchor_is_copied_to_the_second_file_and_one_missed_while_it_was_away_follows()
    {
        var share = Path.Combine(_root, "share");
        var (anchors, mirror) = Make(Path.Combine(share, "anchors.log"));

        anchors.Append(Anchor(1));
        await mirror.SyncAsync(TestContext.Current.CancellationToken);
        AuditAnchors.ReadFile(mirror.MirrorFile!).Anchors.ShouldBe([Anchor(1)]);
        mirror.Status.Targets.ShouldHaveSingleItem().FailingSince.ShouldBeNull();

        // The share goes away (a file where its folder should be): the anchor is kept here, and the copy fails.
        Directory.Delete(share, recursive: true);
        await File.WriteAllTextAsync(share, "not a folder", TestContext.Current.CancellationToken);
        anchors.Append(Anchor(2));
        await mirror.SyncAsync(TestContext.Current.CancellationToken);
        var failing = mirror.Status.Targets.ShouldHaveSingleItem();
        failing.FailingSince.ShouldNotBeNull();
        failing.Error.ShouldNotBeNullOrEmpty();
        anchors.Read().Anchors.Count.ShouldBe(2, "the anchor file on the server has it whatever happens to the copy");

        var warning = AuditAnchorMirrorWarnings.Warnings(mirror.Status, Live(), failing.FailingSince!.Value.AddHours(1)).ShouldHaveSingleItem();
        warning.Code.ShouldBe("audit.anchor_mirror_failed");
        warning.Severity.ShouldBe("warning");
        warning.Message.ShouldContain(share);
        AuditAnchorMirrorWarnings.Warnings(mirror.Status, Live(), failing.FailingSince!.Value.AddDays(1)).ShouldHaveSingleItem()
            .Severity.ShouldBe("critical", "a day without a copy off the server");

        // Back: both anchors are there, each once, in order.
        File.Delete(share);
        anchors.Append(Anchor(3));
        await mirror.SyncAsync(TestContext.Current.CancellationToken);
        AuditAnchors.ReadFile(mirror.MirrorFile!).Anchors.ShouldBe([Anchor(1), Anchor(2), Anchor(3)]);
        mirror.Status.Targets.ShouldHaveSingleItem().FailingSince.ShouldBeNull();
        AuditAnchorMirrorWarnings.Warnings(mirror.Status, Live(), Now).ShouldBeEmpty();

        await mirror.SyncAsync(TestContext.Current.CancellationToken);
        AuditAnchors.ReadFile(mirror.MirrorFile!).Anchors.Count.ShouldBe(3, "nothing is copied twice");
    }

    [Fact]
    public async Task A_copy_holding_an_anchor_the_anchor_file_no_longer_has_is_a_critical_warning()
    {
        var (anchors, mirror) = Make(Path.Combine(_root, "share", "anchors.log"));
        anchors.Append(Anchor(1));
        anchors.Append(Anchor(2));
        await mirror.SyncAsync(TestContext.Current.CancellationToken);

        // Someone who holds the server rewrites its anchor file from position 2, to match a chain made again from there.
        var path = Path.Combine(_root, "data", "audit", "anchors.log");
        await File.WriteAllLinesAsync(path, [(await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken))[0]], TestContext.Current.CancellationToken);
        AuditAnchors.AppendTo(path, [Anchor(2) with { Hash = new string('e', 64) }]);
        await mirror.SyncAsync(TestContext.Current.CancellationToken);

        mirror.Status.Mismatch.ShouldNotBeNull().ShouldContain("chain position 2");
        var warning = AuditAnchorMirrorWarnings.Warnings(mirror.Status, Live(), Now).ShouldHaveSingleItem();
        warning.Code.ShouldBe("audit.anchor_mirror_mismatch");
        warning.Severity.ShouldBe("critical");
    }

    [Fact]
    public async Task Anchors_sent_to_the_syslog_collector_are_sent_again_until_one_takes_them()
    {
        using var ca = SyslogTests.Ca("HC360 anchors CA");
        var caFile = Path.Combine(_root, "ca.cer");
        await File.WriteAllBytesAsync(caFile, ca.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert), TestContext.Current.CancellationToken);
        var port = SyslogTests.FreePort();
        var options = Options(null);
        options.Audit.AnchorsToSyslog = true;
        options.Syslog = new SyslogOptions { Enabled = true, Host = "localhost", Port = port, CaCertificatePath = caFile };
        await using var syslog = new SyslogClient(options.Syslog, TimeProvider.System);
        var mirror = new AuditAnchorMirror(new DataDirectories(options), Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System, syslog);
        var anchors = new AuditAnchors(new DataDirectories(options), NullLogger<AuditAnchors>.Instance, mirror);

        // Nobody listens yet.
        anchors.Append(Anchor(1));
        await mirror.SyncAsync(TestContext.Current.CancellationToken);
        mirror.Status.Targets.ShouldHaveSingleItem().FailingSince.ShouldNotBeNull();
        AuditAnchorMirrorWarnings.Warnings(mirror.Status, options, Now).ShouldHaveSingleItem().Message.ShouldContain("the syslog collector");

        // The collector is up on that port: the anchor written while it was away goes, then the next.
        await using var collector = SyslogTests.Collector.Start(SyslogTests.Issue(ca, "localhost"), port);
        anchors.Append(Anchor(2));
        await mirror.SyncAsync(TestContext.Current.CancellationToken);

        var first = await collector.ReadAsync();
        first.ShouldStartWith("<109>1 ", customMessage: "log audit (13) * 8 + notice (5)");
        first.ShouldContain(" AUDIT_ANCHOR [hc360@32473 chainIndex=\"1\" hash=\"" + Anchor(1).Hash + "\"");
        (await collector.ReadAsync()).ShouldContain("chainIndex=\"2\"");
        mirror.Status.Targets.ShouldHaveSingleItem().FailingSince.ShouldBeNull();
    }

    [Fact]
    public void A_live_server_that_copies_its_anchors_nowhere_is_told_so()
    {
        var off = new AnchorMirrorStatus(false, [], null);
        AuditAnchorMirrorWarnings.Warnings(off, Live(), Now).ShouldHaveSingleItem().Code.ShouldBe("audit.anchor_mirror_off");
        AuditAnchorMirrorWarnings.Warnings(off, new PlatformOptions { SeedDemoData = true }, Now).ShouldBeEmpty("a training server");
        AuditAnchorMirrorWarnings.Warnings(off, new PlatformOptions { Role = "api" }, Now).ShouldBeEmpty("said once, by the node that runs the jobs");
    }

    private static PlatformOptions Live() => new();

    /// <summary>
    /// The anchor a restore writes names a position, and hash, anchored already: it is copied all the same, so the copy off
    /// the server shows the restore that sets the anchors after it aside (AuditRestores), and nothing disagrees.
    /// </summary>
    [Fact]
    public async Task A_restore_anchor_of_a_position_anchored_already_is_copied_too()
    {
        var (anchors, mirror) = Make(Path.Combine(_root, "share", "anchors.log"));
        anchors.Append(Anchor(1));
        anchors.Append(Anchor(2));
        await mirror.SyncAsync(TestContext.Current.CancellationToken);

        var restore = Anchor(1) with { AnchoredAt = Now.AddDays(1), Restore = new AuditRestoreMark(Now.AddDays(1), "hc360-20261108-200000-scheduled.hc360bak", 2, new string('0', 64)) };
        anchors.Append(restore);
        await mirror.SyncAsync(TestContext.Current.CancellationToken);

        AuditAnchors.ReadFile(mirror.MirrorFile!).Anchors.ShouldBe([Anchor(1), Anchor(2), restore]);
        mirror.Status.Mismatch.ShouldBeNull();
    }

    private static AuditAnchor Anchor(long index) =>
        new(index, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(index))), "mac" + index, Now.AddHours(index));

    private PlatformOptions Options(string? mirrorFile)
    {
        var options = new PlatformOptions { DataDirectory = Path.Combine(_root, "data") };
        options.Audit.AnchorMirrorFile = mirrorFile;
        return options;
    }

    private (AuditAnchors Anchors, AuditAnchorMirror Mirror) Make(string mirrorFile)
    {
        var options = Options(mirrorFile);
        var directories = new DataDirectories(options);
        var mirror = new AuditAnchorMirror(directories, Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
        return (new AuditAnchors(directories, NullLogger<AuditAnchors>.Instance, mirror), mirror);
    }
}
