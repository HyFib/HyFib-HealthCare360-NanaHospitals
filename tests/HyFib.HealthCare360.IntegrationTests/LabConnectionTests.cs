using System.Net;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Hosting;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Lab setup › Analysers › connection over the real API and database (INT-LAB): the rules a connection is saved by,
/// the facility it belongs to, and the claim a session holds on the orders it sends. Each test uses analysers and
/// agents of its own, so the shared connection list only grows.
/// </summary>
public sealed class LabConnectionTests(HospitalServer server)
{
    private const string Connections = "/api/v1/integrations/lab/connections";

    private static readonly LabSerialPortDto Com5 = new("COM5", 9600, 8, "None", "One", "None", true, true);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_connection_is_saved_only_when_its_port_agent_and_analyser_are_valid()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var first = await AddAnalyserAsync(admin);
        var second = await AddAnalyserAsync(admin);
        var (agent, _) = await EnrolAsync(admin, DeviceKind.LabAgent);
        var (workstation, _) = await EnrolAsync(admin, DeviceKind.Workstation);
        var (revoked, _) = await EnrolAsync(admin, DeviceKind.LabAgent);
        await admin.PostAsync($"/api/v1/admin/devices/{revoked}/revoke");
        var port = Random.Shared.Next(20_000, 60_000);

        // The server's own ports are never an analyser's.
        await ExpectFieldErrorAsync(admin, first, v => Tcp(5353, v), "is used by the HC360 server itself");
        var platform = server.Services.GetRequiredService<IOptions<PlatformOptions>>().Value;
        await ExpectFieldErrorAsync(admin, first, v => Tcp(platform.HttpsPort, v), "is used by the HC360 server itself");
        await ExpectFieldErrorAsync(admin, first, v => Tcp(platform.PortalHttpsPort, v), "is used by the HC360 server itself, for the patient portal");
        await ExpectFieldErrorAsync(admin, first, v => Tcp(platform.ApiReferencePort, v), "is used by the HC360 server itself, for the API reference");

        // A network analyser connects without signing in: its address is required.
        await ExpectFieldErrorAsync(admin, first, v => Tcp(port, v) with { AllowedIps = null }, "Enter the analyser's fixed IP address");

        // One analyser per network port.
        await admin.PutAsync($"{Connections}/{first}", Tcp(port, await VersionAsync(admin)));
        await ExpectFieldErrorAsync(admin, second, v => Tcp(port, v), $"is already used by analyser {first}");

        // One analyser per COM port of an agent, and only an enrolled lab agent relays.
        await admin.PutAsync($"{Connections}/{first}", Serial(agent, await VersionAsync(admin)));
        await ExpectFieldErrorAsync(admin, second, v => Serial(agent, v), $"is already used by analyser {first}");
        await ExpectFieldErrorAsync(admin, second, v => Serial(workstation, v), "Choose an enrolled lab agent");
        await ExpectFieldErrorAsync(admin, second, v => Serial(revoked, v), "Choose an enrolled lab agent");

        // An analyser that is not in Lab setup has no connection to save.
        var unknownCode = "NOSUCH" + Letters(6);
        var request = Tcp(port + 1, await VersionAsync(admin));
        using (var unknown = await admin.SendRawAsync(HttpMethod.Put, $"{Connections}/{unknownCode}", request))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        // Two edits at once: the one made against a list that changed meanwhile is refused.
        var seen = await VersionAsync(admin);
        await admin.PutAsync($"{Connections}/{second}", Tcp(port + 2, seen));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, $"{Connections}/{second}", Tcp(port + 3, seen)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var listed = await admin.GetAsync<LabConnectionsDto>(Connections);
        listed.Connections.Single(c => c.Code == first).AgentDeviceId.ShouldBe(agent);
        listed.Connections.Single(c => c.Code == second).Port.ShouldBe(port + 2);
    }

    [Fact]
    public async Task Saving_a_connection_clears_the_retired_host_query_flag()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var code = await AddAnalyserAsync(admin);
        var port = Random.Shared.Next(20_000, 60_000);

        // A connection written before Bidirectional in Lab setup decided, with "hostQuery": false.
        var stored = await StoredListAsync();
        stored.Add(JsonNode.Parse($$"""{"code":"{{code}}","protocol":"Astm","port":{{port}},"hostQuery":false}""")!);
        await admin.PutAsync(
            $"/api/v1/admin/settings/{LabSettingKeys.Instruments}", new SaveSettingRequest(stored, null, false, null));
        (await ConnectionAsync(admin, code)).HostQueryRetiredOff.ShouldBeTrue();

        await admin.PutAsync($"{Connections}/{code}", Tcp(port, await VersionAsync(admin)));

        (await ConnectionAsync(admin, code)).HostQueryRetiredOff.ShouldBeFalse();
        var saved = (await StoredListAsync()).Single(n => (string?)n!["code"] == code)!.AsObject();
        saved.ContainsKey("hostQuery").ShouldBeFalse();
    }

    [Fact]
    public async Task Staff_see_and_connect_only_their_own_facilitys_analysers_and_agents()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var ours = await AddAnalyserAsync(admin);
        var (ourAgent, _) = await EnrolAsync(admin, DeviceKind.LabAgent);
        var annexe = await admin.PostAsync<Created>("/api/v1/masters/facilities", new SaveFacilityRequest(
            Letters(6), "Nana Hospitals Lab Annexe " + Letters(4), null,
            new Address { City = "Chennai", PinCode = "600002" }, null, null, null, null, null, null, null,
            null, null, null, IsActive: false, null));
        var theirs = await AddAnalyserAtAsync(annexe.Id);
        var (theirAgent, _) = await EnrolAsync(admin, DeviceKind.LabAgent, annexe.Id);

        // Another facility's analyser cannot be connected, nor ours through another facility's agent.
        var viaTheirAgent = Serial(theirAgent, await VersionAsync(admin));
        using (var other = await admin.SendRawAsync(HttpMethod.Put, $"{Connections}/{theirs}", viaTheirAgent))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        await ExpectFieldErrorAsync(admin, ours, v => Serial(theirAgent, v), "Choose an enrolled lab agent");

        // An account with no facility sees every facility, but cannot cable an analyser to another facility's agent.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var everywhere = scope.ServiceProvider.GetRequiredService<LabConnectionService>();
            var version = (await everywhere.ListAsync(Ct)).ListVersion;
            var mismatch = await everywhere.SaveAsync(ours, Serial(theirAgent, version), Ct);
            mismatch.Error.ShouldNotBeNull().Message.ShouldContain("enrolled at another facility");
            (await everywhere.SaveAsync(theirs, Serial(theirAgent, version), Ct)).IsSuccess.ShouldBeTrue();
            var all = await everywhere.ListAsync(Ct);
            all.Connections.ShouldContain(c => c.Code == theirs);
            all.Agents.ShouldContain(a => a.Id == theirAgent);
        }

        // Staff of this facility see neither the annexe's analyser nor its agent.
        var listed = await admin.GetAsync<LabConnectionsDto>(Connections);
        listed.Connections.ShouldNotContain(c => c.Code == theirs);
        listed.Agents.ShouldNotContain(a => a.Id == theirAgent);
        listed.Agents.ShouldContain(a => a.Id == ourAgent);
        var removal = $"{Connections}/{theirs}?version={listed.ListVersion}";
        using (var remove = await admin.SendRawAsync(HttpMethod.Delete, removal))
        {
            remove.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Only_the_session_holding_an_order_records_how_its_send_went()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var code = "Q" + Letters(8);
        var row = new LabOrderDownload
        {
            FacilityId = Guid.CreateVersion7(),
            InstrumentCode = code,
            SampleBarcode = "SMP-" + Letters(6),
            Action = InstrumentOrderAction.New,
            Status = LabOrderDownloadStatus.Pending,
            CreatedAt = clock.Now.AddMinutes(-1),
            NextAttemptAt = clock.Now.AddMinutes(-1),
            SourceMessageId = Guid.CreateVersion7(),
        };
        await WithDbAsync(async db =>
        {
            db.Add(row);
            await db.SaveChangesAsync(Ct);
        });
        var sink = Substitute.For<IInstrumentResultSink>();
        sink.GetInstrumentAsync(code, Arg.Any<CancellationToken>())
            .Returns(new InstrumentProfile(code, "Chemistry", row.FacilityId, true, true, "Astm"));
        sink.GetOrderToSendAsync(code, row.SampleBarcode, Arg.Any<CancellationToken>())
            .Returns(new InstrumentOrder(row.SampleBarcode, "TEST^PATIENT", "F", null, ["GLU"]));
        var instrument = new LabInstrumentSettings { Code = code };

        // Session A claims the order, but its send outlasts the claim without extending it: session B takes it over.
        var a = await QueueAsync(clock, q => q.NextAsync(instrument, sink, 10, Ct));
        a.ShouldHaveSingleItem();
        clock.Now += LabOrderQueue.ClaimTime + TimeSpan.FromSeconds(1);
        var b = await QueueAsync(clock, q => q.NextAsync(instrument, sink, 10, Ct));
        b.ShouldHaveSingleItem().Claim.ShouldNotBe(a[0].Claim);

        // A's outcome is not recorded, and A can no longer extend the claim; B's claim, extended, keeps C away.
        await QueueAsync(clock, async q =>
        {
            await q.CompleteAsync([row.Id], a[0].Claim, delivered: true, null, Ct);
            return 0;
        });
        (await LoadAsync(row.Id)).Status.ShouldBe(LabOrderDownloadStatus.Pending);
        (await QueueAsync(clock, q => q.ExtendClaimAsync([row.Id], a[0].Claim, Ct))).ShouldBe(0);
        clock.Now += LabOrderQueue.ClaimTime - TimeSpan.FromSeconds(10);
        (await QueueAsync(clock, q => q.ExtendClaimAsync([row.Id], b[0].Claim, Ct))).ShouldBe(1);
        clock.Now += TimeSpan.FromSeconds(30);
        (await QueueAsync(clock, q => q.NextAsync(instrument, sink, 10, Ct))).ShouldBeEmpty();

        await QueueAsync(clock, async q =>
        {
            await q.CompleteAsync([row.Id], b[0].Claim, delivered: true, null, Ct);
            return 0;
        });
        var sent = await LoadAsync(row.Id);
        sent.Status.ShouldBe(LabOrderDownloadStatus.Sent);
        sent.Attempts.ShouldBe(1);
        sent.ClaimedBy.ShouldBeNull();
    }

    private static SaveLabConnectionRequest Tcp(int port, uint version) =>
        new("Tcp", true, port, ["127.0.0.1"], null, null, null, null, null, version);

    private static SaveLabConnectionRequest Serial(Guid agent, uint version) =>
        new("Serial", true, null, null, null, null, null, agent, Com5, version);

    private static async Task<uint> VersionAsync(ApiClient admin) =>
        (await admin.GetAsync<LabConnectionsDto>(Connections)).ListVersion;

    private static async Task<LabConnectionDto> ConnectionAsync(ApiClient admin, string code) =>
        (await admin.GetAsync<LabConnectionsDto>(Connections)).Connections.Single(c => c.Code == code);

    /// <summary>A save is refused with a field error that says <paramref name="message"/>.</summary>
    private static async Task ExpectFieldErrorAsync(
        ApiClient admin, string code, Func<uint, SaveLabConnectionRequest> request, string message)
    {
        var current = request(await VersionAsync(admin));
        using var response = await admin.SendRawAsync(HttpMethod.Put, $"{Connections}/{code}", current);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(message);
    }

    private static async Task<string> AddAnalyserAsync(ApiClient admin)
    {
        var code = "CN" + Letters(8);
        await admin.PostAsync<JsonObject>("/api/v1/lab/instruments", new SaveLabInstrumentRequest(
            code, "Chemistry analyser " + code, "Erba", "Chem 7", LabSection.Biochemistry, InstrumentProtocol.Astm,
            IsBidirectional: false, AutoValidationEnabled: false, Location: null, IsActive: true, Version: null));
        return code;
    }

    private async Task<string> AddAnalyserAtAsync(Guid facilityId)
    {
        var code = "FA" + Letters(8);
        await WithDbAsync(async db =>
        {
            db.Add(new LabInstrument
            {
                FacilityId = facilityId,
                Code = code,
                Name = "Annexe analyser " + code,
                Section = LabSection.Biochemistry,
                Protocol = InstrumentProtocol.Astm,
            });
            await db.SaveChangesAsync(Ct);
        });
        return code;
    }

    private async Task<(Guid DeviceId, string Secret)> EnrolAsync(
        ApiClient admin, DeviceKind kind, Guid? facilityId = null)
    {
        var code = await admin.PostAsync<EnrollmentCodeResponse>("/api/v1/admin/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(kind, facilityId, "Lab connection test", 30));
        var enrolled = await server.Anonymous(ownAddress: true).PostAsync<EnrollDeviceResponse>(
            "/api/v1/devices/enroll",
            new EnrollDeviceRequest(code.Code, "Lab PC " + Letters(6), "LAB-PC-2", "Windows 11", "1.0.0"));
        return (enrolled.DeviceId, enrolled.DeviceSecret);
    }

    private async Task<JsonArray> StoredListAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var value = await db.Set<SystemSetting>().AsNoTracking()
            .Where(s => s.Key == LabSettingKeys.Instruments && s.FacilityId == null)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(Ct);
        return value is null ? [] : JsonNode.Parse(value)?.AsArray() ?? [];
    }

    private async Task WithDbAsync(Func<HospitalDbContext, Task> work)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredService<HospitalDbContext>());
    }

    private async Task<LabOrderDownload> LoadAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<LabOrderDownload>().IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == id, Ct);
    }

    /// <summary>One session's call to the order queue, with its own database context, at the clock's time.</summary>
    private async Task<T> QueueAsync<T>(TimeProvider clock, Func<LabOrderQueue, Task<T>> call)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var queue = new LabOrderQueue(db, clock, NullLogger<LabOrderQueue>.Instance);
        return await call(queue);
    }

    private static string Letters(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
