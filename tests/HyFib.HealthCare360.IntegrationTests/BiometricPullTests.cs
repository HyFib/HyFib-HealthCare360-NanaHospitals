using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Attendance;
using HyFib.HealthCare360.Modules.HR.Services;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Attendance terminals HC360 reads (INT-BIO "pull") end to end, with the training terminal: punches made on it are
/// read in once, a terminal that cannot be read says why, addresses HC360 must not connect to are refused, and a
/// terminal that goes quiet is reported to HR once and cleared when it is heard from again.
/// </summary>
public sealed class BiometricPullTests(HospitalServer server)
{
    private const string Key = "integrations.biometric";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Punches_made_on_the_training_terminal_are_read_in_once()
    {
        await using var reading = await ReadingAsync(new JsonObject { ["enabled"] = true, ["provider"] = "simulator" });
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await hr.GetAsync<AttendanceTerminalReading>("/api/v1/hr/biometric-devices/reading")).ShouldBe(new AttendanceTerminalReading("simulator", null));

        // A terminal HC360 reads sends nothing, so it gets no device key, and a push in its name is refused.
        var serial = Serial();
        var registered = await hr.PostAsync<DeviceKeyIssued>("/api/v1/hr/biometric-devices", Terminal(serial, BiometricConnection.Pull, "10.20.30.40"));
        registered.ApiKey.ShouldBeNull();
        (await server.Anonymous().Http.GetAsync($"/iclock/cdata?SN={serial}&options=all", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var pins = new[] { Pin(), Pin() };
        foreach (var pin in pins)
        {
            await hr.PostAsync($"/api/v1/hr/biometric-devices/{registered.DeviceId}/training-punch", new TrainingPunchRequest(pin));
        }

        using (var invalid = await hr.SendRawAsync(HttpMethod.Post, $"/api/v1/hr/biometric-devices/{registered.DeviceId}/training-punch", new TrainingPunchRequest("12 34")))
        {
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Read now brings both in (the schedule may have read them already: then they are already here).
        var read = await ReadAsync(hr, registered.DeviceId);
        read.ShouldSatisfyAllConditions(
            r => r.Success.ShouldBeTrue(r.Error),
            r => r.Read.ShouldBe(2),
            r => (r.Punches!.Accepted + r.Punches.Duplicates).ShouldBe(2),
            r => r.Punches!.UnknownPins.ShouldBe(r.Punches.Accepted, "no employee has these numbers yet"),
            r => Math.Abs(r.ClockOffsetSeconds!.Value).ShouldBeLessThan(5));

        // Read again, nothing is added twice.
        var again = await ReadAsync(hr, registered.DeviceId);
        again.Punches!.ShouldSatisfyAllConditions(p => p.Accepted.ShouldBe(0), p => p.Duplicates.ShouldBe(2));

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var punches = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AttendancePunch>().IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.BiometricDeviceId == registered.DeviceId)
                .ToListAsync(Ct);
            punches.Select(p => p.Pin).Order().ShouldBe(pins.Order());
            punches.ShouldAllBe(p => p.Source == PunchSource.Pull && p.EmployeeId == null && p.Direction == PunchDirection.Unknown);
        }

        var device = (await hr.GetAsync<List<BiometricDeviceDto>>("/api/v1/hr/biometric-devices")).Single(d => d.Id == registered.DeviceId);
        device.ShouldSatisfyAllConditions(
            d => d.Connection.ShouldBe(BiometricConnection.Pull),
            d => d.PullAddress.ShouldBe("10.20.30.40"),
            d => d.PullPort.ShouldBe(4370),
            d => d.IsOnline.ShouldBeTrue(),
            d => d.LastPullError.ShouldBeNull(),
            d => d.LastPullAt.ShouldNotBeNull());

        // The training terminal's clock cannot drift, so setting it changes nothing and is audited.
        var clock = await hr.PostAsync<TerminalClockResult>($"/api/v1/hr/biometric-devices/{registered.DeviceId}/set-clock");
        clock.ShouldBe(new TerminalClockResult(true, null));

        // The integrations dashboard shows biometric attendance working with the simulator.
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var row = (await admin.GetAsync<List<IntegrationStatusDto>>("/api/v1/admin/integrations")).Single(r => r.Name == "biometric");
        row.ShouldSatisfyAllConditions(r => r.Provider.ShouldBe("simulator"), r => r.Enabled.ShouldBeTrue(), r => r.LastSuccessAt.ShouldNotBeNull());
    }

    [Fact]
    public async Task A_terminal_that_cannot_be_read_says_why_and_addresses_HC360_must_not_reach_are_refused()
    {
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await using (await ReadingAsync(new JsonObject { ["enabled"] = false, ["provider"] = "zkteco" }))
        {
            var registered = await hr.PostAsync<DeviceKeyIssued>("/api/v1/hr/biometric-devices", Terminal(Serial(), BiometricConnection.Pull, "192.168.10.201"));

            var read = await ReadAsync(hr, registered.DeviceId);
            read.ShouldSatisfyAllConditions(r => r.Success.ShouldBeFalse(), r => r.Error.ShouldBe("Biometric attendance is disabled."));
            (await hr.GetAsync<List<BiometricDeviceDto>>("/api/v1/hr/biometric-devices")).Single(d => d.Id == registered.DeviceId)
                .LastPullError.ShouldBe("Biometric attendance is disabled.");

            // Training punches need the simulator.
            using var punch = await hr.SendRawAsync(HttpMethod.Post, $"/api/v1/hr/biometric-devices/{registered.DeviceId}/training-punch", new TrainingPunchRequest("1001"));
            punch.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await punch.Content.ReadAsStringAsync(Ct)).ShouldContain("Biometric attendance is disabled.");
        }

        // HC360 connects only to an IP address on a network: not to itself, a link-local address or a host name.
        foreach (var address in (string?[])["127.0.0.1", "169.254.169.254", "terminal.local", "0.0.0.0", "224.0.0.1", null])
        {
            using var response = await hr.SendRawAsync(HttpMethod.Post, "/api/v1/hr/biometric-devices", Terminal(Serial(), BiometricConnection.Pull, address));
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, address);
        }

        // A terminal that pushes is known by the serial number printed on it, so it is registered with its address too, and a
        // push from any other address is refused (R-5).
        using (var anywhere = await hr.SendRawAsync(HttpMethod.Post, "/api/v1/hr/biometric-devices", Terminal(Serial(), BiometricConnection.Push, null, pushesFrom: null)))
        {
            anywhere.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await anywhere.Content.ReadAsStringAsync(Ct)).ShouldContain("Enter the terminal's IP address");
        }

        // A terminal that pushes is not read; a cashier cannot read terminals.
        var serial = Serial();
        var pushing = await hr.PostAsync<DeviceKeyIssued>("/api/v1/hr/biometric-devices", Terminal(serial, BiometricConnection.Push, null));
        pushing.ApiKey.ShouldNotBeNullOrEmpty();
        (await ReadAsync(hr, pushing.DeviceId)).Error.ShouldBe("This terminal sends its punches; HC360 does not read it.");
        (await server.Anonymous().Http.GetAsync($"/iclock/cdata?SN={serial}&options=all", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            "the test client does not post from 10.20.30.99");
        var cashier = await server.SignInAsync(HospitalServer.Users.Billing);
        using var forbidden = await cashier.SendRawAsync(HttpMethod.Post, $"/api/v1/hr/biometric-devices/{pushing.DeviceId}/read");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Once it is set to be read, punches pushed in its name are refused, even with the key it had while it pushed. The test
        // client calls from no address, so the terminal is first made one registered before addresses were required.
        await using (var legacy = server.Services.CreateAsyncScope())
        {
            await Devices(legacy).Where(d => d.Id == pushing.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.AllowedIpAddress, (string?)null), Ct);
        }

        var anonymous = server.Anonymous().Http;
        using (var accepted = await PushAsync(anonymous, serial, pushing.ApiKey!))
        {
            accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var loaded = (await hr.GetAsync<List<BiometricDeviceDto>>("/api/v1/hr/biometric-devices")).Single(d => d.Id == pushing.DeviceId);
        await hr.PutAsync($"/api/v1/hr/biometric-devices/{pushing.DeviceId}", Terminal(serial, BiometricConnection.Pull, "192.168.10.202") with { Version = loaded.Version });
        using (var refused = await PushAsync(anonymous, serial, pushing.ApiKey!))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await anonymous.GetAsync($"/iclock/cdata?SN={serial}&options=all", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var scope = server.Services.CreateAsyncScope();
        var refusals = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<IntegrationLog>().AsNoTracking()
            .Where(l => l.Integration == "biometric" && l.Reference == serial && l.Operation == "device.refused")
            .Select(l => l.Error)
            .ToListAsync(Ct);
        refusals.ShouldBe([
            "Device inactive or request from a non-allowed address.",
            "The terminal is set to be read by HC360, so it may not push.",
            "The terminal is set to be read by HC360, so it may not push.",
        ], ignoreOrder: true);
    }

    /// <summary>A terminal pushes <paramref name="punches"/>, or a punch a minute ago of a PIN nobody has.</summary>
    private static Task<HttpResponseMessage> PushAsync(HttpClient http, string serial, string key, params DevicePunchInput[] punches)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/hr/attendance/device-punches")
        {
            Content = JsonContent.Create(new DevicePunchBatch(punches.Length > 0 ? punches : [new DevicePunchInput(Pin(), DateTimeOffset.UtcNow.AddMinutes(-1), null, null)])),
        };
        request.Headers.Add("X-Device-Serial", serial);
        request.Headers.Add("X-Device-Key", key);
        return http.SendAsync(request, Ct);
    }

    [Fact]
    public async Task HR_sees_the_PINs_no_employee_has_most_punched_first()
    {
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var serial = Serial();
        var pushing = await hr.PostAsync<DeviceKeyIssued>("/api/v1/hr/biometric-devices", Terminal(serial, BiometricConnection.Push, null));
        await using (var legacy = server.Services.CreateAsyncScope())
        {
            await Devices(legacy).Where(d => d.Id == pushing.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.AllowedIpAddress, (string?)null), Ct);
        }

        // Someone punches with a PIN no employee was given, twice, and someone else once: an enrolment typo to fix.
        var twice = Pin();
        var once = Pin();
        while (once == twice)
        {
            once = Pin();
        }

        var now = DateTimeOffset.UtcNow;
        using (var pushed = await PushAsync(server.Anonymous().Http, serial, pushing.ApiKey!,
                   new DevicePunchInput(twice, now.AddMinutes(-30), null, null),
                   new DevicePunchInput(twice, now.AddMinutes(-20), null, null),
                   new DevicePunchInput(once, now.AddMinutes(-10), null, null)))
        {
            pushed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var unmatched = await hr.GetAsync<List<UnmatchedPinDto>>("/api/v1/hr/attendance/unmatched-pins");

        var shown = unmatched.Where(p => p.Pin == twice || p.Pin == once).ToList();
        shown.Select(p => p.Pin).ShouldBe([twice, once], "the PIN punched most comes first");
        shown[0].ShouldSatisfyAllConditions(
            p => p.PunchCount.ShouldBe(2),
            p => p.FirstPunchAt.ShouldBe(now.AddMinutes(-30), TimeSpan.FromSeconds(1)),
            p => p.LastPunchAt.ShouldBe(now.AddMinutes(-20), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_terminal_registered_without_an_address_is_pinned_to_the_first_one_it_calls_from()
    {
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var serial = Serial();
        var registered = await hr.PostAsync<DeviceKeyIssued>("/api/v1/hr/biometric-devices", Terminal(serial, BiometricConnection.Push, null));
        await using var scope = server.Services.CreateAsyncScope();
        await Devices(scope).Where(d => d.Id == registered.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.AllowedIpAddress, (string?)null), Ct);

        var ingestion = scope.ServiceProvider.GetRequiredService<BiometricIngestionService>();
        (await ingestion.FindDeviceAsync(serial, IPAddress.Parse("10.20.30.41"), Ct)).ShouldNotBeNull();
        (await Devices(scope).AsNoTracking().SingleAsync(d => d.Id == registered.DeviceId, Ct)).AllowedIpAddress.ShouldBe("10.20.30.41");

        // A device on another address posting under the printed serial number is refused from then on.
        (await ingestion.FindDeviceAsync(serial, IPAddress.Parse("10.20.30.66"), Ct)).ShouldBeNull();
        (await ingestion.FindDeviceAsync(serial, IPAddress.Parse("10.20.30.41"), Ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_terminal_that_goes_quiet_is_reported_to_HR_once_and_cleared_when_it_calls_again()
    {
        var hr = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var serial = Serial();
        var registered = await hr.PostAsync<DeviceKeyIssued>("/api/v1/hr/biometric-devices", Terminal(serial, BiometricConnection.Push, null) with { Name = $"Gate {serial}" });

        // The test client calls from no address, so the terminal is one registered before addresses were required.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await Devices(scope).Where(d => d.Id == registered.DeviceId).ExecuteUpdateAsync(s => s.SetProperty(d => d.AllowedIpAddress, (string?)null), Ct);
        }

        // The terminal calls (an ADMS command poll), then nothing is heard from it for 40 minutes.
        var anonymous = server.Anonymous().Http;
        (await anonymous.GetAsync($"/iclock/getrequest?SN={serial}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await Devices(scope).Where(d => d.Id == registered.DeviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastHandshakeAt, DateTimeOffset.UtcNow.AddMinutes(-40)), Ct);
            await scope.ServiceProvider.GetRequiredService<BiometricPullService>().ReportQuietTerminalsAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<BiometricPullService>().ReportQuietTerminalsAsync(Ct);
            (await Devices(scope).SingleAsync(d => d.Id == registered.DeviceId, Ct)).OfflineSince.ShouldNotBeNull();
        }

        var notices = await hr.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200");
        var notice = notices.Items.Where(n => n.Title == $"Attendance terminal Gate {serial} not heard from").ShouldHaveSingleItem();
        notice.ShouldSatisfyAllConditions(n => n.Link.ShouldBe("/hr/devices"), n => n.Severity.ShouldBe("warning"), n => n.Body!.ShouldContain("stopped calling HC360"));
        var device = (await hr.GetAsync<List<BiometricDeviceDto>>("/api/v1/hr/biometric-devices")).Single(d => d.Id == registered.DeviceId);
        device.ShouldSatisfyAllConditions(d => d.IsOnline.ShouldBeFalse(), d => d.OfflineSince.ShouldNotBeNull());

        // It calls again: the flag clears at once, and the integration log says so.
        (await anonymous.GetAsync($"/iclock/getrequest?SN={serial}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await Devices(scope).SingleAsync(d => d.Id == registered.DeviceId, Ct)).OfflineSince.ShouldBeNull();
            var logs = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<IntegrationLog>().AsNoTracking()
                .Where(l => l.Integration == "biometric" && l.Reference == serial)
                .OrderBy(l => l.CreatedAt)
                .Select(l => new { l.Operation, l.Success })
                .ToListAsync(Ct);
            logs.ShouldBe([new { Operation = "terminal.quiet", Success = false }, new { Operation = "terminal.back", Success = true }]);
        }
    }

    /// <summary>Reads the terminal now; while the schedule happens to be reading it, tries again shortly.</summary>
    private static async Task<TerminalPullResult> ReadAsync(ApiClient client, Guid deviceId)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await client.PostAsync<TerminalPullResult>($"/api/v1/hr/biometric-devices/{deviceId}/read");
            if (result.Error?.StartsWith("The terminal is being read already", StringComparison.Ordinal) != true || attempt == 20)
            {
                return result;
            }

            await Task.Delay(250, Ct);
        }
    }

    private static IQueryable<BiometricDevice> Devices(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<BiometricDevice>().IgnoreQueryFilters();

    /// <summary>A terminal to register; one that pushes posts from <paramref name="pushesFrom"/>.</summary>
    private static SaveBiometricDeviceRequest Terminal(string serial, BiometricConnection connection, string? address, string? pushesFrom = "10.20.30.99") =>
        new(serial, $"Terminal {serial}", "Main gate", null, "SpeedFace V5L", connection == BiometricConnection.Push ? pushesFrom : null, true, null,
            connection, address, null, BiometricDevice.MaxPullMinutes);

    private static string Serial() => "TST" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);

    private static string Pin() => Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture);

    /// <summary>Sets <c>integrations.biometric</c> for all facilities, and puts it back afterwards.</summary>
    private async Task<IAsyncDisposable> ReadingAsync(JsonObject value)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={Key}"))
            .FirstOrDefault(s => s.Key == Key && s.FacilityId is null)?.Value;
        await admin.SaveSettingAsync(Key, new SaveSettingRequest(value, null, false, null));
        return new Restore(() => admin.SaveSettingAsync(Key,
            new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
