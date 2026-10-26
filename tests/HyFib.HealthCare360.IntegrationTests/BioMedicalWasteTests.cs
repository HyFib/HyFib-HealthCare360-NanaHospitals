using System.Net;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;
using HyFib.HealthCare360.Modules.SupportServices.Features.Waste;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Bio-medical waste over the real API: bags bar-coded and weighed on the ward, waiting in the storage area, handed over
/// to the treatment facility with the bags kept beyond 48 hours on record, and the month's register.
/// </summary>
public sealed class BioMedicalWasteTests(HospitalServer server)
{
    [Fact]
    public async Task Bags_weighed_on_the_ward_are_handed_over_with_late_ones_on_record_and_reach_the_register()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var now = DateTimeOffset.UtcNow;

        var yellow = await nurse.PostAsync<WasteBagDto>("/api/v1/bmw/bags",
            new RecordWasteBagRequest(WasteCategory.Yellow, null, "Ward 3 dressing room", 2.45m, null));
        var white = await nurse.PostAsync<WasteBagDto>("/api/v1/bmw/bags",
            new RecordWasteBagRequest(WasteCategory.White, null, "Ward 3 dressing room", 0.8m, now.AddHours(-50)));
        var wrong = await nurse.PostAsync<WasteBagDto>("/api/v1/bmw/bags",
            new RecordWasteBagRequest(WasteCategory.Red, null, "Ward 3 dressing room", 1.2m, null));
        yellow.Barcode.ShouldNotBeNullOrWhiteSpace();
        (white.Overdue, yellow.Overdue).ShouldBe((true, false));

        using (var tooHeavy = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/bmw/bags",
                   new RecordWasteBagRequest(WasteCategory.Red, null, "Ward 3", 80m, null)))
        {
            tooHeavy.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // The red bag was labelled by mistake: voided with the reason, it never leaves.
        (await nurse.PostAsync<WasteBagDto>($"/api/v1/bmw/bags/{wrong.Id}/void", new VoidWasteBagRequest("Labelled twice", wrong.Version)))
            .Status.ShouldBe(WasteBagStatus.Voided);

        var pending = await nurse.GetAsync<PendingWaste>("/api/v1/bmw/bags/pending");
        pending.Bags.ShouldContain(b => b.Id == yellow.Id);
        pending.Bags.ShouldNotContain(b => b.Id == wrong.Id);
        pending.Overdue.ShouldBeGreaterThanOrEqualTo(1);

        var request = new HandOverWasteRequest([yellow.Barcode, white.Barcode], "Ramky CBWTF, Kumbakonam", "tn 49 ax 2071", "S. Raman", "RC-88412");
        using (var notTheNurses = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/bmw/handovers", request))
        {
            notTheNurses.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var handover = await admin.PostAsync<WasteHandoverDto>("/api/v1/bmw/handovers", request);
        (handover.BagCount, handover.TotalKg, handover.LateBags, handover.VehicleNumber).ShouldBe((2, 3.25m, 1, "TN 49 AX 2071"));
        handover.ByCategory.Single(c => c.Category == WasteCategory.White).Kg.ShouldBe(0.8m);

        using (var twice = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/bmw/handovers", request))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var today = DateOnly.FromDateTime(now.AddHours(5.5).UtcDateTime);
        var month = await admin.GetAsync<WasteMonth>($"/api/v1/bmw/register?year={today.Year}&month={today.Month}");
        month.Totals.Single(c => c.Category == WasteCategory.Yellow).Kg.ShouldBeGreaterThanOrEqualTo(2.45m);
        month.LateBags.ShouldBeGreaterThanOrEqualTo(1);
        (await admin.GetAsync<WasteYear>($"/api/v1/bmw/annual?year={today.Year}")).TotalKg.ShouldBeGreaterThanOrEqualTo(3.25m);
    }
}
