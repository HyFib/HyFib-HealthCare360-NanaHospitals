using System.Net;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Downtime;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The downtime log over the real API (NABH IMS: downtime monitored, its procedure rehearsed): the quality team records a
/// downtime after the fact, ends one recorded while it lasts, signs off the back-entry of the paper records, and reads
/// the period's figures, which count each kind of downtime and the time it took to restore and to enter the paper.
/// The figures cover this test's own past period, a year back, so other tests' downtime does not meet them.
/// </summary>
public sealed class DowntimeLogTests(HospitalServer server)
{
    private const string Log = "/api/v1/quality/downtime";

    [Fact]
    public async Task Downtime_is_recorded_ended_signed_off_and_counted()
    {
        var quality = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Get, Log))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // A day a year ago, with an unplanned outage of 90 minutes, a planned one of 30 and a drill.
        var day = DateTimeOffset.UtcNow.Date.AddYears(-1).AddDays(-Random.Shared.Next(1, 300));
        var start = new DateTimeOffset(day, TimeSpan.Zero).AddHours(4);
        var outage = await quality.PostAsync<DowntimeEventDto>(Log, new RecordDowntimeRequest(
            DowntimeKind.Unplanned, DowntimeCause.NetworkOutage, "All of HC360", "Core switch failed", start, start.AddMinutes(90), "Switch replaced"));
        (outage.Status, outage.Minutes).ShouldBe((DowntimeStatus.BackEntryPending, 90));
        outage.Number.ShouldContain("DT");

        await quality.PostAsync<DowntimeEventDto>(Log, new RecordDowntimeRequest(
            DowntimeKind.Planned, DowntimeCause.Maintenance, "All of HC360", "Upgrade", start.AddHours(6), start.AddHours(6).AddMinutes(30), null));
        var drill = await quality.PostAsync<DowntimeEventDto>(Log, new RecordDowntimeRequest(
            DowntimeKind.Drill, DowntimeCause.Other, "Registration and OPD", "Quarterly drill", start.AddHours(8), null, null));
        drill.Status.ShouldBe(DowntimeStatus.Ongoing);

        // The drill ends, and the outage's paper is entered and signed off.
        var ended = await quality.PostAsync<DowntimeEventDto>($"{Log}/{drill.Id}/end", new EndDowntimeRequest(start.AddHours(9), "Desks worked from the kits", drill.Version));
        ended.Status.ShouldBe(DowntimeStatus.BackEntryPending);
        var closed = await quality.PostAsync<DowntimeEventDto>($"{Log}/{outage.Id}/back-entry", new SignOffBackEntryRequest(37, "All entered and checked", outage.Version));
        (closed.Status, closed.PaperRecords).ShouldBe((DowntimeStatus.Closed, 37));

        // A stale version, an end before the start, and a second sign-off are refused.
        using (var stale = await quality.SendRawAsync(HttpMethod.Post, $"{Log}/{drill.Id}/back-entry", new SignOffBackEntryRequest(1, null, drill.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var backwards = await quality.SendRawAsync(HttpMethod.Post, Log, new RecordDowntimeRequest(
                   DowntimeKind.Unplanned, DowntimeCause.PowerFailure, "Ward 3", null, start, start.AddMinutes(-5), null)))
        {
            backwards.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var again = await quality.SendRawAsync(HttpMethod.Post, $"{Log}/{outage.Id}/back-entry", new SignOffBackEntryRequest(1, null, closed.Version)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var date = DateOnly.FromDateTime(day);
        var summary = await quality.GetAsync<DowntimeSummary>($"{Log}/summary?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        (summary.Unplanned, summary.Planned, summary.Drills).ShouldBe((1, 1, 1));
        (summary.UnplannedMinutes, summary.PlannedMinutes, summary.LongestUnplannedMinutes, summary.MeanMinutesToRestore).ShouldBe((90, 30, 90, 90));
        // 90 minutes of a 1,440-minute day: 93.75 % available.
        summary.UnplannedAvailabilityPercent.ShouldBe(93.75m);
        summary.MeanHoursToBackEntry.ShouldNotBeNull();

        var listed = await quality.GetAsync<List<DowntimeEventDto>>($"{Log}?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}");
        listed.Select(e => e.Kind).ShouldBe([DowntimeKind.Drill, DowntimeKind.Planned, DowntimeKind.Unplanned]);
        var pending = await quality.GetAsync<List<DowntimeEventDto>>($"{Log}?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}&status=BackEntryPending");
        pending.Select(e => e.Kind).ShouldBe([DowntimeKind.Drill, DowntimeKind.Planned]);
        (await quality.GetAsync<List<DowntimeEventDto>>($"{Log}?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}&status=Closed")).ShouldHaveSingleItem().Id.ShouldBe(outage.Id);
    }
}
