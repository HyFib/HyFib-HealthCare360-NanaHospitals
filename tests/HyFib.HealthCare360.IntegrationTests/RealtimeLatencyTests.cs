using System.Diagnostics;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Realtime;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS PERF-04: real-time updates (queue, bed board, critical alerts) reach clients within 2 s. Each test connects to the
/// events hub as the screen that shows the update does, makes the change through the API as the member of staff who makes
/// it, and times from sending the request to the event arriving at the screen. The test server runs in this process, so
/// the time is the server's own share (saving, publishing, the hub); the hospital network adds its round trip on top,
/// which a run on site measures (docs/testing/LOAD-TESTS.md).
/// </summary>
public sealed class RealtimeLatencyTests(HospitalServer server)
{
    private const string HubPath = "/hubs/events";

    /// <summary>PERF-04's bound.</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_called_token_reaches_the_queue_screen_within_2_seconds()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var tokenId = await WriteBodyValidationTests.WaitingTokenAsync(frontOffice, doctor);
        var practitionerId = (await doctor.GetAsync<AuthUser>("/api/v1/auth/me")).PractitionerId.ShouldNotBeNull();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();

        // The waiting-area display follows the department's queue.
        await using var display = await LongPollingHub.ConnectAsync(frontOffice, HubPath);
        (await display.InvokeAsync(nameof(EventsHub.Subscribe), Topics.Queue(departmentId))).ShouldBeNull();

        var elapsed = await TimeAsync(
            () => doctor.PostAsync<QueueTokenDto>($"/api/v1/queue/{tokenId}/call", new CallTokenRequest("Consultation room 2")),
            display,
            e => e.Name == "queue.called" && e.Payload.GetProperty("id").GetGuid() == tokenId);

        elapsed.ShouldBeLessThan(Budget);
    }

    [Fact]
    public async Task A_bed_marked_for_cleaning_reaches_the_bed_board_within_2_seconds()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();

        // A bed of this test alone, in a ward the hospital has, so no other test's admission takes it.
        var beds = await admin.GetAsync<List<BedDto>>("/api/v1/beds");
        beds.ShouldNotBeEmpty();
        var wardId = beds[0].WardId;
        var bed = await admin.PostAsync<BedDto>("/api/v1/beds", new SaveBedRequest(
            wardId, null, "PERF04-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), false, false, false, 999, true, null));
        bed.Status.ShouldBe(BedStatus.Vacant);

        await using var board = await LongPollingHub.ConnectAsync(admin, HubPath);
        (await board.InvokeAsync(nameof(EventsHub.Subscribe), Topics.Beds(facilityId))).ShouldBeNull();

        var elapsed = await TimeAsync(
            () => admin.PostAsync<BedDto>($"/api/v1/beds/{bed.Id}/cleaning", new BedNoteRequest("Spill on the mattress")),
            board,
            e => e.Name == "beds.changed" && e.Payload.GetProperty("bedId").GetGuid() == bed.Id);

        elapsed.ShouldBeLessThan(Budget);
    }

    [Fact]
    public async Task A_critical_lab_value_reaches_the_pathologist_within_2_seconds()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var tube = await LabVerificationTests.ReceivedSampleAsync(server, "LAB-ELECTROLYTES");

        // The pathologist's worklist follows the facility's lab topic, where a critical value is announced for verification.
        var facilityId = (await pathologist.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        await using var console = await LongPollingHub.ConnectAsync(pathologist, HubPath);
        (await console.InvokeAsync(nameof(EventsHub.Subscribe), Topics.Lab(facilityId))).ShouldBeNull();

        var elapsed = await TimeAsync(
            () => LabVerificationTests.EnterAsync(lab, tube, new Dictionary<string, string> { ["NA"] = "139", ["K"] = "7.4", ["CL"] = "100" }),
            console,
            e => e.Name == "lab.critical");

        elapsed.ShouldBeLessThan(Budget);
    }

    /// <summary>From sending the change to the screen receiving the event <paramref name="wanted"/> accepts.</summary>
    private static async Task<TimeSpan> TimeAsync<T>(Func<Task<T>> change, LongPollingHub screen, Func<Envelope, bool> wanted)
    {
        var clock = Stopwatch.StartNew();
        await change();
        await screen.ReceiveAsync(message => IsEvent(message) && wanted(Envelope.Of(message)));
        var elapsed = clock.Elapsed;
        TestContext.Current.TestOutputHelper?.WriteLine($"Reached the screen in {elapsed.TotalMilliseconds:F0} ms.");
        return elapsed;
    }

    private static bool IsEvent(JsonElement message) =>
        message.TryGetProperty("type", out var type) && type.GetInt32() == 1 && message.GetProperty("target").GetString() == "event";

    /// <summary>The <see cref="RealtimeEnvelope"/> an event carries, as the client reads it.</summary>
    private sealed record Envelope(string Name, JsonElement Payload)
    {
        public static Envelope Of(JsonElement message)
        {
            var envelope = message.GetProperty("arguments")[0];
            return new Envelope(envelope.GetProperty("name").GetString()!, envelope.GetProperty("payload"));
        }
    }
}
