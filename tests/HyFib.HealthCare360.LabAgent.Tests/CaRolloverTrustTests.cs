using System.Net;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.WindowsInterop.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.LabAgent.Tests;

/// <summary>
/// The lab agent follows the hospital CA's two-step rollover by itself (docs/deployment/CA-ROLLOVER.md): once IT prepared
/// the next CA ('certificates prepare-ca') it holds it, on the word of the CA it pins; once the server switched to it
/// ('certificates switch-ca') it pins it, keeps the CA it replaced as a second pin, and says both in its heartbeat. The
/// server's own code makes the CAs, Kestrel serves them with HC360's TLS and routes, and the agent's own calls and check
/// (<see cref="ServerApi"/>, <see cref="ServerTrust"/>) go over Windows' TLS, in the Windows CI job.
/// </summary>
public sealed class CaRolloverTrustTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hc360-agent-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task An_agent_holds_the_next_CA_then_switches_to_it_with_the_server_and_saves_each_step()
    {
        var (directories, keys) = Machine("server");
        var now = DateTimeOffset.UtcNow;
        string oldPem;
        using (var first = Start(directories, keys))
        {
            oldPem = first.CaPem!;
        }

        var prepared = LanCertificateManager.PrepareCa(directories, keys, now);
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "agent.json");
        using var trust = new ServerTrust(oldPem);
        var oldFingerprint = trust.Fingerprint;
        var configuration = new AgentConfiguration
        {
            ServerUrl = "https://127.0.0.1:7360/",
            CaCertificatePem = oldPem,
            CaFingerprint = oldFingerprint,
            DeviceId = Guid.CreateVersion7(),
            DeviceSecret = "s3cr3t-device-key",
        };

        using (var server = Start(directories, keys))
        await using (var app = await ServeAsync(server))
        using (var api = new ServerApi(new Uri(app.Urls.Single()), trust))
        {
            var hold = (await api.CheckCaAsync(now, Ct)).ShouldBeOfType<HospitalCaStep.HoldNext>("the current CA vouched for the next one");
            hold.Next.Fingerprint.ShouldBe(prepared.Fingerprint);
            configuration.Take(hold, path);
            trust.Pins.Hold(hold.Next);
            (await api.CheckCaAsync(now, Ct)).ShouldBeNull("held already, and the server has not switched");
        }

        var held = AgentConfiguration.Load(path);
        held.CaFingerprint.ShouldBe(oldFingerprint, "the server still serves with the current CA");
        held.NextCaCertificatePem.ShouldNotBeNull();
        trust.Pins.Fingerprints(now).ShouldBe([oldFingerprint, prepared.Fingerprint], "its heartbeat says both, the one it pins first");

        var switched = LanCertificateManager.SwitchCa(directories, keys, now);
        using (var server = Start(directories, keys))
        await using (var app = await ServeAsync(server))
        {
            using (var api = new ServerApi(new Uri(app.Urls.Single()), trust))
            {
                configuration.Take((await api.CheckCaAsync(now, Ct)).ShouldBeOfType<HospitalCaStep.SwitchToNext>(), path);
                trust.Pins.SwitchToNext();
            }

            var saved = AgentConfiguration.Load(path);
            (saved.CaFingerprint, saved.PreviousCaCertificatePem, saved.NextCaCertificatePem).ShouldBe((switched.Fingerprint, oldPem, null));
            saved.IsEnrolled.ShouldBeTrue();

            // As the agent starts again from what it saved: it trusts the switched server; one that never took the next CA does not.
            using var restarted = new ServerTrust(saved.CaCertificatePem!, saved.PreviousCaCertificatePem, saved.NextCaCertificatePem);
            using (var api = new ServerApi(new Uri(app.Urls.Single()), restarted))
            {
                (await api.CheckCaAsync(now, Ct)).ShouldBeNull("nothing more: the server's CA is the one it pins");
            }

            using var neverTook = new ServerTrust(oldPem);
            using var refused = new ServerApi(new Uri(app.Urls.Single()), neverTook);
            await Should.ThrowAsync<HttpRequestException>(() => refused.CheckCaAsync(now, Ct));
        }

        trust.Pins.Fingerprints(now).ShouldBe([switched.Fingerprint, switched.PreviousFingerprint]);
    }

    [Fact]
    public void A_step_that_cannot_be_saved_leaves_the_configuration_as_it_was()
    {
        var (directories, keys) = Machine("server");
        string oldPem;
        using (var first = Start(directories, keys))
        {
            oldPem = first.CaPem!;
        }

        var prepared = LanCertificateManager.PrepareCa(directories, keys, DateTimeOffset.UtcNow);
        string nextPem, crossPem;
        using (var server = Start(directories, keys))
        {
            (nextPem, crossPem) = (server.NextCaPem!, server.NextCaCrossPem!);
        }

        var configuration = new AgentConfiguration { ServerUrl = "https://127.0.0.1:7360/", CaCertificatePem = oldPem, CaFingerprint = "OLD" };
        var hold = new HospitalCaStep.HoldNext(new HospitalCaNext(nextPem, prepared.Fingerprint, crossPem));

        // A folder where the file should be: it cannot be written.
        var blocked = Path.Combine(_folder, "blocked", "agent.json");
        Directory.CreateDirectory(blocked);
        var failed = Should.Throw<Exception>(() => configuration.Take(hold, blocked));
        (failed is IOException or UnauthorizedAccessException).ShouldBeTrue(failed.GetType().Name);
        configuration.NextCaCertificatePem.ShouldBeNull("not held: the next check holds it again");

        configuration.Take(hold, Path.Combine(_folder, "agent.json"));
        Should.Throw<Exception>(() => configuration.Take(new HospitalCaStep.SwitchToNext(prepared.Fingerprint), blocked));
        (configuration.CaCertificatePem, configuration.CaFingerprint, configuration.NextCaCertificatePem).ShouldBe((oldPem, "OLD", nextPem), "not switched");
    }

    private (DataDirectories Directories, IDataProtectionProvider Keys) Machine(string name) =>
        (new DataDirectories(new PlatformOptions { DataDirectory = Path.Combine(_folder, name) }), new EphemeralDataProtectionProvider());

    private static LanCertificateManager Start(DataDirectories directories, IDataProtectionProvider keys)
    {
        var certificates = new LanCertificateManager(
            directories, new PlatformOptions { DataDirectory = directories.Root }, keys, NullLogger<LanCertificateManager>.Instance);
        certificates.EnsureCertificates();
        return certificates;
    }

    /// <summary>Kestrel with HC360's TLS (<see cref="HospitalHttps"/>) and the routes the server hands its CAs out at.</summary>
    private static async Task<WebApplication> ServeAsync(LanCertificateManager certificates)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(certificates);
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https => HospitalHttps.Configure(https, certificates))));
        var app = builder.Build();
        HospitalCaEndpoints.Map(app.MapGroup("/api/v1/system"));
        await app.StartAsync(Ct);
        return app;
    }
}
