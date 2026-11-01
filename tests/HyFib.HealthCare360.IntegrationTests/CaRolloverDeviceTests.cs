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
using Microsoft.Extensions.Logging.Abstractions;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// During a hospital CA rollover, System health names each workstation and lab agent that has not taken the new CA
/// (docs/deployment/CA-ROLLOVER.md). HC360 Desktop and the lab agent say in their heartbeats which CAs they trust the server
/// through, over the real API; System health reads what they said, here with this server's CA rolled over as
/// 'certificates roll-ca' rolls it.
/// </summary>
public sealed class CaRolloverDeviceTests(HospitalServer server) : IDisposable
{
    /// <summary>When the old CA was made: nine and a half years ago, so it is in its last year, as a CA is when it is rolled over.</summary>
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
    public async Task System_health_names_the_devices_that_have_not_taken_the_new_CA_from_what_their_heartbeats_say()
    {
        var (beforeRollover, rolled) = Rollover();
        using (beforeRollover)
        using (rolled)
        {
            var current = rolled.CaFingerprint!;
            var previous = rolled.PreviousCaFingerprint!;
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            var tag = Guid.NewGuid().ToString("N")[..6];
            var moved = await EnrolAsync(admin, DeviceKind.Workstation, $"Rollover moved {tag}");
            var stuck = await EnrolAsync(admin, DeviceKind.Workstation, $"Rollover stuck {tag}");
            var silent = await EnrolAsync(admin, DeviceKind.Workstation, $"Rollover silent {tag}");
            var revoked = await EnrolAsync(admin, DeviceKind.Workstation, $"Rollover revoked {tag}");
            var away = await EnrolAsync(admin, DeviceKind.Workstation, $"Rollover away {tag}");
            var agentMoved = await EnrolAsync(admin, DeviceKind.LabAgent, $"Rollover lab moved {tag}");
            var agentStuck = await EnrolAsync(admin, DeviceKind.LabAgent, $"Rollover lab stuck {tag}");

            await DesktopHeartbeatAsync(moved, [current.ToLowerInvariant(), previous]);
            await DesktopHeartbeatAsync(stuck, [previous]);
            await DesktopHeartbeatAsync(silent, null);
            await DesktopHeartbeatAsync(revoked, [previous]);
            await admin.PostAsync($"/api/v1/admin/devices/{revoked.Id}/revoke");
            await AgentHeartbeatAsync(agentMoved, [current, previous]);
            await AgentHeartbeatAsync(agentStuck, [previous]);

            // A workstation last seen 31 days ago: left to Administration › Devices.
            await DesktopHeartbeatAsync(away, [previous]);
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Device>().Where(d => d.Id == away.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(d => d.LastSeenAt, DateTimeOffset.UtcNow.AddDays(-31)), Ct);
            }

            // A heartbeat that says nothing (a version before devices reported their CA, or a moment without it) keeps what
            // the device said last.
            await DesktopHeartbeatAsync(moved, null);
            await AgentHeartbeatAsync(agentMoved, null);

            (await StoredAsync(moved.Id)).ShouldBe($"{current},{previous}", "upper case, the CA it pins first");
            (await StoredAsync(agentMoved.Id)).ShouldBe($"{current},{previous}");
            (await StoredAsync(stuck.Id)).ShouldBe(previous);
            (await StoredAsync(agentStuck.Id)).ShouldBe(previous);
            (await StoredAsync(silent.Id)).ShouldBeNull();

            await using (var scope = server.Services.CreateAsyncScope())
            {
                var readiness = await CaRolloverDevices.ReadAsync(scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), current, DateTimeOffset.UtcNow, Ct);
                readiness.OnPreviousCa.ShouldContain(stuck.Name);
                readiness.OnPreviousCa.ShouldContain(agentStuck.Name);
                readiness.NotReporting.ShouldContain(silent.Name);
                string[] notNamed = [moved.Name, agentMoved.Name, revoked.Name, away.Name];
                readiness.OnPreviousCa.Concat(readiness.NotReporting).ShouldNotContain(name => notNamed.Contains(name), "moved, revoked, or not seen for 30 days");
            }

            // System health on this server, with its CA rolled over: the old CA expires in half a year, so a warning, not yet
            // critical, naming those still on the previous CA. Before the rollover there is no such warning.
            var warning = (await HealthAsync(rolled)).Warnings.Where(w => w.Code == "cert.rollover_devices").ShouldHaveSingleItem();
            warning.Severity.ShouldBe("warning");
            warning.Message.ShouldContain(stuck.Name);
            warning.Message.ShouldContain(agentStuck.Name);
            warning.Message.ShouldContain($"stop trusting this server on {rolled.CrossSignedUntil!.Value:dd MMM yyyy}");
            warning.Message.ShouldNotContain(moved.Name);
            (await HealthAsync(beforeRollover)).Warnings.ShouldNotContain(w => w.Code == "cert.rollover_devices");
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
    /// A server's CA made nine and a half years ago, as it started then, and the same server after 'certificates roll-ca' now:
    /// the new CA, and the previous one vouching for it until it expires.
    /// </summary>
    private (LanCertificateManager Before, LanCertificateManager Rolled) Rollover()
    {
        var directories = new DataDirectories(new PlatformOptions { DataDirectory = _root });
        var keys = HospitalDataProtection.CreateNodeProvider(directories);
        Start(directories, keys, Made).Dispose();
        var before = Start(directories, keys, DateTimeOffset.UtcNow);
        LanCertificateManager.RollCa(directories, keys, DateTimeOffset.UtcNow);
        return (before, Start(directories, keys, DateTimeOffset.UtcNow));
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
