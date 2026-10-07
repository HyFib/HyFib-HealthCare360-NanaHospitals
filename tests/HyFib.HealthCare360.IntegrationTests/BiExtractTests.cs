using System.Net;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Reports;
using HyFib.HealthCare360.Modules.Analytics.Features.Schedules;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// BI extracts (SRS BI-05): a report written on a schedule as a CSV file to the folder IT shares with Power BI, with a
/// refresh log beside the files and an alert when a refresh fails. The server is shared by the whole run, so each test
/// names its extracts uniquely and deletes them.
/// </summary>
public sealed class BiExtractTests(HospitalServer server)
{
    private const string Schedules = "/api/v1/analytics/schedules";
    private const string Folder = "/api/v1/analytics/extracts/folder";
    private const string Report = "revenue-by-department";

    [Fact]
    public async Task Only_schedule_managers_set_up_extracts_and_the_file_name_format_and_report_are_checked()
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        using (var refused = await billing.SendRawAsync(HttpMethod.Get, Folder))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var folder = await admin.GetAsync<ExtractFolderDto>(Folder);
        folder.Problem.ShouldBeNull("the test server can write its data folder");
        Path.GetDirectoryName(folder.FacilityFolder.ShouldNotBeNull()).ShouldBe(folder.Root);
        folder.RefreshLog.ShouldBe(Path.Combine(folder.Root, "refresh-log.csv"));

        await RefusedAsync(admin, Extract("Daily revenue!"), "extractName");
        await RefusedAsync(admin, Extract(UniqueName()) with { Format = ExportFormat.Pdf }, "format");
        await RefusedAsync(admin, Extract(UniqueName()) with { ReportId = "adverse-discharges" }, "reportId");
        await RefusedAsync(admin, Extract(UniqueName()) with { Parameters = new() { ["period"] = "custom", ["from"] = "2026-04-01", ["to"] = "2026-04-30" } }, "period");
        await RefusedAsync(admin, Extract(UniqueName()) with { Destination = ScheduleDestination.Email, Recipients = ["accounts@example.com"] }, "frequency");
    }

    [Fact]
    public async Task An_extract_replaces_its_csv_in_the_facility_folder_and_the_refresh_log_says_when_it_was_refreshed()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var folder = await admin.GetAsync<ExtractFolderDto>(Folder);
        var code = Path.GetFileName(folder.FacilityFolder.ShouldNotBeNull());
        var emailsBefore = (await admin.GetAsync<AnalyticsStatusDto>("/api/v1/analytics/status")).ActiveSchedules;
        var name = UniqueName();

        var extract = await admin.PostAsync<ScheduleDto>(Schedules, Extract("  " + name.ToUpperInvariant() + "  ", "Revenue " + name));
        await using var cleanup = new Cleanup(() => admin.SendRawAsync(HttpMethod.Delete, $"{Schedules}/{extract.Id}"));
        extract.ExtractName.ShouldBe(name, "kept trimmed and in lower case");
        extract.ExtractFile.ShouldBe(name + ".csv");
        extract.Format.ShouldBe(ExportFormat.Csv);
        extract.Recipients.ShouldBeEmpty();
        extract.Parameters["period"].ShouldBe("thisFinancialYear", "an extract covers the financial year unless told otherwise");
        extract.When.ShouldBe("Every hour at 15 minutes past");
        extract.ExtractState.ShouldBe(ExtractState.Waiting);
        await RefusedAsync(admin, Extract(name, "Same file"), "extractName");

        var delivery = await admin.PostAsync<DeliveryDto>($"{Schedules}/{extract.Id}/send-now");
        delivery.Status.ShouldBe(DeliveryStatus.Ready);
        delivery.FileName.ShouldBe(name + ".csv");
        delivery.FileAvailable.ShouldBeFalse("the folder holds the file, not HC360");
        delivery.Recipients.ShouldBeEmpty();

        var file = Path.Combine(folder.FacilityFolder, name + ".csv");
        var lines = await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken);
        lines[0].ShouldBe("Facility code,Department,OP revenue,IP revenue,Gross,Discounts & concessions,Net revenue,Share %");
        lines.Skip(1).ShouldAllBe(line => line.StartsWith(code + ",", StringComparison.Ordinal));
        lines.ShouldNotContain(line => line.StartsWith(code + ",Total,", StringComparison.Ordinal), "a BI tool adds up the rows itself");

        var logged = await LogLineAsync(folder, code, extract.Name);
        logged.ShouldNotBeNull().ShouldStartWith($"{code},{extract.Name},{Path.Combine(code, name + ".csv")},Revenue by department,Every hour at 15 minutes past,");
        logged.ShouldEndWith(",Fresh,");

        var refreshed = await admin.GetAsync<ScheduleDto>($"{Schedules}/{extract.Id}");
        refreshed.ExtractState.ShouldBe(ExtractState.Fresh);
        refreshed.LastSucceededAt.ShouldNotBeNull();
        var status = await admin.GetAsync<AnalyticsStatusDto>("/api/v1/analytics/status");
        status.ActiveExtracts.ShouldBeGreaterThanOrEqualTo(1);
        status.LastExtractRefreshAt.ShouldNotBeNull();
        status.ActiveSchedules.ShouldBe(emailsBefore, "extracts are not counted as e-mails");

        var renamed = await admin.PutAsync<ScheduleDto>($"{Schedules}/{extract.Id}", Extract(name + "-v2", extract.Name) with { Version = refreshed.Version });
        renamed.ExtractState.ShouldBe(ExtractState.Waiting, "the new file is written at the next refresh");
        File.Exists(file).ShouldBeFalse("a BI tool reports the old file missing rather than reading data that is no longer refreshed");
        (await LogLineAsync(folder, code, extract.Name)).ShouldNotBeNull().ShouldContain($",{Path.Combine(code, name + "-v2.csv")},");

        await admin.PostAsync<DeliveryDto>($"{Schedules}/{extract.Id}/send-now");
        var renamedFile = Path.Combine(folder.FacilityFolder, name + "-v2.csv");
        File.Exists(renamedFile).ShouldBeTrue();
        using (var deleted = await admin.SendRawAsync(HttpMethod.Delete, $"{Schedules}/{extract.Id}"))
        {
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        File.Exists(renamedFile).ShouldBeFalse();
        (await LogLineAsync(folder, code, extract.Name)).ShouldBeNull("a deleted extract leaves the refresh log");
    }

    [Fact]
    public async Task A_refresh_that_cannot_write_the_file_keeps_the_last_file_and_tells_the_schedule_managers_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var folder = await admin.GetAsync<ExtractFolderDto>(Folder);
        var code = Path.GetFileName(folder.FacilityFolder.ShouldNotBeNull());
        var name = UniqueName();
        var extract = await admin.PostAsync<ScheduleDto>(Schedules, Extract(name));
        await using var cleanup = new Cleanup(() => admin.SendRawAsync(HttpMethod.Delete, $"{Schedules}/{extract.Id}"));
        var alert = $"BI extract \"{extract.Name}\" was not refreshed";

        // A folder where the file should be: the server cannot replace it, as when the share is read-only.
        var file = Path.Combine(folder.FacilityFolder, name + ".csv");
        Directory.CreateDirectory(file);
        try
        {
            for (var run = 1; run <= 2; run++)
            {
                using var failed = await admin.SendRawAsync(HttpMethod.Post, $"{Schedules}/{extract.Id}/send-now");
                failed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await failed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("analytics.delivery_failed");
            }

            var failing = await admin.GetAsync<ScheduleDto>($"{Schedules}/{extract.Id}");
            failing.ExtractState.ShouldBe(ExtractState.Failing);
            failing.LastStatus.ShouldBe(ScheduleRunStatus.Failed);
            failing.LastError.ShouldNotBeNull().ShouldContain("could not be written to the extract folder");
            (await admin.GetAsync<AnalyticsStatusDto>("/api/v1/analytics/status")).FailingExtracts.ShouldBeGreaterThanOrEqualTo(1);
            (await LogLineAsync(folder, code, extract.Name)).ShouldNotBeNull().ShouldContain(",Failing,");

            var notifications = await admin.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=100");
            var alerts = notifications.Items.Where(n => n.Title == alert).ToList();
            alerts.Count.ShouldBe(1, "told when the extract starts failing, not at every failed refresh");
            alerts[0].Link.ShouldBe("/analytics/extracts");
            alerts[0].Severity.ShouldBe("warning");
        }
        finally
        {
            Directory.Delete(file);
        }

        (await admin.PostAsync<DeliveryDto>($"{Schedules}/{extract.Id}/send-now")).Status.ShouldBe(DeliveryStatus.Ready);
        (await admin.GetAsync<ScheduleDto>($"{Schedules}/{extract.Id}")).ExtractState.ShouldBe(ExtractState.Fresh);
        File.Exists(file).ShouldBeTrue();
    }

    private static SaveScheduleRequest Extract(string extractName, string? name = null) => new(
        Name: name ?? "Revenue " + extractName,
        ReportId: Report,
        Parameters: null,
        Format: ExportFormat.Csv,
        Frequency: ScheduleFrequency.Hourly,
        Time: "00:15",
        DayOfWeek: null,
        DayOfMonth: null,
        Recipients: null,
        Destination: ScheduleDestination.ExtractFolder,
        ExtractName: extractName);

    private static string UniqueName() => "revenue-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>The refresh log's line for the extract named <paramref name="extract"/> of the facility, if it lists it.</summary>
    private static async Task<string?> LogLineAsync(ExtractFolderDto folder, string code, string extract)
    {
        var lines = await File.ReadAllLinesAsync(folder.RefreshLog, TestContext.Current.CancellationToken);
        lines[0].ShouldBe("Facility code,Extract,File,Report,Refresh,Last refreshed (IST),State,Problem");
        return lines.SingleOrDefault(line => line.StartsWith($"{code},{extract},", StringComparison.Ordinal));
    }

    private static async Task RefusedAsync(ApiClient admin, SaveScheduleRequest request, string field)
    {
        using var response = await admin.SendRawAsync(HttpMethod.Post, Schedules, request);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"\"{field}\"");
    }

    private sealed class Cleanup(Func<Task<HttpResponseMessage>> cleanup) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => (await cleanup()).Dispose();
    }
}
