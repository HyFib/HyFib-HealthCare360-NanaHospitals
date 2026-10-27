using System.Net;
using HyFib.HealthCare360.Modules.Analytics.Domain;
using HyFib.HealthCare360.Modules.Analytics.Features.Reports;
using HyFib.HealthCare360.Modules.Analytics.Features.Schedules;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A scheduled report is as closed as the report itself: someone who may schedule reports but not open revenue reports
/// (an MIS analyst) does not see a revenue schedule another person set up, nor its files, nor sends or deletes it.
/// </summary>
public sealed class ScheduleReportAccessTests(HospitalServer server)
{
    private const string Schedules = "/api/v1/analytics/schedules";

    [Fact]
    public async Task A_schedule_of_a_report_one_cannot_open_is_closed_to_them()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var schedule = await admin.PostAsync<ScheduleDto>(Schedules, new SaveScheduleRequest(
            Name: "Revenue " + Guid.NewGuid().ToString("N")[..8], ReportId: "revenue-by-department", Parameters: null, Format: ExportFormat.Csv,
            Frequency: ScheduleFrequency.Daily, Time: "07:00", DayOfWeek: null, DayOfMonth: null, Recipients: ["accounts@example.com"],
            Destination: ScheduleDestination.Email, ExtractName: null));
        try
        {
            var delivery = await admin.PostAsync<DeliveryDto>($"{Schedules}/{schedule.Id}/send-now");
            delivery.FileAvailable.ShouldBeTrue();

            var analyst = await server.SignInAsync(HospitalServer.Users.MisAnalyst);
            (await analyst.GetAsync<List<ScheduleDto>>(Schedules)).ShouldNotContain(s => s.Id == schedule.Id);
            foreach (var (method, path) in new[]
                     {
                         (HttpMethod.Get, $"{Schedules}/{schedule.Id}"),
                         (HttpMethod.Get, $"{Schedules}/{schedule.Id}/deliveries"),
                         (HttpMethod.Get, $"/api/v1/analytics/deliveries/{delivery.Id}/file"),
                         (HttpMethod.Post, $"{Schedules}/{schedule.Id}/send-now"),
                         (HttpMethod.Delete, $"{Schedules}/{schedule.Id}?version={schedule.Version}"),
                     })
            {
                using var refused = await analyst.SendRawAsync(method, path);
                refused.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{method} {path}");
            }

            (await admin.GetAsync<ScheduleDto>($"{Schedules}/{schedule.Id}")).Id.ShouldBe(schedule.Id, "still there for the admin");
        }
        finally
        {
            (await admin.SendRawAsync(HttpMethod.Delete, $"{Schedules}/{schedule.Id}")).Dispose();
        }
    }
}
