using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Integrations.Laboratory;
using HyFib.HealthCare360.Integrations.Laboratory.Astm;
using HyFib.HealthCare360.Integrations.Laboratory.Relay;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Diagnostics.Features.UnmatchedResults;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
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
/// Lab analysers over the real API (INT-LAB): a serial analyser reached through an enrolled lab agent's relay, a sample
/// received in the lab queued as an order for the bidirectional analyser that runs its test, and results from the
/// gateway's own mapper reaching the LIS: auto-validated only on QC their analyser ran, held with the reasons when the
/// analyser flags them, reports them preliminary or in another unit, and kept for the lab to assign or discard, with the
/// analyser acknowledged, when they cannot go on a sample's test.
/// </summary>
public sealed class LabAnalyserTests(HospitalServer server)
{
    private const byte Enq = 0x05;
    private const byte Ack = 0x06;
    private const byte Eot = 0x04;

    /// <summary>The lab's zone, in which analysers write their times.</summary>
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");

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
        var received = await ReceiveAsync(socket, reply);
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
    public async Task A_revoked_agent_a_desktop_or_another_agent_may_not_relay_an_analyser()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var code = "RLY" + Letters(6).ToUpperInvariant();
        await admin.PostAsync<JsonObject>("/api/v1/lab/instruments", new SaveLabInstrumentRequest(
            code, "Serial analyser " + code, "Erba", "Chem 7", LabSection.Biochemistry, InstrumentProtocol.Astm,
            IsBidirectional: false, AutoValidationEnabled: false, Location: null, IsActive: true, Version: null));
        var (assigned, assignedSecret) = await EnrolLabAgentAsync(admin);
        var (other, otherSecret) = await EnrolLabAgentAsync(admin);
        var (desktop, desktopSecret) = await EnrolDeviceAsync(admin, DeviceKind.Workstation);
        await EnableGatewayAsync(admin);
        var version = (await admin.GetAsync<LabConnectionsDto>("/api/v1/integrations/lab/connections")).ListVersion;
        await admin.PutAsync($"/api/v1/integrations/lab/connections/{code}", new SaveLabConnectionRequest(
            "Serial", true, null, null, null, null, null, assigned,
            new LabSerialPortDto("COM4", 9600, 8, "None", "One", "None", true, true), version));

        // Another lab agent is refused the analyser, and an HC360 Desktop's credential is no lab agent's.
        (await RefusedRelayAsync(other, otherSecret, code)).ShouldContain("404");
        (await RefusedRelayAsync(desktop, desktopSecret, code)).ShouldContain("401");

        // A relay replaced by a newer one is told why before it is closed.
        // The server takes a relay on only after accepting its socket, so the second waits until the first is relaying.
        using var first = await ConnectRelayAsync(assigned, assignedSecret, code);
        await WaitUntilRelayingAsync(admin, code);
        using var second = await ConnectRelayAsync(assigned, assignedSecret, code);
        var closed = await ReceiveAsync(first, new byte[16]);
        closed.MessageType.ShouldBe(WebSocketMessageType.Close);
        first.CloseStatusDescription.ShouldBe(RelayLease.Replaced);
        await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "agent replaced", Ct);
        await second.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", Ct);

        // Once revoked, the agent may not relay any more.
        await admin.PostAsync($"/api/v1/admin/devices/{assigned}/revoke");
        (await RefusedRelayAsync(assigned, assignedSecret, code)).ShouldContain("401");
    }

    [Fact]
    public async Task A_lab_agent_credential_does_not_count_as_an_enrolled_workstation()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var (agentId, agentSecret) = await EnrolLabAgentAsync(admin);
        var (workstationId, workstationSecret) = await EnrolDeviceAsync(admin, DeviceKind.Workstation);
        var anonymous = server.Anonymous(ownAddress: true);

        // The desktop heartbeat is for HC360 Desktop only.
        using (var agent = await anonymous.SendRawAsync(HttpMethod.Post, "/api/v1/devices/heartbeat",
                   new DeviceHeartbeatRequest(agentId, agentSecret, "1.0.0", null, null)))
        {
            agent.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using (var workstation = await anonymous.SendRawAsync(HttpMethod.Post, "/api/v1/devices/heartbeat",
                   new DeviceHeartbeatRequest(workstationId, workstationSecret, "1.0.0", null, null)))
        {
            workstation.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // Signing in with the agent's id and secret is a sign-in from no enrolled device: the session is bound to none,
        // so the agent's credential cannot satisfy the roles that may sign in only at an enrolled workstation.
        var fromAgent = await server.SignInFromDeviceAsync(HospitalServer.Users.Lab, agentId, agentSecret);
        (await CurrentSessionAsync(fromAgent)).DeviceName.ShouldBeNull();
        var fromWorkstation = await server.SignInFromDeviceAsync(
            HospitalServer.Users.Lab, workstationId, workstationSecret);
        (await CurrentSessionAsync(fromWorkstation)).DeviceName.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_received_sample_is_queued_for_the_bidirectional_analyser_that_runs_its_test()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var tube = await ReceivedFastingSugarSampleAsync(admin, lab);

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

    [Fact]
    public async Task Results_sent_from_the_training_analyser_reach_the_sample_as_that_analysers()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var tube = await ReceivedFastingSugarSampleAsync(admin, lab);
        await EnableGatewayAsync(admin);
        const string connections = "/api/v1/integrations/lab/connections";
        var version = (await admin.GetAsync<LabConnectionsDto>(connections)).ListVersion;
        await admin.PutAsync($"{connections}/EM360", new SaveLabConnectionRequest("Simulator", true, null, null, null, null, null, null, null, version));
        try
        {
            // The training analyser lists the sample's tests it runs, by its own channel codes.
            var sample = await admin.GetAsync<TrainingSampleDto>($"{connections}/EM360/training/samples/{tube.Barcode}");
            sample.SampleBarcode.ShouldBe(tube.Barcode);
            var channel = sample.Channels.ShouldHaveSingleItem();

            // The results go through the analyser's own message and the same path as a real analyser's.
            var outcome = await admin.PostAsync<TrainingResultsOutcome>($"{connections}/EM360/training/results",
                new TrainingResultsRequest(tube.Barcode, [new TrainingResult(channel, "96", "mg/dL")]));
            outcome.Accepted.ShouldBeTrue(outcome.Message);
            outcome.RawMessage.ShouldContain(tube.Barcode);

            var sheet = await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");
            var line = sheet.Lines.ShouldHaveSingleItem();
            (line.Value, line.InstrumentCode).ShouldBe(("96", "EM360"));

            // Only a training analyser takes results from the screen.
            using var refused = await admin.SendRawAsync(HttpMethod.Post, $"{connections}/XN550/training/results",
                new TrainingResultsRequest(tube.Barcode, [new TrainingResult(channel, "96", null)]));
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            using var unsafeValue = await admin.SendRawAsync(HttpMethod.Post, $"{connections}/EM360/training/results",
                new TrainingResultsRequest(tube.Barcode, [new TrainingResult(channel, "9|6", null)]));
            unsafeValue.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
        finally
        {
            var current = (await admin.GetAsync<LabConnectionsDto>(connections)).ListVersion;
            using var removed = await admin.SendRawAsync(HttpMethod.Delete, $"{connections}/EM360?version={current}");
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task On_a_live_server_no_results_are_typed_in_as_an_analysers()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var tube = await ReceivedFastingSugarSampleAsync(admin, lab);
        await EnableGatewayAsync(admin);
        const string connections = "/api/v1/integrations/lab/connections";
        try
        {
            // On the hospital's live server an analyser cannot be made the training analyser...
            using (server.AsLiveServer())
            {
                var version = (await admin.GetAsync<LabConnectionsDto>(connections)).ListVersion;
                using var refused = await admin.SendRawAsync(HttpMethod.Put, $"{connections}/EM360",
                    new SaveLabConnectionRequest("Simulator", true, null, null, null, null, null, null, null, version));
                refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("lab.training_server");
            }

            // ...and one made on a training server takes no results from the screen once the server is live.
            var current = (await admin.GetAsync<LabConnectionsDto>(connections)).ListVersion;
            await admin.PutAsync($"{connections}/EM360", new SaveLabConnectionRequest("Simulator", true, null, null, null, null, null, null, null, current));
            var channel = (await admin.GetAsync<TrainingSampleDto>($"{connections}/EM360/training/samples/{tube.Barcode}")).Channels.ShouldHaveSingleItem();
            using (server.AsLiveServer())
            {
                using var sample = await admin.SendRawAsync(HttpMethod.Get, $"{connections}/EM360/training/samples/{tube.Barcode}");
                sample.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                using var results = await admin.SendRawAsync(HttpMethod.Post, $"{connections}/EM360/training/results",
                    new TrainingResultsRequest(tube.Barcode, [new TrainingResult(channel, "96", "mg/dL")]));
                results.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await results.Content.ReadAsStringAsync(Ct)).ShouldContain("lab.training_server");
            }

            var line = (await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results")).Lines.ShouldHaveSingleItem();
            (line.Value, line.InstrumentCode).ShouldBe((null, null));
        }
        finally
        {
            var list = await admin.GetAsync<LabConnectionsDto>(connections);
            if (list.Connections.Any(c => c.Code == "EM360"))
            {
                using var removed = await admin.SendRawAsync(HttpMethod.Delete, $"{connections}/EM360?version={list.ListVersion}");
                removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }
        }
    }

    [Fact]
    public async Task An_analysers_results_auto_validate_only_on_the_qc_that_analyser_ran_today()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);

        // A back-up chemistry analyser, auto-validation on, beside the EM 360 that ran this morning's glucose QC.
        var backup = "QCB" + Letters(5).ToUpperInvariant();
        await admin.PostAsync<JsonObject>("/api/v1/lab/instruments", new SaveLabInstrumentRequest(
            backup, "Back-up chemistry analyser", "Erba", "Chem 7", LabSection.Biochemistry, InstrumentProtocol.Astm,
            IsBidirectional: false, AutoValidationEnabled: true, Location: null, IsActive: true, Version: null));
        var onEm360 = await ReceivedFastingSugarSampleAsync(admin, lab);
        var onBackup = await ReceivedFastingSugarSampleAsync(admin, lab);

        // Each sends an in-range fasting sugar, final, in the test's unit, with no flag.
        (await AnalyserSendsAsync("EM360", $"O|1|{onEm360.Barcode}||^^^GLU|R", "R|1|^^^GLU|92|mg/dL||N||F")).ShouldNotBeNull().Accepted.ShouldBeTrue();
        (await AnalyserSendsAsync(backup, $"O|1|{onBackup.Barcode}||^^^GLU|R", "R|1|^^^GLU|92|mg/dL||N||F")).ShouldNotBeNull().Accepted.ShouldBeTrue();

        var trusted = (await SheetAsync(lab, onEm360)).Lines.ShouldHaveSingleItem();
        (trusted.Status, trusted.AutoValidated).ShouldBe((LabResultStatus.Verified, true), "the EM 360's own glucose QC was accepted today");
        trusted.HeldReasons.ShouldBeEmpty();

        // The EM 360's QC says nothing about the back-up analyser: its result waits for a pathologist, who sees why.
        var held = (await SheetAsync(lab, onBackup)).Lines.ShouldHaveSingleItem();
        (held.Status, held.Value, held.InstrumentCode).ShouldBe((LabResultStatus.Entered, "92", backup));
        held.HeldReasons.ShouldBe(["Today's QC for this test on this analyser is missing or not accepted."]);
    }

    [Fact]
    public async Task Results_under_an_order_record_without_a_sample_id_are_kept_for_the_lab_to_assign_never_dropped()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var tube = await ReceivedFastingSugarSampleAsync(admin, lab);
        var channel = "G" + Letters(6).ToUpperInvariant();

        // As in the production-style run: the analyser's sample number is not in O.3 or O.4, where the gateway reads it.
        var outcome = await AnalyserSendsAsync("EM360", "O|1||||R", $"R|1|^^^{channel}|93|mg/dL||N||F");

        outcome.ShouldNotBeNull("the gateway hands the results to the LIS rather than acknowledging an empty message").Accepted
            .ShouldBeTrue("the analyser is acknowledged, so it moves on to its next sample");
        var kept = (await UnmatchedAsync(lab, channel)).ShouldHaveSingleItem();
        (kept.Reason, kept.InstrumentCode, kept.SampleBarcode, kept.Value, kept.InstrumentUnit, kept.InstrumentStatus)
            .ShouldBe((UnmatchedResultReason.NoSampleId, "EM360", null, "93", "mg/dL", "F"));

        // The technician knows the tube: the value goes on its fasting sugar, entered, for a person to verify.
        var line = (await SheetAsync(lab, tube)).Lines.ShouldHaveSingleItem();
        using (var assigned = await lab.SendRawAsync(HttpMethod.Post, $"/api/v1/lab/unmatched-results/{kept.Id}/assign",
                   new AssignUnmatchedResultRequest(line.ResultId)))
        {
            assigned.StatusCode.ShouldBe(HttpStatusCode.NoContent, await assigned.Content.ReadAsStringAsync(Ct));
        }

        var sheet = await SheetAsync(lab, tube);
        var entered = sheet.Lines.ShouldHaveSingleItem();
        (entered.Status, entered.Value, entered.InstrumentCode, entered.AutoValidated).ShouldBe((LabResultStatus.Entered, "93", "EM360", false));
        entered.HeldReasons.ShouldBe([UnmatchedResultService.AssignedReason]);
        (await UnmatchedAsync(lab, channel)).ShouldBeEmpty();
        (await UnmatchedAsync(lab, channel, UnmatchedResultStatus.Assigned)).ShouldHaveSingleItem().AssignedResultId.ShouldBe(line.ResultId);
        (await AuditedAsync(admin, "lab.unmatched.assigned", kept.Id)).PatientId.ShouldBe(sheet.PatientId);

        // It was put on a test once: it cannot be put on another, nor discarded.
        using var again = await lab.SendRawAsync(HttpMethod.Post, $"/api/v1/lab/unmatched-results/{kept.Id}/discard",
            new DiscardUnmatchedResultRequest("Assigned by mistake"));
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_unknown_sample_is_kept_and_acknowledged_once_and_the_known_sample_beside_it_is_stored()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var known = await ReceivedFastingSugarSampleAsync(admin, lab);
        var unknown = "9" + Digits(8);
        string[] message = [$"O|1|{known.Barcode}||^^^GLU|R", "R|1|^^^GLU|91|mg/dL||N||F", $"O|2|{unknown}||^^^GLU|R", "R|1|^^^GLU|97|mg/dL||N||F"];

        // The analyser is acknowledged, so it neither sends the message for ever nor sends the known sample twice.
        (await AnalyserSendsAsync("EM360", message)).ShouldNotBeNull().Accepted.ShouldBeTrue();
        (await SheetAsync(lab, known)).Lines.ShouldHaveSingleItem().Value.ShouldBe("91");
        var kept = (await UnmatchedAsync(lab, unknown)).ShouldHaveSingleItem();
        (kept.Reason, kept.Value, kept.SampleId, kept.PatientName).ShouldBe((UnmatchedResultReason.UnknownSample, "97", null, null));

        // The same message again, after an acknowledgement the analyser did not get: nothing is stored twice.
        (await AnalyserSendsAsync("EM360", message)).ShouldNotBeNull().Accepted.ShouldBeTrue();
        (await UnmatchedAsync(lab, unknown)).ShouldHaveSingleItem().Id.ShouldBe(kept.Id);

        // A run of control material under a made-up sample id: set aside with the reason, which the audit trail keeps.
        using (var discarded = await lab.SendRawAsync(HttpMethod.Post, $"/api/v1/lab/unmatched-results/{kept.Id}/discard",
                   new DiscardUnmatchedResultRequest("Control material run under a made-up sample id")))
        {
            discarded.StatusCode.ShouldBe(HttpStatusCode.NoContent, await discarded.Content.ReadAsStringAsync(Ct));
        }

        (await UnmatchedAsync(lab, unknown)).ShouldBeEmpty();
        (await UnmatchedAsync(lab, unknown, UnmatchedResultStatus.Discarded)).ShouldHaveSingleItem().DiscardReason
            .ShouldBe("Control material run under a made-up sample id");
        (await AuditedAsync(admin, "lab.unmatched.discarded", kept.Id)).Summary.ShouldNotBeNull().ShouldContain(unknown);
    }

    [Fact]
    public async Task A_verified_result_sent_again_is_acknowledged_and_another_value_for_it_is_kept_for_the_lab()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var tube = await ReceivedFastingSugarSampleAsync(admin, lab);
        string[] message = [$"O|1|{tube.Barcode}||^^^GLU|R", "R|1|^^^GLU|94|mg/dL||N||F"];
        (await AnalyserSendsAsync("EM360", message)).ShouldNotBeNull().Accepted.ShouldBeTrue();
        var stored = (await SheetAsync(lab, tube)).Lines.ShouldHaveSingleItem();
        if (stored.Status != LabResultStatus.Verified)
        {
            await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify", new VerifyResultsRequest([stored.ResultId], null, null, Release: false));
        }

        var verified = (await SheetAsync(lab, tube)).Lines.ShouldHaveSingleItem();
        verified.Status.ShouldBe(LabResultStatus.Verified);

        // The acknowledgement was lost and the analyser sends the message again: taken, and nothing changes.
        (await AnalyserSendsAsync("EM360", message)).ShouldNotBeNull().Accepted.ShouldBeTrue();
        (await SheetAsync(lab, tube)).Lines.ShouldHaveSingleItem().Version.ShouldBe(verified.Version);
        (await UnmatchedAsync(lab, tube.Barcode)).ShouldBeEmpty();

        // A rerun with another value does not overwrite the verified result: it waits for the lab, beside it.
        (await AnalyserSendsAsync("EM360", $"O|1|{tube.Barcode}||^^^GLU|R", "R|1|^^^GLU|131|mg/dL||H||F")).ShouldNotBeNull().Accepted.ShouldBeTrue();
        var line = (await SheetAsync(lab, tube)).Lines.ShouldHaveSingleItem();
        (line.Value, line.Status).ShouldBe(("94", LabResultStatus.Verified));
        var kept = (await UnmatchedAsync(lab, tube.Barcode)).ShouldHaveSingleItem();
        (kept.Reason, kept.LabResultId, kept.Value, kept.InstrumentFlags, kept.SampleId, kept.TestName)
            .ShouldBe((UnmatchedResultReason.DiffersFromVerified, verified.ResultId, "131", "H", tube.Id, verified.TestName));
        kept.Detail.ShouldBe($"{verified.TestName} on sample {tube.Barcode} is verified as 94 mg/dL; EM360 sent 131 mg/dL.");
    }

    [Fact]
    public async Task What_the_analyser_says_of_a_result_is_kept_and_a_flagged_preliminary_or_other_unit_value_waits_for_a_person()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var preliminary = await ReceivedFastingSugarSampleAsync(admin, lab);
        var otherUnit = await ReceivedFastingSugarSampleAsync(admin, lab);

        // In range, but the analyser flagged it, reported it as preliminary and commented on the sample.
        (await AnalyserSendsAsync("EM360", $"O|1|{preliminary.Barcode}||^^^GLU|R", "R|1|^^^GLU|90|mg/dL||W||P", "C|1|I|Lipaemic sample|G"))
            .ShouldNotBeNull().Accepted.ShouldBeTrue();
        var held = (await SheetAsync(lab, preliminary)).Lines.ShouldHaveSingleItem();
        (held.Status, held.Flag, held.InstrumentUnit, held.InstrumentFlags, held.InstrumentStatus, held.InstrumentComment)
            .ShouldBe((LabResultStatus.Entered, ResultFlag.Normal, "mg/dL", "W", "P", "Lipaemic sample"));
        held.HeldReasons.ShouldBe(["The analyser flagged the result (W).", "The analyser did not report the result as final (status P)."]);

        // 85 mmol/L (some 1,530 mg/dL) reads as normal against the test's interval of 70 - 100 mg/dL: never taken as it stands.
        (await AnalyserSendsAsync("EM360", $"O|1|{otherUnit.Barcode}||^^^GLU|R", "R|1|^^^GLU|85|mmol/L||N||F")).ShouldNotBeNull().Accepted.ShouldBeTrue();
        var mmol = (await SheetAsync(lab, otherUnit)).Lines.ShouldHaveSingleItem();
        mmol.Status.ShouldBe(LabResultStatus.Entered);
        mmol.HeldReasons.ShouldContain("The analyser sent the value in mmol/L, not in the test's unit, mg/dL.");
    }

    [Fact]
    public async Task A_serial_analysers_transmission_with_no_sample_id_and_an_unknown_sample_is_acknowledged_once_every_result_is_kept()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);

        // A serial analyser cabled to a lab PC, as on the bench: the agent relays its bytes to the server's ASTM session.
        var code = "RLY" + Letters(5).ToUpperInvariant();
        await admin.PostAsync<JsonObject>("/api/v1/lab/instruments", new SaveLabInstrumentRequest(
            code, "Serial chemistry analyser", "Erba", "Chem 7", LabSection.Biochemistry, InstrumentProtocol.Astm,
            IsBidirectional: false, AutoValidationEnabled: false, Location: "Bench 4", IsActive: true, Version: null));
        var (deviceId, secret) = await EnrolLabAgentAsync(admin);
        await EnableGatewayAsync(admin);
        var listed = await admin.GetAsync<LabConnectionsDto>("/api/v1/integrations/lab/connections");
        await admin.PutAsync($"/api/v1/integrations/lab/connections/{code}", new SaveLabConnectionRequest(
            "Serial", true, null, null, null, null, null, deviceId, new LabSerialPortDto("COM4", 9600, 8, "None", "One", "None", true, true), listed.ListVersion));
        await AgentClient(deviceId, secret).PostAsync<LabAgentAssignments>(LabAgentProtocol.HeartbeatPath,
            new LabAgentHeartbeatRequest("1.0.0", [new LabAgentPortReport(code, true, null)]));

        // One transmission, in E1381 frames: an order record whose sample number is not in O.3 or O.4, where the gateway
        // reads it, and a sample id no tube has (a control run under a made-up number).
        var channel = "R" + Letters(6).ToUpperInvariant();
        var unknown = "8" + Digits(8);
        var frames = AstmFrames.BuildMessage(
        [
            $@"H|\^&|||{code}|||||||P|1", "P|1",
            "O|1||||R", $"R|1|^^^{channel}|93|mg/dL||N||F",
            $"O|2|{unknown}||^^^GLU|R", "R|1|^^^GLU|97|mg/dL||N||F",
            "L|1|N",
        ], Encoding.ASCII);

        using var socket = await ConnectRelayAsync(deviceId, secret, code);
        async Task<byte> AnswerToAsync(byte[] sent)
        {
            await socket.SendAsync(sent, WebSocketMessageType.Binary, endOfMessage: true, Ct);
            var reply = new byte[16];
            var received = await ReceiveAsync(socket, reply);
            (received.MessageType, received.Count).ShouldBe((WebSocketMessageType.Binary, 1), "the session answers with one control character");
            return reply[0];
        }

        (await AnswerToAsync([Enq])).ShouldBe(Ack, "the server's ASTM session takes the analyser's bid");
        for (var i = 0; i < frames.Count; i++)
        {
            (await AnswerToAsync(frames[i])).ShouldBe(Ack,
                $"frame {i + 1} of {frames.Count} (the last is acknowledged once the LIS has stored the message, refused with NAK else)");
        }

        await socket.SendAsync(new[] { Eot }, WebSocketMessageType.Binary, endOfMessage: true, Ct);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "agent stopping", Ct);

        // Acknowledged, and nothing lost: both results wait for the lab with what the analyser said of them.
        var noSampleId = (await UnmatchedAsync(lab, channel)).ShouldHaveSingleItem();
        (noSampleId.Reason, noSampleId.InstrumentCode, noSampleId.SampleBarcode, noSampleId.Value, noSampleId.InstrumentUnit)
            .ShouldBe((UnmatchedResultReason.NoSampleId, code, null, "93", "mg/dL"));
        var unknownSample = (await UnmatchedAsync(lab, unknown)).ShouldHaveSingleItem();
        (unknownSample.Reason, unknownSample.InstrumentCode, unknownSample.SampleBarcode, unknownSample.Value)
            .ShouldBe((UnmatchedResultReason.UnknownSample, code, unknown, "97"));
    }

    /// <summary>A walk-in fasting blood sugar, collected and received in the lab (the counter's payment gate waived).</summary>
    private async Task<CreatedSample> ReceivedFastingSugarSampleAsync(ApiClient admin, ApiClient lab)
    {
        var patient = await RegisterPatientAsync();
        var fbs = (await lab.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=LAB-FBS")).Items.Single(t => t.Code == "LAB-FBS");
        var order = await lab.PostAsync<LabOrderCreated>("/api/v1/lab/orders", new CreateLabOrderRequest(
            patient.Id, null, [fbs.Id], OrderPriority.Routine, LabOrderSource.WalkIn, null, null, null));
        var tube = order.Samples.ShouldHaveSingleItem();

        // A walk-in test is drawn only once paid; these tests are about the analyser, so the counter waives that here.
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

        return tube;
    }

    /// <summary>
    /// An ASTM E1394 message from the analyser (records between its header and terminator), read by the gateway's own
    /// mapper and handed to the LIS as the analyser's session hands it; the session acknowledges the message when the
    /// outcome is accepted. Null when the gateway found no results in it: the session acknowledges that too, with
    /// nothing stored.
    /// </summary>
    private async Task<LabSubmitOutcome?> AnalyserSendsAsync(string code, params string[] records)
    {
        var raw = string.Join("\r", [$@"H|\^&|||{code}|||||||P|1", .. records, "L|1|N"]);
        var results = AstmMessageMapper.MapResults(AstmRecords.Parse(raw), code, raw, DateTimeOffset.UtcNow, India);
        if (results.Count == 0)
        {
            return null;
        }

        var instrument = new LabInstrumentSettings { Code = code, Name = code, Protocol = LabProtocol.Astm };
        return await server.Services.GetRequiredService<ILabMessageProcessor>().SubmitResultsAsync(instrument, results, raw, Ct);
    }

    private static Task<ResultSheet> SheetAsync(ApiClient api, CreatedSample tube) => api.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.Id}/results");

    /// <summary>The facility's analyser results kept aside, found by sample barcode, analyser or channel.</summary>
    private static async Task<IReadOnlyList<UnmatchedResultDto>> UnmatchedAsync(
        ApiClient lab, string search, UnmatchedResultStatus status = UnmatchedResultStatus.Open) =>
        (await lab.GetAsync<PagedResult<UnmatchedResultDto>>(
            $"/api/v1/lab/unmatched-results?search={Uri.EscapeDataString(search)}&status={status}&pageSize=50")).Items;

    private static async Task<AuditEventDto> AuditedAsync(ApiClient admin, string action, Guid unmatchedId) =>
        (await admin.GetAsync<PagedResult<AuditEventDto>>(
            $"/api/v1/admin/audit?action={action}&entityType=UnmatchedLabResult&entityId={unmatchedId}")).Items.ShouldHaveSingleItem();

    private Task<(Guid DeviceId, string Secret)> EnrolLabAgentAsync(ApiClient admin, Guid? facilityId = null) =>
        EnrolDeviceAsync(admin, DeviceKind.LabAgent, facilityId);

    private async Task<(Guid DeviceId, string Secret)> EnrolDeviceAsync(
        ApiClient admin, DeviceKind kind, Guid? facilityId = null)
    {
        var code = await admin.PostAsync<EnrollmentCodeResponse>("/api/v1/admin/devices/enrollment-codes",
            new CreateEnrollmentCodeRequest(kind, facilityId, "Lab PC for serial analysers", 30));
        var enrolled = await server.Anonymous(ownAddress: true).PostAsync<EnrollDeviceResponse>("/api/v1/devices/enroll",
            new EnrollDeviceRequest(code.Code, "Lab PC " + Guid.NewGuid().ToString("N")[..6], "LAB-PC-1", "Windows 11", "1.0.0"));
        return (enrolled.DeviceId, enrolled.DeviceSecret);
    }

    /// <summary>The refusal of a relay the server does not open (its message names the HTTP status).</summary>
    private async Task<string> RefusedRelayAsync(Guid deviceId, string secret, string code) =>
        (await Should.ThrowAsync<Exception>(() => ConnectRelayAsync(deviceId, secret, code))).Message;

    private static async Task<SessionInfo> CurrentSessionAsync(ApiClient client) =>
        (await client.GetAsync<List<SessionInfo>>("/api/v1/auth/sessions")).Single(s => s.IsCurrent);

    private static Task EnableGatewayAsync(ApiClient admin) =>
        admin.SaveSettingAsync("integrations.lab", new SaveSettingRequest(JsonNode.Parse("""{"enabled":true}"""), null, false, null));

    private static Task SetPaymentGateAsync(ApiClient admin, Guid facilityId, bool required) =>
        admin.SaveSettingAsync("lab.requirePaymentBeforeCollection", new SaveSettingRequest(JsonValue.Create(required), facilityId, false, null));

    private ApiClient AgentClient(Guid deviceId, string secret)
    {
        var agent = server.Anonymous(ownAddress: true);
        agent.Http.DefaultRequestHeaders.Add(LabAgentProtocol.DeviceIdHeader, deviceId.ToString());
        agent.Http.DefaultRequestHeaders.Add(LabAgentProtocol.DeviceKeyHeader, secret);
        return agent;
    }

    /// <summary>Waits until Lab setup shows the analyser's relay as connected.</summary>
    private static async Task WaitUntilRelayingAsync(ApiClient admin, string code)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var connections = await admin.GetAsync<LabConnectionsDto>("/api/v1/integrations/lab/connections");
            if (connections.Connections.Single(c => c.Code == code).Status?.State == "connected")
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }

        throw new TimeoutException($"The relay of analyser {code} was not taken on within 10 seconds.");
    }

    /// <summary>The server's next message on a relay, or a failure naming the wait if none comes in 30 seconds.</summary>
    private static async Task<WebSocketReceiveResult> ReceiveAsync(WebSocket socket, byte[] buffer)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await socket.ReceiveAsync(buffer, wait.Token);
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            throw new TimeoutException("The server sent nothing on the relay within 30 seconds.");
        }
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
            Title: "Mrs.", FirstName: TestPatients.FirstName("Meera"), MiddleName: null, LastName: "Analyser" + Letters(8), Gender: Gender.Female,
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
