using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Quality.Domain;
using HyFib.HealthCare360.Modules.Quality.Features.Risks;
using HyFib.HealthCare360.Platform.Numbering;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Number formats an administrator sets up before go-live (Hospital setup → Number formats): every series is listed
/// before its first number, a next number set there is the next one issued, and changes that would break numbers are
/// refused. The database is shared by the whole run, so these tests use the risk register, which no other test numbers,
/// and put back what they change.
/// </summary>
public sealed class NumberFormatTests(HospitalServer server)
{
    private const string Base = "/api/v1/admin/number-series";

    [Fact]
    public async Task A_series_nobody_has_used_yet_is_listed_with_its_defaults()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        var list = await admin.GetAsync<NumberSeriesList>(Base);

        list.FacilityCount.ShouldBeGreaterThanOrEqualTo(1);
        list.Series.ShouldNotContain(s => s.Key.StartsWith("token:", StringComparison.Ordinal), "queue tokens are internal");
        list.Series.Select(s => s.Area).ShouldBe(list.Series.Select(s => s.Area).Order(StringComparer.OrdinalIgnoreCase), "listed by area");

        // Neither the demonstration data nor any test raises a CAPA.
        var capa = list.Series.Single(s => s.Key == "quality.capa");
        capa.ShouldSatisfyAllConditions(
            () => capa.Format.ShouldBe("{FAC}CAPA{YY}{SEQ:4}"),
            () => capa.ResetPolicy.ShouldBe(NumberResetPolicy.Yearly),
            () => capa.DefaultFormat.ShouldBe(capa.Format),
            () => capa.IsCustomised.ShouldBeFalse(),
            () => capa.IsLocked.ShouldBeFalse(),
            () => capa.Scope.ShouldBe("facility"),
            () => capa.LastNumber.ShouldBe(0L),
            () => capa.NextExample.ShouldStartWith(capa.FacilityCode + "CAPA"),
            () => capa.NextExample.ShouldEndWith("0001"));

        var uhid = list.Series.Single(s => s.Key == "uhid");
        uhid.Scope.ShouldBe("global");
        uhid.IsLocked.ShouldBeTrue();
        uhid.LockedReason.ShouldNotBeNullOrWhiteSpace();
        uhid.LastNumber.ShouldBeGreaterThan(0L, "the demonstration patients have UHIDs");
    }

    [Fact]
    public async Task The_next_number_an_administrator_sets_is_the_next_one_issued()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var before = await SeriesAsync(admin, "quality.risk");
        var startAt = before.LastNumber + 500;
        await using var restore = new Restore(async () =>
        {
            var now = await SeriesAsync(admin, "quality.risk");
            await admin.PutAsync($"{Base}/quality.risk", new SaveNumberSeriesRequest(now.DefaultFormat, now.DefaultResetPolicy, null, now.Format, now.ResetPolicy));
        });

        await admin.PutAsync($"{Base}/quality.risk", new SaveNumberSeriesRequest("{FAC}/RSK/{SEQ:4}", NumberResetPolicy.Never, startAt, before.Format, before.ResetPolicy));

        var changed = await SeriesAsync(admin, "quality.risk");
        changed.Format.ShouldBe("{FAC}/RSK/{SEQ:4}");
        changed.IsCustomised.ShouldBeTrue();
        changed.LastNumber.ShouldBe(startAt - 1);
        changed.NextExample.ShouldBe($"{changed.FacilityCode}/RSK/{startAt.ToString("0000", CultureInfo.InvariantCulture)}");

        var created = await admin.PostAsync<RiskCreated>("/api/v1/quality/risks", new SaveRiskRequest(
            "Wet floor near the OPD entrance", null, RiskCategory.FireAndSafety, null, 3, 3, null, null, null, null, null,
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(90), RiskStatus.Open, null));

        created.RiskNumber.ShouldBe(changed.NextExample, "the risk register issues the number the administrator chose");
        (await SeriesAsync(admin, "quality.risk")).LastNumber.ShouldBe(startAt);
    }

    [Fact]
    public async Task Changes_that_would_break_numbers_are_refused()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var list = await admin.GetAsync<NumberSeriesList>(Base);
        var opBill = list.Series.Single(s => s.Key == "billing.bill.op");
        var ipBill = list.Series.Single(s => s.Key == "billing.bill.ip");
        var uhid = list.Series.Single(s => s.Key == "uhid");

        // Someone else changed the format meanwhile: the administrator sees theirs before overwriting it.
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, $"{Base}/billing.bill.op",
                   new SaveNumberSeriesRequest("{FAC}/OP/{YY}/{SEQ:6}", opBill.ResetPolicy, null, "{FAC}OLD{YY}{SEQ:7}", opBill.ResetPolicy)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await ProblemAsync(stale)).GetProperty("code").GetString().ShouldBe("numbering.changed");
        }

        // OP and IP bills share a table: one format would issue the same bill numbers twice.
        using (var duplicate = await admin.SendRawAsync(HttpMethod.Put, $"{Base}/billing.bill.op",
                   new SaveNumberSeriesRequest(ipBill.Format, opBill.ResetPolicy, null, opBill.Format, opBill.ResetPolicy)))
        {
            duplicate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ProblemAsync(duplicate)).GetProperty("errors").GetProperty("format")[0].GetString().ShouldStartWith("IP bill already uses this format.");
        }

        // UHIDs are read back by their shape: only the next number may change.
        using (var locked = await admin.SendRawAsync(HttpMethod.Put, $"{Base}/uhid",
                   new SaveNumberSeriesRequest("{FAC}/{YY}/{SEQ:6}", uhid.ResetPolicy, null, uhid.Format, uhid.ResetPolicy)))
        {
            locked.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await ProblemAsync(locked)).GetProperty("code").GetString().ShouldBe("numbering.locked");
        }

        using (var unknown = await admin.SendRawAsync(HttpMethod.Put, $"{Base}/no.such.series",
                   new SaveNumberSeriesRequest("{SEQ}", NumberResetPolicy.Never, null, "{SEQ}", NumberResetPolicy.Never)))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ProblemAsync(unknown)).GetProperty("code").GetString().ShouldBe("numbering.not_found");
        }

        (await SeriesAsync(admin, "billing.bill.op")).Format.ShouldBe(opBill.Format, "nothing refused was saved");
    }

    private static async Task<NumberSeriesItem> SeriesAsync(ApiClient admin, string key) =>
        (await admin.GetAsync<NumberSeriesList>(Base)).Series.Single(s => s.Key == key);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, TestContext.Current.CancellationToken);

    private sealed record RiskCreated(Guid Id, string RiskNumber);

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
