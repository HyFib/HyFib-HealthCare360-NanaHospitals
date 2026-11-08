using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.ColdChain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The cold-chain temperature log over the real API: a refrigerator set up with its range, readings entered by hand and
/// sent by a logger with its own key, an excursion alerted once to the store's in-charge with the cold-chain stock kept
/// there, the action recorded, the daily register, and a missed twice-daily check alerted.
/// </summary>
public sealed class ColdChainLogTests(HospitalServer server)
{
    private const string Base = "/api/v1/inventory/cold-chain";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_excursion_is_alerted_once_to_the_in_charge_with_the_insulin_kept_there_and_ends_back_in_range()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var suffix = Letters(6);
        var inCharge = await UserIdAsync(pharmacist);
        var store = (await admin.PostAsync<JsonElement>("/api/v1/inventory/stores",
            new SaveStoreRequest($"CC{suffix}", $"Ward pharmacy {suffix}", StoreType.Pharmacy, null, inCharge, null, true, null))).GetProperty("id").GetGuid();
        var insulin = await InsulinAsync(admin, suffix);
        await ReceiveAsync(admin, store, insulin, $"INS{suffix}");

        // The pharmacist sets up the refrigerator; with no range given it takes 2–8 °C.
        var unitId = await CreateUnitAsync(pharmacist, store, $"F{suffix}", $"Fridge {suffix}");
        var unit = (await pharmacist.GetAsync<List<ColdStorageUnitDto>>($"{Base}/units?storeId={store}")).ShouldHaveSingleItem();
        (unit.Id, unit.MinCelsius, unit.MaxCelsius).ShouldBe((unitId, 2m, 8m));

        // The nurse reads 5 °C: in range, no alert.
        (await nurse.PostAsync<TemperatureReadingRecorded>($"{Base}/units/{unitId}/readings", new RecordTemperatureRequest(5m, null, null)))
            .OutOfRange.ShouldBeFalse();
        (await AlertsAsync(inCharge, suffix)).ShouldBeEmpty();

        // 11.2 °C: out of range, an excursion, and one alert to the in-charge naming the insulin and its batch.
        var warm = await nurse.PostAsync<TemperatureReadingRecorded>($"{Base}/units/{unitId}/readings", new RecordTemperatureRequest(11.2m, null, "Door found ajar"));
        (warm.OutOfRange, warm.AllowedRange).ShouldBe((true, "2 °C to 8 °C"));
        var excursionId = warm.ExcursionId.ShouldNotBeNull();
        var alert = (await AlertsAsync(inCharge, suffix)).ShouldHaveSingleItem();
        alert.GetProperty("title").GetString().ShouldBe($"Temperature out of range: Fridge {suffix}, Ward pharmacy {suffix}");
        alert.GetProperty("body").GetString().ShouldNotBeNull().ShouldContain($"batch INS{suffix}");

        // Still warm a little later: the same excursion, no second alert. Back to 6 °C: it ends.
        await nurse.PostAsync<TemperatureReadingRecorded>($"{Base}/units/{unitId}/readings", new RecordTemperatureRequest(12m, null, null));
        await nurse.PostAsync<TemperatureReadingRecorded>($"{Base}/units/{unitId}/readings", new RecordTemperatureRequest(6m, null, null));
        (await AlertsAsync(inCharge, suffix)).Count.ShouldBe(1);

        // The excursion waits for its action; the nurse records what was done.
        var open = (await nurse.GetAsync<List<ColdChainExcursionDto>>($"{Base}/excursions?storeId={store}&openOnly=true")).ShouldHaveSingleItem();
        (open.Id, open.HighestCelsius, open.ReadingsOutOfRange, open.EndedAt is not null, open.Extreme).ShouldBe((excursionId, 12m, 2, true, "up to 12 °C"));
        await nurse.PostAsync($"{Base}/excursions/{excursionId}/action", new ExcursionActionRequest("Insulin moved to the IP pharmacy fridge; kept, as below 25 °C for under an hour"));
        (await nurse.GetAsync<List<ColdChainExcursionDto>>($"{Base}/excursions?storeId={store}&openOnly=true")).ShouldBeEmpty();

        // The day's register: every reading, the low and the high, and the excursion with its action.
        var register = await pharmacist.GetAsync<ColdChainDailyRegister>($"{Base}/units/{unitId}/register");
        register.Readings.Select(r => r.Celsius).ShouldBe([5m, 11.2m, 12m, 6m]);
        (register.LowestCelsius, register.HighestCelsius).ShouldBe(((decimal?)5m, (decimal?)12m));
        register.Excursions.ShouldHaveSingleItem().ActionTaken.ShouldNotBeNull().ShouldStartWith("Insulin moved");
        register.Checks.Select(c => c.Slot).ShouldBe([ColdChainSlot.Morning, ColdChainSlot.Evening]);

        // A doctor (no cold-chain permission) does not record readings.
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        using var refused = await doctor.SendRawAsync(HttpMethod.Post, $"{Base}/units/{unitId}/readings", new RecordTemperatureRequest(5m, null, null));
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_logger_sends_readings_with_its_own_key_resends_are_skipped_and_a_wrong_key_is_refused()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var suffix = Letters(6);
        var store = (await admin.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "IPPH").Id;
        var unitId = await CreateUnitAsync(admin, store, $"L{suffix}", $"Vaccine fridge {suffix}");
        var key = await admin.PostAsync<ColdChainLoggerKey>($"{Base}/units/{unitId}/logger-key");
        (key.Path, key.Header).ShouldBe(($"{Base}/logger/readings", "X-Device-Key"));

        var now = DateTimeOffset.UtcNow;
        var batch = new LoggerUploadRequest([new LoggerReadingInput(now.AddMinutes(-10), 4.5m), new LoggerReadingInput(now.AddMinutes(-5), 4.7m)]);
        var anonymous = server.Anonymous().Http;
        (await UploadAsync(anonymous, key.Key, batch)).ShouldBe((HttpStatusCode.OK, 2, 0));
        (await UploadAsync(anonymous, key.Key, batch)).ShouldBe((HttpStatusCode.OK, 0, 2), "a resent batch adds nothing");
        (await UploadAsync(anonymous, key.Key + "x", batch)).Status.ShouldBe(HttpStatusCode.Unauthorized);

        var unit = (await admin.GetAsync<List<ColdStorageUnitDto>>($"{Base}/units?storeId={store}")).Single(u => u.Id == unitId);
        (unit.HasLogger, unit.LastCelsius).ShouldBe((true, (decimal?)4.7m));

        // A new key stops the old one.
        await admin.PostAsync<ColdChainLoggerKey>($"{Base}/units/{unitId}/logger-key");
        (await UploadAsync(anonymous, key.Key, batch)).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_twice_daily_check_not_taken_is_alerted_once()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var suffix = Letters(6);
        var store = (await admin.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "OPPH").Id;
        var unitId = await CreateUnitAsync(admin, store, $"M{suffix}", $"Freezer {suffix}", ColdUnitKind.Freezer);

        // The unit was set up two days ago and nobody has read it since.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ColdStorageUnit>().IgnoreQueryFilters()
                .Where(u => u.Id == unitId).ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, DateTimeOffset.UtcNow.AddDays(-2)), Ct);
        }

        await WatchAsync();
        var missed = (await MissedAsync(suffix)).ShouldHaveSingleItem("one alert, listing every check missed");
        missed.GetProperty("body").GetString().ShouldNotBeNull().ShouldContain("temperature check was not recorded");

        await WatchAsync();
        (await MissedAsync(suffix)).Count.ShouldBe(1, "a check is alerted once");
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<Guid> CreateUnitAsync(ApiClient api, Guid store, string code, string name, ColdUnitKind kind = ColdUnitKind.Refrigerator)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, $"{Base}/units", new SaveColdStorageUnitRequest(store, code, name, kind, null, null, true));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<Guid>(Ct);
    }

    private static async Task<(HttpStatusCode Status, int Accepted, int Duplicates)> UploadAsync(HttpClient http, string key, LoggerUploadRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}/logger/readings") { Content = JsonContent.Create(body) };
        request.Headers.Add("X-Device-Key", key);
        using var response = await http.SendAsync(request, Ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
            return (response.StatusCode, 0, 0);
        }

        var result = await response.Content.ReadFromJsonAsync<LoggerUploadResult>(Ct);
        return (response.StatusCode, result!.Accepted, result.Duplicates);
    }

    private async Task WatchAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ColdChainService>().AlertMissedAsync(Ct);
    }

    /// <summary>The cold-chain alerts queued for delivery (the test server delivers nothing) about the test's own unit.</summary>
    private async Task<List<JsonElement>> QueuedAsync(string suffix)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var queued = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == ColdChainService.AlertType)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(Ct);
        return queued.Select(m => JsonDocument.Parse(m.Payload).RootElement.Clone())
            .Where(a => a.GetProperty("title").GetString()!.Contains(suffix, StringComparison.Ordinal))
            .ToList();
    }

    private async Task<List<JsonElement>> AlertsAsync(Guid inCharge, string suffix) =>
        (await QueuedAsync(suffix)).Where(a => a.GetProperty("inChargeUserId").ValueKind == JsonValueKind.String
                                               && a.GetProperty("inChargeUserId").GetGuid() == inCharge
                                               && a.GetProperty("title").GetString()!.StartsWith("Temperature out of range", StringComparison.Ordinal)).ToList();

    private async Task<List<JsonElement>> MissedAsync(string suffix) =>
        (await QueuedAsync(suffix)).Where(a => a.GetProperty("title").GetString()!.StartsWith("Temperature not recorded", StringComparison.Ordinal)).ToList();

    private static async Task<Guid> InsulinAsync(ApiClient storeKeeper, string suffix) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: $"Insulin glargine 100 IU/ml pen {suffix}", GenericName: "Insulin glargine", BrandName: null, Type: ItemType.Drug,
            CategoryId: null, Strength: "100 IU/ml", DosageForm: "Injection", PackSize: "1 pen", BaseUnit: "PEN", PurchaseUnit: "PEN", PurchaseUnitFactor: 1,
            HsnCode: "300431", GstRate: 5m, Schedule: DrugSchedule.H, IsControlled: false, IsColdChain: true, VedClass: VedClass.Vital,
            ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
        .GetProperty("id").GetGuid();

    private async Task ReceiveAsync(ApiClient storeKeeper, Guid storeId, Guid itemId, string batch)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await GoodsReceipts.IntoStockAsync(server, storeKeeper, new CreateGoodsReceiptRequest(
            vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(itemId, null, batch, null, today.AddYears(1), 10m, 0m, 400m, 600m, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: true));
    }

    private static async Task<Guid> UserIdAsync(ApiClient user) => (await user.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
