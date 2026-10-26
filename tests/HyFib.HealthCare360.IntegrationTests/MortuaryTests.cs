using System.Net;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Mortuary;
using HyFib.HealthCare360.Modules.SupportServices.Features.Mortuary;
using HyFib.HealthCare360.Platform.Documents;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The mortuary over the real API: slots set by the administrator, a patient's body received only once the death is on
/// record, one body to a slot, and a medico-legal body released to the family only on the police's clearance, with the slip.
/// </summary>
public sealed class MortuaryTests(HospitalServer server)
{
    [Fact]
    public async Task A_body_is_received_once_the_death_is_on_record_and_released_on_the_authority_its_case_needs()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var request = new SaveSlotsRequest([new SlotInput(null, $"A{tag}", "Chamber A", true, null), new SlotInput(null, $"B{tag}", "Chamber A", true, null)]);
        using (var notTheNurses = await nurse.SendRawAsync(HttpMethod.Put, "/api/v1/mortuary/slots", request))
        {
            notTheNurses.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var slots = await admin.PutAsync<List<MortuarySlotDto>>("/api/v1/mortuary/slots", request);
        var a = slots.Single(s => s.Code == $"A{tag}");
        var b = slots.Single(s => s.Code == $"B{tag}");

        // A patient still alive on record does not go in.
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1960, 3, 1));
        var receive = new ReceiveBodyRequest(BodySource.InHospitalDeath, patient.Id, null, true, null, null, null, "Ward 4 attender", false, null, null,
            null, "Gold chain, watch", a.Id);
        (await BirthsAndDeathsTests.RefusalAsync(nurse, HttpMethod.Post, "/api/v1/mortuary/bodies", receive)).ShouldBe("mortuary.death_not_recorded");

        await doctor.PostAsync<object>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-2)));
        var body = await nurse.PostAsync<BodyDto>("/api/v1/mortuary/bodies", receive);
        (body.Uhid, body.SlotCode, body.Status, body.Name).ShouldBe((patient.Uhid, $"A{tag}", BodyStatus.InStorage, body.Name));
        body.Number.ShouldNotBeNullOrWhiteSpace();
        var held = (await nurse.GetAsync<List<MortuarySlotDto>>("/api/v1/mortuary/slots")).Single(s => s.Id == a.Id);
        held.BodyId.ShouldBe(body.Id);
        (await BirthsAndDeathsTests.RefusalAsync(admin, HttpMethod.Put, "/api/v1/mortuary/slots",
            new SaveSlotsRequest([new SlotInput(a.Id, a.Code, a.Chamber, false, held.Version)]))).ShouldBe("mortuary.slot_occupied");

        // One body to a slot.
        var stranger = new ReceiveBodyRequest(BodySource.Police, null, "Unknown male, about 40", false, "Male", 40, null, "HC 1123, B2 police station",
            true, "MLC 77/2026", "B2 Ashok Nagar", "Scar on left forearm", null, a.Id);
        using (var occupied = await nurse.SendRawAsync(HttpMethod.Post, "/api/v1/mortuary/bodies", stranger))
        {
            occupied.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var unknown = await nurse.PostAsync<BodyDto>("/api/v1/mortuary/bodies", stranger with { SlotId = b.Id });
        (unknown.IsMlc, unknown.Identified).ShouldBe((true, false));

        // An unidentified medico-legal body goes neither to "relatives" nor for donation.
        (await BirthsAndDeathsTests.RefusalAsync(nurse, HttpMethod.Post, $"/api/v1/mortuary/bodies/{unknown.Id}/release",
            new ReleaseBodyRequest(ReleaseTo.BodyDonation, "Anatomy department", null, null, null, false, null, unknown.Version))).ShouldBe("mortuary.mlc_donation");

        using (var noSlipYet = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/mortuary/bodies/{body.Id}/slip"))
        {
            noSlipYet.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var released = await nurse.PostAsync<BodyDto>($"/api/v1/mortuary/bodies/{body.Id}/release",
            new ReleaseBodyRequest(ReleaseTo.Relatives, "Suresh Kumar", "Son", "Aadhaar XXXX-4410", null, true, null, body.Version));
        (released.Status, released.ReleasedTo, released.SlotCode).ShouldBe((BodyStatus.Released, ReleaseTo.Relatives, $"A{tag}"));
        (await nurse.GetAsync<List<MortuarySlotDto>>("/api/v1/mortuary/slots")).Single(s => s.Id == a.Id).BodyId.ShouldBeNull();

        var slip = await nurse.GetAsync<PrintableDocumentDto>($"/api/v1/mortuary/bodies/{body.Id}/slip");
        slip.Html.ShouldContain("Suresh Kumar (Son)");
        slip.Html.ShouldContain("Gold chain, watch");

        var inStorage = await nurse.GetAsync<List<BodyDto>>("/api/v1/mortuary/bodies?status=InStorage");
        inStorage.ShouldContain(x => x.Id == unknown.Id);
        inStorage.ShouldNotContain(x => x.Id == body.Id);
        (await nurse.GetAsync<List<BodyDto>>("/api/v1/mortuary/bodies")).ShouldContain(x => x.Id == body.Id);

        using (var notFrontOffice = await frontOffice.SendRawAsync(HttpMethod.Get, "/api/v1/mortuary/bodies"))
        {
            notFrontOffice.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }
}
