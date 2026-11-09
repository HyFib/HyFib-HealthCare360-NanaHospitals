using System.Net;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Waste;
using HyFib.HealthCare360.Modules.SupportServices.Features.Waste;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Settings;
using Microsoft.Extensions.DependencyInjection;

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

        // Each bag is weighed again at the handover, before the CBWTF's driver: the yellow bag weighs half a kilo more than
        // where it was filled (opened and added to), and is flagged.
        var request = new HandOverWasteRequest([new HandoverBag(yellow.Barcode, 2.95m), new HandoverBag(white.Barcode, 0.8m)], "Ramky CBWTF, Kumbakonam",
            "tn 49 ax 2071", "S. Raman", "RC-88412");
        using (var notTheNurses = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/bmw/handovers", request))
        {
            notTheNurses.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var handover = await admin.PostAsync<WasteHandoverDto>("/api/v1/bmw/handovers", request);
        (handover.BagCount, handover.TotalKg, handover.LateBags, handover.MismatchedBags, handover.VehicleNumber).ShouldBe((2, 3.75m, 1, 1, "TN 49 AX 2071"));
        handover.ByCategory.Single(c => c.Category == WasteCategory.White).Kg.ShouldBe(0.8m);
        handover.Bags.Single(b => b.Id == yellow.Id).ShouldSatisfyAllConditions(
            b => (b.WeightKg, b.HandoverWeightKg, b.WeightMismatch).ShouldBe((2.45m, 2.95m, true)));
        handover.Bags.Single(b => b.Id == white.Id).WeightMismatch.ShouldBeFalse();

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

    [Fact]
    public async Task A_handover_weighs_every_bag_once()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var bag = await nurse.PostAsync<WasteBagDto>("/api/v1/bmw/bags", new RecordWasteBagRequest(WasteCategory.Red, null, "ICU", 1.1m, null));

        using (var unweighed = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/bmw/handovers",
                   new HandOverWasteRequest([new HandoverBag(bag.Barcode, 0m)], "Ramky CBWTF", "TN 49 AX 2071", "S. Raman", null)))
        {
            unweighed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var twice = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/bmw/handovers",
            new HandOverWasteRequest([new HandoverBag(bag.Barcode, 1.1m), new HandoverBag(bag.Barcode.ToLowerInvariant(), 1.1m)], "Ramky CBWTF", "TN 49 AX 2071", "S. Raman", null));
        twice.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_bags_label_carries_the_colour_the_ward_the_weight_the_date_and_the_hospitals_identifier()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("No facility.");
        var bag = await nurse.PostAsync<WasteBagDto>("/api/v1/bmw/bags", new RecordWasteBagRequest(WasteCategory.White, null, "Ward 5 treatment room", 0.65m, null));

        // No label goes out without the hospital's identifier.
        await RemoveSettingAsync(WasteService.HcfIdSetting, facilityId);
        await RemoveSettingAsync(WasteService.AuthorisationSetting, facilityId);
        using (var unlabelled = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/bmw/bags/{bag.Id}/label"))
        {
            unlabelled.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        await server.SaveSettingAsync(admin, WasteService.HcfIdSetting, new SaveSettingRequest(JsonValue.Create("TN-HCF-04417"), facilityId, false, null));
        try
        {
            var label = await nurse.GetAsync<WasteBagLabel>($"/api/v1/bmw/bags/{bag.Id}/label");
            label.ShouldSatisfyAllConditions(
                l => l.Barcode.ShouldBe(bag.Barcode),
                l => l.Colour.ShouldBe("White translucent puncture-proof container"),
                l => l.Symbol.ShouldBe("Biohazard"),
                l => l.Source.ShouldBe("Ward 5 treatment room"),
                l => l.WeightKg.ShouldBe(0.65m),
                l => l.CollectedOn.ShouldBe(DateOnly.FromDateTime(bag.CollectedAt.ToOffset(TimeSpan.FromHours(5.5)).DateTime)),
                l => l.HcfId.ShouldBe("TN-HCF-04417"));
        }
        finally
        {
            await RemoveSettingAsync(WasteService.HcfIdSetting, facilityId);
        }
    }

    [Fact]
    public async Task An_accident_is_reported_on_Form_I_and_counted_in_the_annual_report_Form_IV()
    {
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var year = DateTimeOffset.UtcNow.AddHours(5.5).Year;
        var accident = await nurse.PostAsync<WasteAccidentDto>("/api/v1/bmw/accidents", new RecordWasteAccidentRequest(
            DateTimeOffset.UtcNow.AddHours(-2), WasteAccidentKind.NeedleStick, "Ward 3 dressing room",
            "Recapping a used needle after an injection, the needle slipped into the nurse's finger", WasteCategory.White, "A used 24G needle", 1,
            "Possible exposure to blood-borne infection; source patient tested", "Wound washed under running water; reported to the infection control nurse",
            "Post-exposure prophylaxis started within two hours", "Recapping stopped; sharps containers at every bedside", null, "Staff nurse"));
        accident.ReportedByName.ShouldNotBeNullOrWhiteSpace();

        // The nurse records it; the waste officer files Form I.
        using (var notTheNurses = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/bmw/accidents/{accident.Id}/form-i"))
        {
            notTheNurses.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var form = await admin.GetAsync<FormI>($"/api/v1/bmw/accidents/{accident.Id}/form-i");
        form.Lines.Single(l => l.Number == "2").Answer.ShouldBe("Needle-stick injury");
        form.Lines.Single(l => l.Number == "4").Answer.ShouldBe("No");
        form.Lines.Single(l => l.Number == "6").Answer.ShouldContain("Persons affected: 1.");
        form.Lines.Single(l => l.Number == "9").Answer.ShouldBe("Recapping stopped; sharps containers at every bedside");
        form.Lines.Single(l => l.Number == "10").Answer.ShouldBe(WasteForms.ToFill);

        var formIv = await admin.GetAsync<FormIv>($"/api/v1/bmw/annual/form-iv?year={year}");
        formIv.Beds.ShouldBeGreaterThan(0);
        formIv.Lines.Single(l => l.Number == "7").Answer.ShouldContain("persons affected");
        formIv.Lines.ShouldContain(l => l.Item.StartsWith("Needle-stick injury on ", StringComparison.Ordinal));
        formIv.Lines.Single(l => l.Number == "6").Answer.ShouldBe(WasteForms.ToFill);
    }

    private async Task RemoveSettingAsync(string key, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(key, facilityId, CancellationToken.None);
    }
}
