using System.Net;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Lab analysers over the real API (INT-LAB): a serial analyser reached through an enrolled lab agent's relay, and a
/// sample received in the lab queued as an order for the bidirectional analyser that runs its test.
/// </summary>
public sealed class LabAnalyserTests(HospitalServer server)
{
    private const byte Enq = 0x05;
    private const byte Ack = 0x06;
    private const byte Eot = 0x04;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_lab_agent_relays_a_serial_analyser_it_is_assigned()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // Lab setup knows the analyser; it is cabled to a lab PC whose agent is enrolled with a one-time code.
        await admin.PostAsync<JsonObject>("/api/v1/lab/instruments", new SaveLabInstrumentRequest(
            "AGENT1", "Serial chemistry analyser", "Erba", "Chem 7", LabSection.Biochemistry, InstrumentProtocol.Astm,
            IsBidirectional: false, AutoValidationEnabled: false, Location: "Bench 3", IsActive: true, Version: null));
        var (deviceId, secret) = await EnrolLabAgentAsync(admin);
        await EnableGatewayAsync(admin);

        var listed = await admin.GetAsync<LabConnectionsDto>("/api/v1/integrations/lab/connections");
        listed.Agents.ShouldContain(a => a.Id == deviceId);
        await admin.PutAsync("/api/v1/integrations/lab/connections/AGENT1", new SaveLabConnectionRequest(
            "Serial", true, null, null, null, null, null, deviceId, new LabSerialPortDto("COM3", 9600, 8, "None", "One", "None", true, true), listed.ListVersion));

        // The agent's heartbeat gives it the analyser and its port settings.
        var agent = AgentClient(deviceId, secret);
        var assignments = await agent.PostAsync<LabAgentAssignments>(LabAgentProtocol.HeartbeatPath,
            new LabAgentHeartbeatRequest("1.0.0", [new LabAgentPortReport("AGENT1", true, null)]));
        var assignment = assignments.Instruments.ShouldHaveSingleItem();
        assignment.Code.ShouldBe("AGENT1");
        assignment.Port.PortName.ShouldBe("COM3");

        // The relay carries the analyser's bytes to the protocol session on the server, which answers them.
        using var socket = await ConnectRelayAsync(deviceId, secret, "AGENT1");
        socket.SubProtocol.ShouldBe(LabAgentProtocol.Subprotocol);
        await socket.SendAsync(new[] { Enq }, WebSocketMessageType.Binary, endOfMessage: true, Ct);
        var reply = new byte[1];
        var received = await socket.ReceiveAsync(reply, Ct);
        received.MessageType.ShouldBe(WebSocketMessageType.Binary);
        reply[0].ShouldBe(Ack, "the server's ASTM session acknowledges the analyser's bid");

        var connection = (await admin.GetAsync<LabConnectionsDto>("/api/v1/integrations/lab/connections")).Connections.Single(c => c.Code == "AGENT1");
        connection.Transport.ShouldBe("Serial");
        connection.InLabSetup.ShouldBeTrue();
        connection.Status.ShouldNotBeNull().State.ShouldBe("connected");
        (await admin.GetAsync<LabConnectionsDto>("/api/v1/integrations/lab/connections")).Agents.Single(a => a.Id == deviceId).Online.ShouldBeTrue();

        await socket.SendAsync(new[] { Eot }, WebSocketMessageType.Binary, endOfMessage: true, Ct);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", Ct);
    }

    [Fact]
    public async Task Only_an_enrolled_lab_agent_may_relay_and_only_its_own_analysers()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (deviceId, secret) = await EnrolLabAgentAsync(admin);
        await EnableGatewayAsync(admin);

        using (var wrongKey = await AgentClient(deviceId, "not-the-secret").SendRawAsync(HttpMethod.Post, LabAgentProtocol.HeartbeatPath, new LabAgentHeartbeatRequest("1.0.0", [])))
        {
            wrongKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // The agent is enrolled, but no analyser is assigned to it: the relay is refused before any WebSocket opens.
        var refused = await Should.ThrowAsync<Exception>(() => ConnectRelayAsync(deviceId, secret, "EM360"));
        refused.Message.ShouldContain("404");

        var assignments = await AgentClient(deviceId, secret).PostAsync<LabAgentAssignments>(LabAgentProtocol.HeartbeatPath, new LabAgentHeartbeatRequest("1.0.0", null));
        assignments.Instruments.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_received_sample_is_queued_for_the_bidirectional_analyser_that_runs_its_test()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var patient = await RegisterPatientAsync();
        var fbs = (await lab.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=LAB-FBS")).Items.Single(t => t.Code == "LAB-FBS");
        var order = await lab.PostAsync<LabOrderCreated>("/api/v1/lab/orders", new CreateLabOrderRequest(
            patient.Id, null, [fbs.Id], OrderPriority.Routine, LabOrderSource.WalkIn, null, null, null));
        var tube = order.Samples.ShouldHaveSingleItem();

        // A walk-in test is drawn only once paid; this test is about the analyser, so the counter waives that here.
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await SetPaymentGateAsync(admin, facilityId, required: false);
        try
        {
            await lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{order.Id}/collect", new CollectSamplesRequest(null, null, null));
            var outcome = (await lab.PostAsync<List<ReceiveOutcome>>("/api/v1/lab/samples/receive", new ReceiveSamplesRequest([tube.Barcode]))).ShouldHaveSingleItem();
            outcome.Received.ShouldBeTrue(outcome.Message);
        }
        finally
        {
            await SetPaymentGateAsync(admin, facilityId, required: true);
        }

        // Diagnostics asked, in the receipt's own transaction, for the chemistry analyser (bidirectional, on the
        // biochemistry bench) to be sent the order; the haematology analyser is not told, nor the unidirectional one.
        var requested = (await OutboxRequestsAsync()).Single(r => r.SampleBarcode == tube.Barcode);
        requested.Action.ShouldBe(InstrumentOrderAction.New);
        requested.Analysers.Select(a => a.InstrumentCode).ShouldBe(["EM360"]);

        // The outbox hands it to the lab gateway's queue, where it waits until the analyser is connected.
        var queued = await WaitForQueuedOrderAsync(tube.Barcode);
        queued.InstrumentCode.ShouldBe("EM360");
        queued.Status.ShouldBe(LabOrderDownloadStatus.Pending);
        queued.Action.ShouldBe(InstrumentOrderAction.New);
    }

    private async Task<(Guid DeviceId, string Secret)> EnrolLabAgentAsync(ApiClient admin)
    {
        var code = await admin.PostAsync<EnrollmentCodeResponse>("/api/v1/admin/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(DeviceKind.LabAgent, null, "Lab PC for serial analysers", 30));
        var enrolled = await server.Anonymous(ownAddress: true).PostAsync<EnrollDeviceResponse>("/api/v1/devices/enroll",
            new EnrollDeviceRequest(code.Code, "Lab PC " + Guid.NewGuid().ToString("N")[..6], "LAB-PC-1", "Windows 11", "1.0.0"));
        return (enrolled.DeviceId, enrolled.DeviceSecret);
    }

    private static Task EnableGatewayAsync(ApiClient admin) =>
        admin.PutAsync("/api/v1/admin/settings/integrations.lab", new SaveSettingRequest(JsonNode.Parse("""{"enabled":true}"""), null, false, null));

    private static Task SetPaymentGateAsync(ApiClient admin, Guid facilityId, bool required) =>
        admin.PutAsync("/api/v1/admin/settings/lab.requirePaymentBeforeCollection", new SaveSettingRequest(JsonValue.Create(required), facilityId, false, null));

    private ApiClient AgentClient(Guid deviceId, string secret)
    {
        var agent = server.Anonymous(ownAddress: true);
        agent.Http.DefaultRequestHeaders.Add(LabAgentProtocol.DeviceIdHeader, deviceId.ToString());
        agent.Http.DefaultRequestHeaders.Add(LabAgentProtocol.DeviceKeyHeader, secret);
        return agent;
    }

    private async Task<WebSocket> ConnectRelayAsync(Guid deviceId, string secret, string code)
    {
        var testServer = (TestServer)server.Services.GetRequiredService<IServer>();
        var client = testServer.CreateWebSocketClient();
        client.SubProtocols.Add(LabAgentProtocol.Subprotocol);
        client.ConfigureRequest = request =>
        {
            request.Headers[LabAgentProtocol.DeviceIdHeader] = deviceId.ToString();
            request.Headers[LabAgentProtocol.DeviceKeyHeader] = secret;
        };
        return await client.ConnectAsync(new Uri(testServer.BaseAddress, LabAgentProtocol.RelayPathFor(code).TrimStart('/')), Ct);
    }

    private async Task<List<InstrumentOrderRequest>> OutboxRequestsAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == InstrumentOrderRequest.OutboxType)
            .ToListAsync(Ct);
        return messages.Select(m => m.Read<InstrumentOrderRequest>()).ToList();
    }

    private async Task<LabOrderDownload> WaitForQueuedOrderAsync(string barcode)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
                var queued = await db.Set<LabOrderDownload>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(d => d.SampleBarcode == barcode, Ct);
                if (queued is not null)
                {
                    return queued;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        }

        throw new TimeoutException($"The order for sample {barcode} did not reach the lab gateway's queue.");
    }

    private async Task<PatientCreated> RegisterPatientAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Meera", MiddleName: null, LastName: "Analyser" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
