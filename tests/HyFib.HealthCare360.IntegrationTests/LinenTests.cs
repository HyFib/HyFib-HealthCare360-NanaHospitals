using System.Net;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Linen;
using HyFib.HealthCare360.Modules.SupportServices.Features.Linen;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Linen over the real API: received into the store, issued to a ward against its par level, sent back soiled by the
/// ward (infected linen apart), washed back to the store, and condemned, with every count checked against what is there.
/// </summary>
public sealed class LinenTests(HospitalServer server)
{
    [Fact]
    public async Task Linen_is_counted_from_the_store_to_the_ward_to_the_laundry_and_back_with_infected_linen_disinfected()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var code = $"SHEET{Random.Shared.Next(10000, 99999)}";

        var items = await admin.PostAsync<List<LinenItemDto>>("/api/v1/linen/items", new SaveLinenItemRequest(null, code, $"Test sheet {code}", 150, true, null));
        var sheet = items.Single(i => i.Code == code);
        var ward = (await nurse.GetAsync<List<LinenWard>>("/api/v1/linen/wards")).First();

        RecordLinenRequest Count(LinenMovementKind kind, int quantity, Guid? wardId = null, bool infected = false) =>
            new(kind, wardId, infected, null, null, null, null, null, null, [new LinenLineInput(sheet.Id, quantity)]);

        // The nurse cannot issue linen from the store; the store keeper does.
        using (var notTheNurse = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/linen/movements", Count(LinenMovementKind.Received, 100)))
        {
            notTheNurse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        await admin.PostAsync<LinenMovementDto>("/api/v1/linen/movements", Count(LinenMovementKind.Received, 100));
        (await BirthsAndDeathsTests.RefusalAsync(admin, HttpMethod.Post, "/api/v1/linen/movements", Count(LinenMovementKind.Issued, 120, ward.Id)))
            .ShouldBe("linen.short");
        await admin.PostAsync<LinenStock>("/api/v1/linen/par", new SetParRequest(ward.Id, [new LinenLineInput(sheet.Id, 40)]));
        await admin.PostAsync<LinenMovementDto>("/api/v1/linen/movements", Count(LinenMovementKind.Issued, 30, ward.Id));

        var stock = await nurse.GetAsync<LinenStock>("/api/v1/linen/stock");
        stock.Items.Single(i => i.LinenItemId == sheet.Id).ShouldSatisfyAllConditions(i => i.Store.ShouldBe(70), i => i.Wards.ShouldBe(30));
        stock.Wards.Single(w => w.WardId == ward.Id).Lines.Single(l => l.LinenItemId == sheet.Id)
            .ShouldSatisfyAllConditions(l => l.Held.ShouldBe(30), l => l.Par.ShouldBe(40), l => l.Short.ShouldBe(10));

        // The ward sends 12 soiled, 4 of them infected, and reports 1 lost.
        await nurse.PostAsync<LinenMovementDto>("/api/v1/linen/movements", Count(LinenMovementKind.SoiledReturn, 8, ward.Id));
        await nurse.PostAsync<LinenMovementDto>("/api/v1/linen/movements", Count(LinenMovementKind.SoiledReturn, 4, ward.Id, infected: true));
        await nurse.PostAsync<LinenMovementDto>("/api/v1/linen/movements", Count(LinenMovementKind.Lost, 1, ward.Id));

        // Infected linen is not washed in a normal wash, nor at too low a temperature.
        using (var normal = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/linen/movements",
                   Count(LinenMovementKind.Washed, 4, infected: true) with { Process = WashProcess.Normal }))
        {
            normal.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await BirthsAndDeathsTests.RefusalAsync(admin, HttpMethod.Post, "/api/v1/linen/movements",
            Count(LinenMovementKind.Washed, 4, infected: true) with { Process = WashProcess.ThermalDisinfection, TemperatureC = 60, HoldMinutes = 20 }))
            .ShouldBe("linen.not_disinfected");
        var batch = await admin.PostAsync<LinenMovementDto>("/api/v1/linen/movements",
            Count(LinenMovementKind.Washed, 4, infected: true) with { Process = WashProcess.ThermalDisinfection, TemperatureC = 72, HoldMinutes = 4 });
        (batch.Infected, batch.Pieces).ShouldBe((true, 4));
        await admin.PostAsync<LinenMovementDto>("/api/v1/linen/movements", Count(LinenMovementKind.Washed, 6) with { Process = WashProcess.Normal });
        await admin.PostAsync<LinenMovementDto>("/api/v1/linen/movements",
            Count(LinenMovementKind.Condemned, 2) with { CondemnReason = CondemnReason.Torn, From = LinenLocation.Laundry });

        var after = (await admin.GetAsync<LinenStock>("/api/v1/linen/stock")).Items.Single(i => i.LinenItemId == sheet.Id);
        (after.Store, after.Wards, after.Soiled, after.Infected, after.InCirculation).ShouldBe((80, 17, 0, 0, 97));
        (await admin.GetAsync<List<LinenItemDto>>("/api/v1/linen/items")).Single(i => i.Id == sheet.Id).AverageWashes.ShouldBe(0.1);
        (await nurse.GetAsync<List<LinenMovementDto>>("/api/v1/linen/movements")).ShouldContain(m => m.Id == batch.Id);
    }
}
