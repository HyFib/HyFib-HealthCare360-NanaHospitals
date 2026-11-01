using System.Net;
using System.Security.Cryptography.X509Certificates;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace HyFib.HealthCare360.Platform.Tests;

/// <summary>
/// HC360 Desktop and the lab agent follow the hospital CA's two-step rollover by themselves (<see cref="HospitalCaFollow"/>):
/// once IT prepared the next CA they hold it as a second pin, on the word of the CA they pin; once the server switched to it
/// they pin it, and keep the CA it replaced as a second pin until it expires. Over real TLS to Kestrel, as HC360 serves it, with
/// the routes it hands its CAs out at and the code both apps follow with; their own few lines around it are tested on Windows.
/// </summary>
public sealed class HospitalCaFollowTests : IDisposable
{
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
    public async Task A_device_holds_the_next_CA_on_the_current_CAs_word_then_switches_to_it_with_the_server()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var old = node.Ca(Made);
        using var pins = new HospitalCaPins(old.Pem);
        using var oldCa = X509Certificate2.CreateFromPem(old.Pem);

        (await CheckAsync(node, pins, now)).ShouldBeNull("nothing is prepared: nothing to do");

        var prepared = LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        string nextPem;
        using (var server = node.Start(now))
        await using (var app = await HospitalCaTestServer.ServeAsync(server))
        using (var http = HospitalCaTestServer.PinnedClient(app, pins))
        {
            nextPem = server.NextCaPem!;
            var hold = (await HospitalCaFollow.CheckAsync(http, pins, now, Ct)).ShouldBeOfType<HospitalCaStep.HoldNext>();
            hold.Next.Fingerprint.ShouldBe(prepared.Fingerprint);
            using (var next = X509Certificate2.CreateFromPem(hold.Next.CaPem))
            using (var cross = X509Certificate2.CreateFromPem(hold.Next.CrossPem))
            using (var handedOut = X509Certificate2.CreateFromPem(nextPem))
            {
                next.RawData.ShouldBe(handedOut.RawData);
                HospitalCaSuccession.Succeeded(oldCa, next, cross).ShouldBeTrue("it keeps the current CA's word for the next one");
            }

            pins.Hold(hold.Next);
            pins.Fingerprints(now).ShouldBe([old.Fingerprint, prepared.Fingerprint], "the heartbeat says both, the one it pins first");
            pins.Fingerprint.ShouldBe(old.Fingerprint, "the server still serves with the current CA");
            (await HospitalCaFollow.CheckAsync(http, pins, now, Ct)).ShouldBeNull("it holds it already, and the server has not switched");
        }

        LanCertificateManager.SwitchCa(node.Directories, node.Keys, now);
        using (var server = node.Start(now))
        await using (var app = await HospitalCaTestServer.ServeAsync(server))
        {
            using (var http = HospitalCaTestServer.PinnedClient(app, pins))
            {
                (await HospitalCaFollow.CheckAsync(http, pins, now, Ct)).ShouldBeOfType<HospitalCaStep.SwitchToNext>()
                    .Fingerprint.ShouldBe(prepared.Fingerprint, "asked over the connection the next CA it holds lets through");
                pins.SwitchToNext();
                (await HospitalCaFollow.CheckAsync(http, pins, now, Ct)).ShouldBeNull("nothing more: the server's CA is the one it pins");
            }

            (pins.Fingerprint, pins.NextCa).ShouldBe((prepared.Fingerprint, null));
            pins.PreviousCa.ShouldNotBeNull().RawData.ShouldBe(oldCa.RawData);
            pins.Fingerprints(now).ShouldBe([prepared.Fingerprint, old.Fingerprint], "the CA it replaced, a second pin until it expires");
            pins.Fingerprints(old.ExpiresAt.AddDays(1)).ShouldBe([prepared.Fingerprint], "an expired CA vouches for nothing");

            // One that never took the next CA is refused by the server's certificate, and asks nothing of it.
            using var oldAlone = new HospitalCaPins(old.Pem);
            using var refused = HospitalCaTestServer.PinnedClient(app, oldAlone);
            await Should.ThrowAsync<HttpRequestException>(() => HospitalCaFollow.CheckAsync(refused, oldAlone, now, Ct));
        }
    }

    /// <summary>
    /// A standby serves with the CA it was given until it is given the new one: a device that switched still trusts it, through
    /// the CA it replaced, and does not go back to that CA; one that holds the next CA keeps holding it.
    /// </summary>
    [Fact]
    public async Task A_device_that_switched_trusts_a_standby_not_switched_yet_and_never_goes_back_to_the_old_CA()
    {
        var primary = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var old = primary.Ca(Made);
        var standby = primary.CopyAs(_root, "standby");
        var prepared = LanCertificateManager.PrepareCa(primary.Directories, primary.Keys, now);
        string nextPem;
        using (var server = primary.Start(now))
        {
            nextPem = server.NextCaPem!;
        }

        LanCertificateManager.SwitchCa(primary.Directories, primary.Keys, now);
        using var switched = new HospitalCaPins(nextPem, previousCaPem: old.Pem);
        using var holding = new HospitalCaPins(old.Pem, nextCaPem: nextPem);

        (await CheckAsync(standby, switched, now)).ShouldBeNull("the standby's CA is the one this device replaced: it stays on the new one");
        switched.Fingerprint.ShouldBe(prepared.Fingerprint);
        (await CheckAsync(standby, holding, now)).ShouldBeNull("the standby has not switched, and hands out no next CA");
        holding.NextCa.ShouldNotBeNull("it keeps the next CA it holds: the primary switched to it");
        (await CheckAsync(primary, holding, now)).ShouldBeOfType<HospitalCaStep.SwitchToNext>();
    }

    /// <summary>
    /// What the server hands out as the next CA is held only on the word of the CA the device pins: the next CA without it, a
    /// rollover another CA vouched for (another hospital's, a CA of this one's name), or a page that is no certificate at all
    /// (an older server's answer) changes nothing.
    /// </summary>
    [Fact]
    public async Task A_device_holds_no_next_CA_the_CA_it_pins_did_not_vouch_for()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var current = node.Ca(Made);
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        var other = Node("other-hospital");
        other.Ca(Made);
        LanCertificateManager.PrepareCa(other.Directories, other.Keys, now);
        string nextPem, crossPem, othersNext, othersCross;
        using (var server = node.Start(now))
        using (var otherServer = other.Start(now))
        {
            (nextPem, crossPem) = (server.NextCaPem!, server.NextCaCrossPem!);
            (othersNext, othersCross) = (otherServer.NextCaPem!, otherServer.NextCaCrossPem!);
        }

        using (var nextCa = X509Certificate2.CreateFromPem(nextPem))
        using (var otherCa = X509Certificate2.CreateFromPem(othersNext))
        {
            otherCa.SubjectName.RawData.ShouldBe(nextCa.SubjectName.RawData, "a server of the same name set up the same day: the names say nothing");
        }

        string[] refused =
        [
            nextPem,
            Pem(othersNext, othersCross),
            Pem(nextPem, othersCross),
            "<!doctype html><html><body>HC360</body></html>",
            current.Pem,
        ];
        using var pins = new HospitalCaPins(current.Pem);
        foreach (var handedOut in refused)
        {
            (await CheckAsync(node, pins, now, handedOut)).ShouldBeNull(handedOut[..Math.Min(40, handedOut.Length)]);
        }

        pins.NextCa.ShouldBeNull();
        (await CheckAsync(node, pins, now, Pem(nextPem, crossPem))).ShouldBeOfType<HospitalCaStep.HoldNext>("the real one, as the server hands it out");
        (await CheckAsync(node, pins, now, Pem(crossPem, nextPem))).ShouldBeOfType<HospitalCaStep.HoldNext>("in either order");
    }

    /// <summary>
    /// A certificate this computer holds, but the server did not send, does not count: the chain is built from what the server
    /// sent and the pins only. Windows builds chains from the computer's own stores, and .NET elsewhere from the user's: here,
    /// after a switch, the certificate the old CA signed for the new key is in this user's intermediate store, and a device
    /// that holds the old CA alone still refuses the server.
    /// </summary>
    [Fact]
    public void A_certificate_on_this_computer_that_the_server_did_not_send_does_not_count()
    {
        var node = Node("primary");
        var now = DateTimeOffset.UtcNow;
        var old = node.Ca(Made);
        LanCertificateManager.PrepareCa(node.Directories, node.Keys, now);
        string crossPem;
        using (var prepared = node.Start(now))
        {
            crossPem = prepared.NextCaCrossPem!;
        }

        LanCertificateManager.SwitchCa(node.Directories, node.Keys, now);
        using var server = node.Start(now);
        using var cross = X509Certificate2.CreateFromPem(crossPem);
        using var pins = new HospitalCaPins(old.Pem);
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(cross);
        try
        {
            pins.IsTrusted(server.ServerCertificate, "127.0.0.1", []).ShouldBeFalse("the server sent its certificate alone");
            pins.IsTrusted(server.ServerCertificate, "127.0.0.1", [cross]).ShouldBeTrue("had the server sent it, the old CA's word would let it through");
        }
        finally
        {
            store.Remove(cross);
        }
    }

    /// <summary>A second pin that cannot be read is left out: the device still trusts the server through the first.</summary>
    [Fact]
    public void A_second_pin_that_cannot_be_read_is_left_out()
    {
        var node = Node("primary");
        var current = node.Ca(DateTimeOffset.UtcNow);
        const string broken = "-----BEGIN CERTIFICATE-----\nnot a certificate\n-----END CERTIFICATE-----";
        using var pins = new HospitalCaPins(current.Pem, previousCaPem: broken, nextCaPem: broken);

        (pins.PreviousCa, pins.NextCa).ShouldBe((null, null));
        pins.Fingerprints(DateTimeOffset.UtcNow).ShouldBe([current.Fingerprint]);
    }

    /// <summary>
    /// What HC360 Desktop and the lab agent ask the server, started at <paramref name="now"/>, over a connection pinned to
    /// <paramref name="pins"/>. With <paramref name="nextHandedOut"/> the server hands that out as the next CA instead of its own.
    /// </summary>
    private static async Task<HospitalCaStep?> CheckAsync(HospitalCaTestServer node, HospitalCaPins pins, DateTimeOffset now, string? nextHandedOut = null)
    {
        using var server = node.Start(now);
        await using var app = nextHandedOut is null ? await HospitalCaTestServer.ServeAsync(server) : await ServeHandingOutAsync(server, nextHandedOut);
        using var http = HospitalCaTestServer.PinnedClient(app, pins);
        return await HospitalCaFollow.CheckAsync(http, pins, now, Ct);
    }

    /// <summary>The server's TLS and CA, handing out <paramref name="next"/> where it hands out the next CA.</summary>
    private static async Task<WebApplication> ServeHandingOutAsync(LanCertificateManager certificates, string next)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https => HospitalHttps.Configure(https, certificates))));
        var app = builder.Build();
        app.MapGet("/" + HospitalCaFollow.CaPath, () => Results.Text(certificates.CaPem!, "application/x-x509-ca-cert"));
        app.MapGet("/" + HospitalCaFollow.NextCaPath, () => Results.Text(next, "application/x-pem-file"));
        await app.StartAsync(Ct);
        return app;
    }

    private HospitalCaTestServer Node(string name) => new(_root, name);

    /// <summary>Certificates one after the other, each on lines of its own, as the server hands them out.</summary>
    private static string Pem(params string[] certificates) => string.Join("\n", certificates.Select(c => c.TrimEnd())) + "\n";
}
