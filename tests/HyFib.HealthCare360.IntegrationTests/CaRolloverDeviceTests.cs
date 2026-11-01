using System.Net;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using HyFib.HealthCare360.Server;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// While the next hospital CA is prepared ('certificates prepare-ca', docs/deployment/CA-ROLLOVER.md), HC360 Desktop and the
/// lab agent say in their heartbeats which CAs they hold, over the real API. System health names each workstation and lab
/// agent seen in the last 30 days that does not hold the next CA, and 'certificates switch-ca' refuses while there is one,
/// reading the same database: after the switch such a device would no longer trust the server.
/// </summary>
public sealed class CaRolloverDeviceTests(HospitalServer server) : IDisposable
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
    public async Task System_health_names_the_devices_without_the_next_CA_and_the_switch_waits_for_them()
    {
        var (directories, beforePreparing, prepared) = Prepare();
        using (beforePreparing)
        using (prepared)
        {
            var current = prepared.CaFingerprint!;
            var next = prepared.NextCaFingerprint!;
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            var tag = Guid.NewGuid().ToString("N")[..6];
            var holds = await EnrolAsync(admin, DeviceKind.Workstation, $"Next CA held {tag}");
            var without = await EnrolAsync(admin, DeviceKind.Workstation, $"Next CA missing {tag}");
            var silent = await EnrolAsync(admin, DeviceKind.Workstation, $"Next CA silent {tag}");
            var revoked = await EnrolAsync(admin, DeviceKind.Workstation, $"Next CA revoked {tag}");
            var away = await EnrolAsync(admin, DeviceKind.Workstation, $"Next CA away {tag}");
            var agentHolds = await EnrolAsync(admin, DeviceKind.LabAgent, $"Next CA lab held {tag}");
            var agentWithout = await EnrolAsync(admin, DeviceKind.LabAgent, $"Next CA lab missing {tag}");

            await DesktopHeartbeatAsync(holds, [current, next.ToLowerInvariant()]);
            await DesktopHeartbeatAsync(without, [current]);
            await DesktopHeartbeatAsync(silent, null);
            await DesktopHeartbeatAsync(revoked, [current]);
            await admin.PostAsync($"/api/v1/admin/devices/{revoked.Id}/revoke");
            await AgentHeartbeatAsync(agentHolds, [current, next]);
            await AgentHeartbeatAsync(agentWithout, [current]);

            // A workstation last seen 31 days ago: left to Administration › Devices.
            await DesktopHeartbeatAsync(away, [current]);
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Device>().Where(d => d.Id == away.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow.AddDays(-31)), Ct);
            }

            // A heartbeat that says nothing (a version before devices reported their CAs, or a moment without them) keeps what
            // the device said last.
            await DesktopHeartbeatAsync(holds, null);
            await AgentHeartbeatAsync(agentHolds, null);

            (await StoredAsync(holds.Id)).ShouldBe($"{current},{next}", "upper case, the CA it pins first");
            (await StoredAsync(agentHolds.Id)).ShouldBe($"{current},{next}");
            (await StoredAsync(without.Id)).ShouldBe(current);
            (await StoredAsync(agentWithout.Id)).ShouldBe(current);
            (await StoredAsync(silent.Id)).ShouldBeNull();

            var readiness = await ReadinessAsync(next, DateTimeOffset.UtcNow);
            readiness.WithoutNextCa.ShouldContain(without.Name);
            readiness.WithoutNextCa.ShouldContain(agentWithout.Name);
            readiness.NotReporting.ShouldContain(silent.Name);
            string[] notNamed = [holds.Name, agentHolds.Name, revoked.Name, away.Name];
            readiness.WithoutNextCa.Concat(readiness.NotReporting).ShouldNotContain(name => notNamed.Contains(name), "holding it, revoked, or not seen for 30 days");

            // System health on this server with the next CA prepared: the current CA expires in half a year, so a warning, not
            // yet critical, naming those without it. Before the next CA was prepared there is no such warning.
            var warning = (await HealthAsync(prepared)).Warnings.Where(w => w.Code == "cert.next_ca_devices").ShouldHaveSingleItem();
            warning.Severity.ShouldBe("warning");
            warning.Message.ShouldContain(without.Name);
            warning.Message.ShouldContain(agentWithout.Name);
            warning.Message.ShouldContain($"the current CA expires on {prepared.CaExpiresAt!.Value:dd MMM yyyy}");
            warning.Message.ShouldNotContain(holds.Name);
            (await HealthAsync(beforePreparing)).Warnings.ShouldNotContain(w => w.Code == "cert.next_ca_devices");

            // 'certificates switch-ca --confirm', reading which devices hold the next CA from this database: refused, and the
            // server keeps its CA, with the next one still prepared.
            var options = new Dictionary<string, string> { ["data-dir"] = directories.Root };
            string? askedFor = null;
            var exit = await ServerCli.SwitchCaAsync(options, confirmed: true, (fingerprint, now) =>
            {
                askedFor = fingerprint;
                return ReadinessAsync(fingerprint, now);
            });

            exit.ShouldBe(1, "devices seen in the last 30 days do not hold the next CA");
            askedFor.ShouldBe(next);
            using var unchanged = LanCertificateManager.PublicCa(directories).ShouldNotBeNull();
            CertificateProbe.Fingerprint(unchanged).ShouldBe(current, "nothing changed");
            LanCertificateManager.PreparedNextCaFingerprint(directories, HospitalDataProtection.CreateNodeProvider(directories), DateTimeOffset.UtcNow)
                .ShouldBe(next);
        }
    }

    [Fact]
    public async Task A_heartbeat_with_fingerprints_that_are_not_SHA_256_is_refused_and_changes_nothing()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var workstation = await EnrolAsync(admin, DeviceKind.Workstation, $"Rollover refused {tag}");
        var agent = await EnrolAsync(admin, DeviceKind.LabAgent, $"Rollover lab refused {tag}");
        string[][] refused = [["AB12"], [new string('G', 64)], [.. Enumerable.Range(0, 5).Select(i => new string((char)('A' + i), 64))]];

        foreach (var fingerprints in refused)
        {
            using var desktop = await server.Anonymous(ownAddress: true).SendRawAsync(HttpMethod.Post, "/api/v1/devices/heartbeat",
                new DeviceHeartbeatRequest(workstation.Id, workstation.Secret, "1.0.0", null, null, fingerprints));
            desktop.StatusCode.ShouldBe(HttpStatusCode.BadRequest, string.Join(",", fingerprints));
            using var lab = await AgentClient(agent).SendRawAsync(HttpMethod.Post, LabAgentProtocol.HeartbeatPath,
                new LabAgentHeartbeatRequest("1.0.0", [], fingerprints));
            lab.StatusCode.ShouldBe(HttpStatusCode.BadRequest, string.Join(",", fingerprints));
        }

        (await StoredAsync(workstation.Id)).ShouldBeNull();
        (await StoredAsync(agent.Id)).ShouldBeNull();
    }

    /// <summary>
    /// A server's CA made nine and a half years ago, as it started then; the same server now; and after 'certificates
    /// prepare-ca' now, with the next CA prepared. With the node key ring, as the commands open the CA with.
    /// </summary>
    private (DataDirectories Directories, LanCertificateManager Before, LanCertificateManager Prepared) Prepare()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        var keys = HospitalDataProtection.CreateNodeProvider(directories);
        Start(directories, keys, Made).Dispose();
        var before = Start(directories, keys, DateTimeOffset.UtcNow);
        LanCertificateManager.PrepareCa(directories, keys, DateTimeOffset.UtcNow);
        return (directories, before, Start(directories, keys, DateTimeOffset.UtcNow));
    }

    /// <summary>The devices that do not hold <paramref name="nextCaFingerprint"/>, read from this server's database.</summary>
    private async Task<CaRolloverReadiness> ReadinessAsync(string nextCaFingerprint, DateTimeOffset now)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await CaRolloverDevices.ReadAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), nextCaFingerprint, now, Ct);
    }

    private static LanCertificateManager Start(DataDirectories directories, IDataProtectionProvider keys, DateTimeOffset now)
    {
        var certificates = new LanCertificateManager(
            directories, new PlatformOptions { DataDirectory = directories.Root }, keys, NullLogger<LanCertificateManager>.Instance, new Clock(now));
        certificates.EnsureCertificates();
        return certificates;
    }

    /// <summary>System health as this server builds it, with <paramref name="certificates"/> as its TLS.</summary>
    private async Task<SystemHealthReport> HealthAsync(LanCertificateManager certificates)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await ActivatorUtilities.CreateInstance<SystemHealthService>(scope.ServiceProvider, certificates).BuildAsync(Ct);
    }

    private async Task<Enrolled> EnrolAsync(ApiClient admin, DeviceKind kind, string name)
    {
        var code = await admin.PostAsync<EnrollmentCodeResponse>("/api/v1/admin/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(kind, null, "CA rollover test", 30));
        var enrolled = await server.Anonymous(ownAddress: true).PostAsync<EnrollDeviceResponse>("/api/v1/devices/enroll",
            new EnrollDeviceRequest(code.Code, name, "ROLLOVER-PC", "Windows 11", "1.0.0"));
        return new Enrolled(enrolled.DeviceId, enrolled.DeviceSecret, name);
    }

    private Task DesktopHeartbeatAsync(Enrolled device, IReadOnlyList<string>? fingerprints) =>
        server.Anonymous(ownAddress: true).PostAsync("/api/v1/devices/heartbeat",
            new DeviceHeartbeatRequest(device.Id, device.Secret, "1.0.0", "HOSPITAL-WIFI", 80, fingerprints));

    private Task AgentHeartbeatAsync(Enrolled agent, IReadOnlyList<string>? fingerprints) =>
        AgentClient(agent).PostAsync<LabAgentAssignments>(LabAgentProtocol.HeartbeatPath, new LabAgentHeartbeatRequest("1.0.0", [], fingerprints));

    private ApiClient AgentClient(Enrolled agent)
    {
        var client = server.Anonymous(ownAddress: true);
        client.Http.DefaultRequestHeaders.Add(LabAgentProtocol.DeviceIdHeader, agent.Id.ToString());
        client.Http.DefaultRequestHeaders.Add(LabAgentProtocol.DeviceKeyHeader, agent.Secret);
        return client;
    }

    private async Task<string?> StoredAsync(Guid deviceId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Device>()
            .Where(d => d.Id == deviceId).Select(d => d.TrustedCaFingerprints).SingleAsync(Ct);
    }

    private sealed record Enrolled(Guid Id, string Secret, string Name);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
