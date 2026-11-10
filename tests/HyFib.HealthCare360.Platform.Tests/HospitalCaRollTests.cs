using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// The hospital CA is rolled over in two steps, so no device is set up again (docs/deployment/CA-ROLLOVER.md).
/// 'certificates prepare-ca' makes the next CA (the current one's name, a new key) and a certificate the current CA signs for
/// its key; the server keeps serving with the current CA and hands both out. 'certificates switch-ca' then makes the next CA
/// the server's, and the server hands out the CA it replaced until that expires. The server only ever sends its own
/// certificate. Over real TLS to Kestrel, as HC360 serves it (<see cref="HospitalHttps"/>, the routes it hands its CAs out
/// at), devices check the server with the code HC360 Desktop and the lab agent check it with (<see cref="HospitalCaPins"/>).
/// </summary>
public sealed class HospitalCaRollTests : IDisposable
{
    private const string Password = "standby-transfer-2026";

    /// <summary>When the current CA was made: nine and a half years ago, so it is in its last year, as a CA is when it is rolled over.</summary>
    private static readonly DateTimeOffset Made = DateTimeOffset.UtcNow.AddYears(-9).AddMonths(-6);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hc360-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Preparing_keeps_the_server_on_its_CA_and_hands_out_the_next_one_with_the_current_CAs_word_for_it()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var current = node.Ca(Made);

        var prepared = LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);

        (prepared.CurrentFingerprint, prepared.CurrentExpiresAt).ShouldBe((current.Fingerprint, current.ExpiresAt));
        prepared.Fingerprint.ShouldNotBe(current.Fingerprint);
        prepared.ExpiresAt.ShouldBeGreaterThan(now.AddYears(10).AddDays(-2), "valid for ten years");
        File.Exists(Path.Combine(node.Directories.Certificates, "hospital-ca-next.pfx.protected")).ShouldBeTrue("the next CA's key, protected for this machine");

        using var server = node.Start(now);
        (server.CaFingerprint, server.NextCaFingerprint, server.PreviousCaFingerprint).ShouldBe((current.Fingerprint, prepared.Fingerprint, null));
        using var currentCa = X509Certificate2.CreateFromPem(current.Pem);
        using var nextCa = X509Certificate2.CreateFromPem(server.NextCaPem!);
        using var cross = X509Certificate2.CreateFromPem(server.NextCaCrossPem!);
        nextCa.SubjectName.RawData.ShouldBe(currentCa.SubjectName.RawData, "the same name, so the certificate the current CA signs for it is self-issued");
        nextCa.IssuerName.RawData.ShouldBe(nextCa.SubjectName.RawData, "a CA of its own");
        nextCa.PublicKey.EncodedKeyValue.RawData.ShouldNotBe(currentCa.PublicKey.EncodedKeyValue.RawData, "a new key");
        nextCa.Extensions.OfType<X509BasicConstraintsExtension>().Single().PathLengthConstraint.ShouldBe(0, "it issues server certificates only, as the current one");
        cross.SubjectName.RawData.ShouldBe(nextCa.SubjectName.RawData);
        cross.PublicKey.EncodedKeyValue.RawData.ShouldBe(nextCa.PublicKey.EncodedKeyValue.RawData, "for the next CA's key");
        cross.NotAfter.ShouldBe(currentCa.NotAfter, "valid as long as the current CA, and no longer");
        cross.Extensions.OfType<X509AuthorityKeyIdentifierExtension>().Single().KeyIdentifier!.Value.ToArray()
            .ShouldBe(currentCa.Extensions.OfType<X509SubjectKeyIdentifierExtension>().Single().SubjectKeyIdentifierBytes.ToArray(), "it names the current key as its issuer's");
        HospitalCaSuccession.Succeeds(currentCa, nextCa, [cross], now).ShouldBeTrue("signed by the current CA: its word, which devices check");

        await using var app = await HospitalCaTestServer.ServeAsync(server);
        using var pinnedToCurrent = new HospitalCaPins(current.Pem);
        var connection = await HospitalCaTestServer.ConnectAsync(app, pinnedToCurrent);
        connection.Trusted.ShouldBeTrue("the server keeps serving with the current CA: nothing changes for any device");
        connection.Sent.ShouldBeEmpty("the server sends its own certificate alone");
        using var pinnedToNextAlone = new HospitalCaPins(server.NextCaPem!);
        (await HospitalCaTestServer.TrustsAsync(app, pinnedToNextAlone)).ShouldBeFalse("the next CA issues nothing before the switch");

        // Handed out over the connection the devices pin, the next CA first, then the current CA's word for it.
        using var http = HospitalCaTestServer.PinnedClient(app, pinnedToCurrent);
        var handedOut = new X509Certificate2Collection();
        handedOut.ImportFromPem(await http.GetStringAsync(HospitalCaFollow.NextCaPath, Ct));
        handedOut.Select(c => c.RawData).ShouldBe([nextCa.RawData, cross.RawData]);
    }

    /// <summary>Why the switch waits for every device: a device that holds the current CA alone no longer trusts the server.</summary>
    [Fact]
    public async Task After_the_switch_a_device_that_holds_the_next_CA_trusts_the_server_and_one_that_holds_only_the_old_CA_does_not()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var old = node.Ca(Made);
        var stranger = Node("other-hospital").Ca(now);
        var prepared = LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        string nextPem;
        using (var preparedServer = node.Start(now))
        {
            nextPem = preparedServer.NextCaPem!;
        }

        var switched = LanCertificateManager.SwitchCa(node.Directories, node.Keys, now);

        (switched.Fingerprint, switched.PreviousFingerprint, switched.PreviousExpiresAt).ShouldBe((prepared.Fingerprint, old.Fingerprint, old.ExpiresAt));
        File.Exists(Path.Combine(node.Directories.Certificates, switched.PreviousKeptAs.ShouldNotBeNull())).ShouldBeTrue("the CA it replaced is kept, never deleted");
        File.Exists(Path.Combine(node.Directories.Certificates, "hospital-ca-next.pfx.protected")).ShouldBeFalse("no next CA is prepared any more");
        File.Exists(Path.Combine(node.Directories.Certificates, "server.pfx.protected")).ShouldBeFalse("the server certificate the old CA issued goes with it");

        using var server = node.Start(now);
        (server.CaFingerprint, server.NextCaFingerprint).ShouldBe((prepared.Fingerprint, null));
        (server.PreviousCaFingerprint, server.PreviousCaExpiresAt).ShouldBe((old.Fingerprint, old.ExpiresAt), "handed out until it expires");
        await using var app = await HospitalCaTestServer.ServeAsync(server);

        using var holdsBoth = new HospitalCaPins(old.Pem, nextCaPem: nextPem);
        using var holdsOldAlone = new HospitalCaPins(old.Pem);
        using var holdsNewAlone = new HospitalCaPins(nextPem);
        using var holdsAnother = new HospitalCaPins(stranger.Pem, nextCaPem: old.Pem);
        var both = await HospitalCaTestServer.ConnectAsync(app, holdsBoth);
        both.Trusted.ShouldBeTrue("a device that took the next CA before the switch trusts the server after it");
        both.Sent.ShouldBeEmpty("through the next CA it holds itself: the server sends nothing for it");
        (await HospitalCaTestServer.TrustsAsync(app, holdsOldAlone)).ShouldBeFalse("a device that never took the next CA is cut off: the switch waits for it");
        (await HospitalCaTestServer.TrustsAsync(app, holdsNewAlone)).ShouldBeTrue("a device set up after the switch");
        (await HospitalCaTestServer.TrustsAsync(app, holdsAnother)).ShouldBeFalse("another CA is trusted no more after a switch than before");
    }

    /// <summary>
    /// A standby serves with the CA it was given until it is given the new one: devices that switched keep the old CA as a
    /// second pin, so a failover in between cuts none of them off. A next CA prepared on the primary stays there.
    /// </summary>
    [Fact]
    public async Task A_standby_not_switched_yet_serves_the_devices_that_switched_and_after_the_import_serves_with_the_new_CA()
    {
        var primary = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var old = primary.Ca(Made);
        var standby = Node("standby");
        standby.Ca(now);
        LanCertificateManager.ImportCa(standby.Directories, standby.Keys, LanCertificateManager.ExportCa(primary.Directories, primary.Keys, Password).Pkcs12, Password, now);

        var prepared = LanCertificateManager.PrepareCa(primary.Directories, primary.Keys, now);
        string nextPem;
        using (var preparedServer = primary.Start(now))
        {
            nextPem = preparedServer.NextCaPem!;
        }

        var duringPreparation = LanCertificateManager.ExportCa(primary.Directories, primary.Keys, Password);
        (duringPreparation.Fingerprint, duringPreparation.NextCaLeftHere).ShouldBe((old.Fingerprint, true), "the CA the standby serves with; the next one stays");
        var spare = Node("spare");
        spare.Ca(now);
        LanCertificateManager.ImportCa(spare.Directories, spare.Keys, duringPreparation.Pkcs12, Password, now);
        using (var spareServer = spare.Start(now))
        {
            (spareServer.CaFingerprint, spareServer.NextCaFingerprint).ShouldBe((old.Fingerprint, null));
        }

        LanCertificateManager.SwitchCa(primary.Directories, primary.Keys, now);
        using var switchedDevice = new HospitalCaPins(nextPem, previousCaPem: old.Pem);
        using var notSwitchedYet = new HospitalCaPins(old.Pem, nextCaPem: nextPem);
        using (var primaryServer = primary.Start(now))
        using (var standbyServer = standby.Start(now))
        {
            standbyServer.CaFingerprint.ShouldBe(old.Fingerprint, "not given the new CA yet");
            await using var primaryApp = await HospitalCaTestServer.ServeAsync(primaryServer);
            await using var standbyApp = await HospitalCaTestServer.ServeAsync(standbyServer);
            foreach (var device in new[] { switchedDevice, notSwitchedYet })
            {
                (await HospitalCaTestServer.TrustsAsync(primaryApp, device)).ShouldBeTrue();
                (await HospitalCaTestServer.TrustsAsync(standbyApp, device)).ShouldBeTrue("a failover to the standby cuts no device off");
            }
        }

        var afterSwitch = LanCertificateManager.ExportCa(primary.Directories, primary.Keys, Password);
        (afterSwitch.Fingerprint, afterSwitch.PreviousCaExpiresAt, afterSwitch.NextCaLeftHere).ShouldBe((prepared.Fingerprint, old.ExpiresAt, false));
        var imported = LanCertificateManager.ImportCa(standby.Directories, standby.Keys, afterSwitch.Pkcs12, Password, now);
        (imported.Fingerprint, imported.Changed, imported.PreviousCaExpiresAt).ShouldBe((prepared.Fingerprint, true, old.ExpiresAt));
        using var standbySwitched = standby.Start(now);
        (standbySwitched.CaFingerprint, standbySwitched.PreviousCaFingerprint).ShouldBe((prepared.Fingerprint, old.Fingerprint), "it hands out the CA the new one replaced, as the primary does");
        await using var app = await HospitalCaTestServer.ServeAsync(standbySwitched);
        (await HospitalCaTestServer.TrustsAsync(app, switchedDevice)).ShouldBeTrue();
        using var holdsOldAlone = new HospitalCaPins(old.Pem);
        (await HospitalCaTestServer.TrustsAsync(app, holdsOldAlone)).ShouldBeFalse();
    }

    [Fact]
    public void Preparing_is_refused_for_an_expired_CA_while_a_next_CA_is_prepared_and_while_devices_keep_the_CA_the_last_switch_replaced()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var current = node.Ca(Made);

        var expired = Should.Throw<InvalidOperationException>(() => LanCertificateManager.PrepareCa(node.Directories, node.Keys, current.ExpiresAt.AddDays(1)));
        expired.Message.ShouldContain("an expired CA cannot vouch for the next one");
        expired.Message.ShouldContain("certificates new-ca");

        var prepared = LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        var again = Should.Throw<InvalidOperationException>(() => LanCertificateManager.PrepareCa(node.Directories, node.Keys, now.AddDays(30)));
        again.Message.ShouldContain($"The next CA, {prepared.Fingerprint}, is prepared already");
        LanCertificateManager.PreparedNextCaFingerprint(node.Directories, node.Keys, now).ShouldBe(prepared.Fingerprint, "nothing changed");

        LanCertificateManager.SwitchCa(node.Directories, node.Keys, now);
        var whileKept = Should.Throw<InvalidOperationException>(() => LanCertificateManager.PrepareCa(node.Directories, node.Keys, now.AddDays(30)));
        whileKept.Message.ShouldContain($"until it expires on {current.ExpiresAt:dd MMM yyyy}, and hold two at most");

        // Once the CA the switch replaced has expired, the next rollover can begin.
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, current.ExpiresAt.AddDays(1)).CurrentFingerprint.ShouldBe(prepared.Fingerprint);
    }

    [Fact]
    public void Switching_is_refused_with_no_next_CA_and_with_a_next_CA_the_current_one_did_not_vouch_for()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        node.Ca(Made);
        Should.Throw<InvalidOperationException>(() => LanCertificateManager.SwitchCa(node.Directories, node.Keys, now))
            .Message.ShouldContain("No next CA is prepared");

        // The next CA another CA prepared, copied in beside this one (its files, on the same machine's keys): left aside.
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        var other = node.CopyAs(_root, "other");
        LanCertificateManager.NewCa(other.Directories, other.Keys, "Nana Hospitals", now);
        foreach (var file in new[] { "hospital-ca-next.pfx.protected", "hospital-ca-cross.crt" })
        {
            File.Copy(Path.Combine(node.Directories.Certificates, file), Path.Combine(other.Directories.Certificates, file));
        }

        using (var otherServer = other.Start(now))
        {
            otherServer.NextCaFingerprint.ShouldBeNull("its CA did not sign the certificate for that key: no device would take it");
        }

        LanCertificateManager.PreparedNextCaFingerprint(other.Directories, other.Keys, now).ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => LanCertificateManager.SwitchCa(other.Directories, other.Keys, now))
            .Message.ShouldContain("No next CA is prepared");
    }

    /// <summary>
    /// A switch held up until the current CA expired: the server does not start with an expired CA, and says to switch, which
    /// the devices that hold the next CA need nothing more for. No device that holds it is set up again.
    /// </summary>
    [Fact]
    public async Task A_server_whose_CA_expired_before_the_switch_says_to_switch_and_once_switched_serves_the_devices_that_hold_the_next_CA()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var made = now.AddYears(-10).AddDays(-1);
        var old = node.Ca(made);
        old.ExpiresAt.ShouldBeLessThan(now, "expired yesterday");
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, made.AddYears(9).AddMonths(6));
        string nextPem;
        using (var preparedServer = node.Start(made.AddYears(9).AddMonths(6)))
        {
            nextPem = preparedServer.NextCaPem!;
        }

        var refused = Should.Throw<InvalidOperationException>(() => node.Start(now));
        refused.Message.ShouldContain($"the hospital CA expired on {old.ExpiresAt:dd MMM yyyy}");
        refused.Message.ShouldContain("The next CA is prepared: switch to it with 'HyFib.HealthCare360.Server.exe certificates switch-ca --confirm'");
        LanCertificateManager.PreparedNextCaFingerprint(node.Directories, node.Keys, now).ShouldNotBeNull("the switch-ca command finds it, to check the devices");

        LanCertificateManager.SwitchCa(node.Directories, node.Keys, now);

        using var server = node.Start(now);
        server.PreviousCaFingerprint.ShouldBeNull("an expired CA is handed out to no one");
        await using var app = await HospitalCaTestServer.ServeAsync(server);
        using var heldTheNext = new HospitalCaPins(old.Pem, nextCaPem: nextPem);
        (await HospitalCaTestServer.TrustsAsync(app, heldTheNext)).ShouldBeTrue();
    }

    /// <summary>A new CA, for a key that may have been seen, ends any rollover: whoever holds the old key could sign a CA of their own.</summary>
    [Fact]
    public void A_new_CA_ends_a_rollover_prepared_or_switched()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        node.Ca(Made);
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);

        LanCertificateManager.NewCa(node.Directories, node.Keys, "Nana Hospitals", now);

        using (var server = node.Start(now))
        {
            (server.NextCaFingerprint, server.NextCaPem, server.NextCaCrossPem).ShouldBe((null, null, null));
        }

        File.Exists(Path.Combine(node.Directories.Certificates, "hospital-ca-next.pfx.protected")).ShouldBeFalse();
        File.Exists(Path.Combine(node.Directories.Certificates, "hospital-ca-cross.crt")).ShouldBeFalse();

        var switching = Node("switching");
        switching.Ca(Made);
        LanCertificateManager.PrepareCa(switching.Directories, switching.Keys, now);
        LanCertificateManager.SwitchCa(switching.Directories, switching.Keys, now);

        // Within a second of the switch, as a script may run them: each CA replaced is kept under a name of its own.
        LanCertificateManager.NewCa(switching.Directories, switching.Keys, "Nana Hospitals", now);

        Directory.GetFiles(switching.Directories.Certificates, "hospital-ca.pfx.protected.replaced-*").Length.ShouldBe(2, "both CAs it replaced are kept");
        File.Exists(Path.Combine(switching.Directories.Certificates, "hc360-hospital-ca-previous.crt")).ShouldBeFalse();
        using var restarted = switching.Start(now);
        (restarted.PreviousCaFingerprint, restarted.PreviousCaPem).ShouldBe((null, null));
    }

    [Fact]
    public void Once_the_CA_the_switch_replaced_expires_the_server_stops_handing_it_out()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var old = node.Ca(Made);
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        LanCertificateManager.SwitchCa(node.Directories, node.Keys, now);
        var clock = new RolloverClock(now);
        using var server = new LanCertificateManager(
            node.Directories, new PlatformOptions { DataDirectory = node.Directories.Root }, node.Keys, NullLogger<LanCertificateManager>.Instance, clock);
        server.EnsureCertificates();
        server.PreviousCaFingerprint.ShouldBe(old.Fingerprint);

        // The running server's own check, the day after the previous CA expired.
        clock.Now = old.ExpiresAt.AddDays(1);
        server.RefreshIfNeeded();

        (server.PreviousCaFingerprint, server.PreviousCaPem, server.PreviousCaExpiresAt).ShouldBe((null, null, null));
        using var restarted = node.Start(clock.Now);
        restarted.PreviousCaFingerprint.ShouldBeNull("nor at its next start");
    }

    /// <summary>
    /// The outside probe (CA-ROLLOVER.md) sees the rollover as a device does: in the CA's last year it says to prepare the next
    /// CA, then, once prepared, to switch to it; pinned to the CA a switch replaced it is critical and says to give it the new
    /// fingerprint, which it is well with.
    /// </summary>
    [Fact]
    public async Task The_probe_says_to_prepare_then_to_switch_and_once_switched_to_be_given_the_new_CA()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var old = node.Ca(Made);

        ProbeResult before;
        using (var server = node.Start(now))
        await using (var app = await HospitalCaTestServer.ServeAsync(server))
        {
            before = await ProbeAsync(app, old.Fingerprint, now);
        }

        var prepared = LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        ProbeResult whilePrepared;
        using (var server = node.Start(now))
        await using (var app = await HospitalCaTestServer.ServeAsync(server))
        {
            whilePrepared = await ProbeAsync(app, old.Fingerprint, now);
        }

        LanCertificateManager.SwitchCa(node.Directories, node.Keys, now);
        ProbeResult byOldPin, byNewPin;
        using (var server = node.Start(now))
        await using (var app = await HospitalCaTestServer.ServeAsync(server))
        {
            byOldPin = await ProbeAsync(app, old.Fingerprint, now);
            byNewPin = await ProbeAsync(app, prepared.Fingerprint, now);
        }

        before.Outcome.ShouldBe(CertificateProbeOutcome.Warning, before.Said);
        before.Said.ShouldContain("Prepare the next CA with 'certificates prepare-ca'");
        whilePrepared.Outcome.ShouldBe(CertificateProbeOutcome.Warning, whilePrepared.Said);
        whilePrepared.Said.ShouldContain("The next CA is prepared: switch to it with 'certificates switch-ca'");
        byOldPin.Outcome.ShouldBe(CertificateProbeOutcome.Critical, byOldPin.Said);
        byOldPin.Said.ShouldContain($"The hospital CA was switched: the server's CA is now {CertificateProbe.Group(prepared.Fingerprint)}");
        byNewPin.Outcome.ShouldBe(CertificateProbeOutcome.Ok, byNewPin.Said);
    }

    /// <summary>Prepared well before the CA's last year: the probe is well, and says the next CA is prepared.</summary>
    [Fact]
    public async Task Pinned_to_the_current_CA_the_probe_says_a_next_CA_is_prepared()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var current = node.Ca(now.AddYears(-1));
        var prepared = LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        using var server = node.Start(now);
        await using var app = await HospitalCaTestServer.ServeAsync(server);

        var probed = await ProbeAsync(app, current.Fingerprint, now);

        probed.Outcome.ShouldBe(CertificateProbeOutcome.Ok, probed.Said);
        probed.Said.ShouldContain($"The next CA, {CertificateProbe.Group(prepared.Fingerprint)}, is prepared");
    }

    private static async Task<ProbeResult> ProbeAsync(WebApplication app, string pinned, DateTimeOffset now)
    {
        var result = await CertificateProbe.ProbeAsync(new Uri(app.Urls.Single()), pinned, null, now, Ct);
        return new ProbeResult(result.Outcome, string.Join(" ", result.Findings));
    }

    private HospitalCaTestServer Node(string name) => new(_root, name);

    private sealed record ProbeResult(CertificateProbeOutcome Outcome, string Said);
}
