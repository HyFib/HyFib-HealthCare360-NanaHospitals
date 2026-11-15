using System.Security.Cryptography;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// After a restore the anchors written before it go on naming positions past the point restored to, as the chain had them
/// then. The anchor a restore writes sets those aside (<see cref="AuditRestores.InForce"/>), and nothing else: only when it
/// carries this server's seal and the audit trail records the restore. AuditRestoreTests, in the integration tests, restore
/// a real backup and check the whole chain.
/// </summary>
public sealed class AuditRestoreAnchorTests : IDisposable
{
    private static readonly DateTimeOffset RestoredAt = new(2026, 11, 14, 9, 10, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));
    private readonly DataDirectories _directories;
    private readonly AuditSealKey _key;

    public AuditRestoreAnchorTests()
    {
        _directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        _directories.EnsureCreated();
        _key = new AuditSealKey(_directories, new EphemeralDataProtectionProvider());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void A_restore_sets_aside_the_anchors_written_before_it_past_the_point_restored_to()
    {
        var restore = Restore(15);
        var sinceTheRestore = Anchor(18);

        var inForce = AuditRestores.InForce([Anchor(10), Anchor(20), Anchor(30), restore, sinceTheRestore], _key, Recorded(restore));

        inForce.Anchors.ShouldBe([Anchor(10), restore, sinceTheRestore], "the chain up to the point restored to, and the anchors written since, are checked");
        inForce.Restores.ShouldBe([restore]);
        inForce.SetAside.ShouldBe(2);
    }

    [Fact]
    public void A_restore_the_audit_trail_does_not_record_sets_nothing_aside()
    {
        var restore = Restore(15);
        AuditAnchor[] written = [Anchor(10), Anchor(30), restore];

        var inForce = AuditRestores.InForce(written, _key, new HashSet<(long, string)>());

        inForce.Anchors.ShouldBe(written, "the anchor is still checked as an anchor of its position, and the chain must still reach position 30");
        inForce.Restores.ShouldBeEmpty();
    }

    [Fact]
    public void A_restore_mark_this_server_did_not_seal_or_that_was_changed_sets_nothing_aside()
    {
        var otherServer = new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_root, "other") });
        otherServer.EnsureCreated();
        var forged = AuditRestores.Seal(Unsealed(15), new AuditSealKey(otherServer, new EphemeralDataProtectionProvider()));
        var restore = Restore(15);
        var stretched = restore with { Restore = restore.Restore! with { Superseded = 99 } };

        AuditRestores.InForce([Anchor(30), forged], _key, Recorded(forged)).SetAside.ShouldBe(0, "sealed with another key");
        AuditRestores.InForce([Anchor(30), stretched], _key, Recorded(stretched)).SetAside.ShouldBe(0, "its seal covers what it says");
    }

    [Fact]
    public void A_second_restore_sets_aside_what_was_anchored_since_the_first_past_its_own_point()
    {
        var first = Restore(15);
        var second = Restore(20);

        var inForce = AuditRestores.InForce([Anchor(10), Anchor(30), first, Anchor(25), Anchor(40), second, Anchor(22)], _key, Recorded(first, second));

        inForce.Anchors.ShouldBe([Anchor(10), first, second, Anchor(22)]);
        inForce.Restores.ShouldBe([first, second]);
        inForce.SetAside.ShouldBe(3);
    }

    [Fact]
    public void A_restore_anchor_reads_back_with_its_mark_and_other_anchors_are_written_as_before()
    {
        var anchors = new AuditAnchors(_directories, NullLogger<AuditAnchors>.Instance);
        var restore = Restore(5);
        var emptyChain = AuditRestores.Seal(new AuditAnchor(0, new string('0', 64), null, RestoredAt) { Restore = new AuditRestoreMark(RestoredAt, null, 0, string.Empty) }, _key);

        anchors.Append(Anchor(5));
        anchors.Append(restore);
        anchors.Append(emptyChain);

        File.ReadAllLines(anchors.Path)[0].ShouldNotContain("restore", Case.Insensitive, "an anchor that marks no restore is written as anchors always were");
        var (read, unreadable) = anchors.Read();
        read.ShouldBe([Anchor(5), restore, emptyChain], "a restore to a chain with no sealed record is an anchor too");
        unreadable.ShouldBe(0);
        AuditRestores.InForce(read, _key, Recorded(restore, emptyChain)).Anchors.ShouldBeEmpty("a restore to an empty chain sets aside every anchor before it, and anchors no record");
    }

    private static string Hash(long position) => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(position)));

    private static AuditAnchor Anchor(long position) => new(position, Hash(position), null, RestoredAt.AddMinutes(position - 100));

    private static AuditAnchor Unsealed(long position) =>
        new(position, Hash(position), null, RestoredAt.AddMinutes(1)) { Restore = new AuditRestoreMark(RestoredAt, "hc360-20261113-200000-scheduled.hc360bak", 30, string.Empty) };

    private AuditAnchor Restore(long position) => AuditRestores.Seal(Unsealed(position), _key);

    private static HashSet<(long, string)> Recorded(params AuditAnchor[] restores) => [.. restores.Select(a => (a.ChainIndex, a.Hash))];
}
