using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.HR.Features.Rostering;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Incidents;
using HyFib.HealthCare360.Modules.Quality.Features.Indicators;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Adjustments;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS REL-04 on the edits that had no version before: each saves only over the record as it was loaded. A save made to
/// an older version, or (where the first save creates the record) one that never saw it, is refused (409).
/// </summary>
public sealed class AdminEditVersionTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_incident_analysis_saved_over_a_newer_one_is_refused_even_when_only_its_factors_change()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var incident = await admin.PostAsync<IncidentReceipt>("/api/v1/quality/incidents", new ReportIncidentRequest(
            IncidentType.PatientFall, IncidentSeverity.NoHarm, DateTimeOffset.UtcNow.AddHours(-2), "Ward 3, bed 12", null, null, null,
            "Patient slipped while walking to the washroom; no injury.", null, null, null, false, false));
        var path = $"/api/v1/quality/incidents/{incident.Id}/investigation";
        SaveInvestigationRequest Analysis(string factor, uint? version) => new(
            RcaMethod.FiveWhys, "Why did the patient fall?", ["Floor was wet"], [new RcaFactorInput(ContributingFactorCategory.Environment, factor, true)],
            null, null, null, version);

        // The first save starts the analysis; a second "first" save would overwrite it unseen.
        await admin.PutAsync<Guid>(path, Analysis("Wet floor", null));
        using (var unseen = await admin.SendRawAsync(HttpMethod.Put, path, Analysis("Poor lighting", null)))
        {
            unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        // The factors are rows of their own: changing only them still checks the analysis' version.
        var loaded = await admin.GetAsync<InvestigationDto>(path);
        await admin.PutAsync<Guid>(path, Analysis("Wet floor near the washroom", loaded.Version));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, Analysis("Poor lighting", loaded.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await admin.GetAsync<InvestigationDto>(path)).Factors.ShouldHaveSingleItem().Description.ShouldBe("Wet floor near the washroom");
    }

    [Fact]
    public async Task A_quality_indicators_value_for_a_period_is_not_saved_over_unseen()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var created = await admin.PostAsync<Created>("/api/v1/quality/indicators", new SaveIndicatorRequest(
            "RV-" + Letters(6), "Hand hygiene (version test)", "Infection control", "Opportunities taken", "Opportunities observed",
            IndicatorUnit.Percent, null, 90m, IndicatorDirection.HigherIsBetter, IndicatorFrequency.Monthly, null, 900, true, null));
        var period = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        var path = $"/api/v1/quality/indicators/{created.Id}/values";

        await admin.PutAsync<IndicatorValueDto>(path, new RecordIndicatorValueRequest(period, 45m, 50m, null));
        using (var unseen = await admin.SendRawAsync(HttpMethod.Put, path, new RecordIndicatorValueRequest(period, 40m, 50m, null)))
        {
            unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var loaded = (await admin.GetAsync<List<IndicatorValueDto>>(path)).Single(v => v.PeriodStart == period);
        await admin.PutAsync<IndicatorValueDto>(path, new RecordIndicatorValueRequest(period, 46m, 50m, "Recounted", loaded.Version));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, new RecordIndicatorValueRequest(period, 30m, 50m, null, loaded.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await admin.GetAsync<List<IndicatorValueDto>>(path)).Single(v => v.PeriodStart == period).Numerator.ShouldBe(46m);
    }

    [Fact]
    public async Task A_tariff_price_is_changed_only_as_it_was_loaded_while_the_bulk_import_replaces_prices()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var plan = await admin.PostAsync<TariffPlanDetail>("/api/v1/billing/tariffs", new SaveTariffPlanRequest(
            "RV" + Letters(6).ToUpperInvariant(), "Version test plan", TariffType.Corporate, null, null, 0m, false, false, null, null, null));
        var service = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            "SV-" + Letters(8).ToUpperInvariant(), "Dressing (version test)", null, ServiceCategory.Procedure, null, 400m, 0m, null, true, false, null, true, null));
        var from = new DateOnly(2031, 1, 1);
        var path = $"/api/v1/billing/tariffs/{plan.Id}/prices";

        await admin.PutAsync<BulkTariffPricesResult>(path, new SaveTariffPriceRequest(service.Id, 500m, from, null));
        using (var unseen = await admin.SendRawAsync(HttpMethod.Put, path, new SaveTariffPriceRequest(service.Id, 550m, from, null)))
        {
            unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var loaded = await PriceAsync(admin, plan.Id, service.Id);
        await admin.PutAsync<BulkTariffPricesResult>(path, new SaveTariffPriceRequest(service.Id, 600m, from, null, loaded.Version));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, new SaveTariffPriceRequest(service.Id, 650m, from, null, loaded.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await PriceAsync(admin, plan.Id, service.Id)).Price.ShouldBe(600m);

        // A price list imported from a spreadsheet sets the prices it lists, on purpose.
        (await admin.PostAsync<BulkTariffPricesResult>($"{path}/bulk", new BulkTariffPricesRequest([new SaveTariffPriceRequest(service.Id, 700m, from, null)])))
            .Updated.ShouldBe(1);
        (await PriceAsync(admin, plan.Id, service.Id)).Price.ShouldBe(700m);
    }

    [Fact]
    public async Task Counted_lines_are_saved_only_on_the_count_sheet_as_it_was_loaded()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var (count, started) = await StartCountWithLinesAsync(admin);
        try
        {
            var path = $"/api/v1/inventory/counts/{started.Id}/lines";
            var line = count.Lines[0];
            await admin.PutAsync(path, new RecordCountsRequest([new CountEntry(line.Id, 1m, null)], count.Version));

            // A second counter, still holding the sheet as it was, would overwrite the first one's count.
            using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, new RecordCountsRequest([new CountEntry(line.Id, 9m, null)], count.Version)))
            {
                stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            var reloaded = await admin.GetAsync<CountDetail>($"/api/v1/inventory/counts/{started.Id}");
            reloaded.Lines.Single(l => l.Id == line.Id).CountedQuantity.ShouldBe(1m);
            await admin.PutAsync(path, new RecordCountsRequest([new CountEntry(line.Id, 2m, null)], reloaded.Version));
        }
        finally
        {
            await admin.PostAsync($"/api/v1/inventory/counts/{started.Id}/cancel");
        }
    }

    [Fact]
    public async Task A_count_is_submitted_only_as_it_was_last_read()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var (count, started) = await StartCountWithLinesAsync(admin);
        var submitted = false;
        try
        {
            // Counted as the books have it, so the count posts no adjustment.
            var line = count.Lines[0];
            await admin.PutAsync($"/api/v1/inventory/counts/{started.Id}/lines", new RecordCountsRequest([new CountEntry(line.Id, line.SystemQuantity, null)], count.Version));
            var read = await admin.GetAsync<CountDetail>($"/api/v1/inventory/counts/{started.Id}");

            // The sheet as it was before that count: submitting it would post variances nobody looked at.
            using (var stale = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/counts/{started.Id}/submit", new SubmitCountRequest(count.Version)))
            {
                stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            using (var unversioned = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/counts/{started.Id}/submit", new SubmitCountRequest(0)))
            {
                unversioned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            }

            (await admin.GetAsync<CountDetail>($"/api/v1/inventory/counts/{started.Id}")).Status.ShouldBe(StockCountStatus.InProgress);
            using (var accepted = await admin.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/counts/{started.Id}/submit", new SubmitCountRequest(read.Version)))
            {
                accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Ct));
            }

            submitted = true;
            (await admin.GetAsync<CountDetail>($"/api/v1/inventory/counts/{started.Id}")).Status.ShouldNotBe(StockCountStatus.InProgress);
        }
        finally
        {
            if (!submitted)
            {
                await admin.PostAsync($"/api/v1/inventory/counts/{started.Id}/cancel");
            }
        }
    }

    [Fact]
    public async Task A_roster_change_replaces_only_the_entries_the_planner_saw()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var employeeId = (await admin.GetAsync<PagedResult<JsonElement>>("/api/v1/hr/employees?status=Active&pageSize=50")).Items
            .Select(e => e.GetProperty("id").GetGuid()).First();
        var shift = (await admin.GetAsync<List<ShiftDto>>("/api/v1/hr/shifts")).First(s => s.IsActive);
        var date = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(400 + Random.Shared.Next(300));
        var rosterPath = $"/api/v1/hr/roster?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}&employeeId={employeeId}";
        SaveRosterRequest Cell(bool weekOff, IReadOnlyList<RosterEntryVersion>? replacing) =>
            new([new RosterAssignmentInput(employeeId, date, weekOff ? null : shift.Id, weekOff, null)], true, true, replacing);

        await admin.PutAsync<RosterSaveResult>("/api/v1/hr/roster", Cell(weekOff: false, null));
        var entry = (await admin.GetAsync<List<RosterEntryDto>>(rosterPath)).ShouldHaveSingleItem();
        try
        {
            // The planner did not see the shift now in the cell: their change does not silently replace it.
            using (var unseen = await admin.SendRawAsync(HttpMethod.Put, "/api/v1/hr/roster", Cell(weekOff: true, null)))
            {
                unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            }

            await admin.PutAsync<RosterSaveResult>("/api/v1/hr/roster", Cell(weekOff: true, [new RosterEntryVersion(entry.Id, entry.Version)]));
            using (var stale = await admin.SendRawAsync(HttpMethod.Put, "/api/v1/hr/roster", Cell(weekOff: false, [new RosterEntryVersion(entry.Id, entry.Version)])))
            {
                stale.StatusCode.ShouldBe(HttpStatusCode.Conflict, "The entry the planner saw was replaced meanwhile.");
            }

            (await admin.GetAsync<List<RosterEntryDto>>(rosterPath)).ShouldHaveSingleItem().IsWeekOff.ShouldBeTrue();
        }
        finally
        {
            foreach (var left in await admin.GetAsync<List<RosterEntryDto>>(rosterPath))
            {
                using var removed = await admin.SendRawAsync(HttpMethod.Delete, $"/api/v1/hr/roster/{left.Id}");
            }
        }
    }

    /// <summary>A count started on the first store that has stock to count, and the sheet as loaded.</summary>
    private static async Task<(CountDetail Count, DocumentCreated Started)> StartCountWithLinesAsync(ApiClient admin)
    {
        foreach (var store in await admin.GetAsync<List<JsonElement>>("/api/v1/inventory/stores"))
        {
            var started = await admin.PostAsync<DocumentCreated>("/api/v1/inventory/counts", new StartCountRequest(store.GetProperty("id").GetGuid(), null, null, "REL-04 test"));
            var count = await admin.GetAsync<CountDetail>($"/api/v1/inventory/counts/{started.Id}");
            if (count.Lines.Count > 0)
            {
                return (count, started);
            }

            await admin.PostAsync($"/api/v1/inventory/counts/{started.Id}/cancel");
        }

        throw new InvalidOperationException("No store has stock to count.");
    }

    private static async Task<TariffPriceDto> PriceAsync(ApiClient admin, Guid planId, Guid serviceId) =>
        (await admin.GetAsync<PagedResult<TariffPriceDto>>($"/api/v1/billing/tariffs/{planId}/prices?pageSize=50")).Items.Single(p => p.ServiceItemId == serviceId);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
