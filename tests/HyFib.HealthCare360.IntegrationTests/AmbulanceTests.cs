using System.Net;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Ambulance;
using HyFib.HealthCare360.Modules.SupportServices.Features.Ambulance;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The ambulance desk over the real API: the fleet with its certificates, a call taken and a suitable vehicle sent, each
/// step of the trip, and the kilometres charged to the patient's bill beyond what the base fare covers.
/// </summary>
public sealed class AmbulanceTests(HospitalServer server)
{
    [Fact]
    public async Task A_call_is_answered_by_a_vehicle_with_its_papers_in_force_and_the_trip_is_charged_by_the_kilometre()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        var plate = $"TN 49 Z{Random.Shared.Next(1000, 9999)}";

        var vehicle = new SaveVehicleRequest(null, plate, AmbulanceKind.Bls, today.AddMonths(6), today.AddMonths(6), today.AddDays(-1), 45_200, true, null);
        using (var notFrontOffice = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/ambulance/vehicles", vehicle))
        {
            notFrontOffice.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var fleet = await admin.PostAsync<List<AmbulanceVehicleDto>>("/api/v1/ambulance/vehicles", vehicle);
        var bls = fleet.Single(v => v.RegistrationNumber == plate);
        bls.Lapsed.ShouldBe(["pollution certificate"]);

        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1970, 1, 1));
        var trip = await frontOffice.PostAsync<AmbulanceTripDto>("/api/v1/ambulance/trips", new RequestTripRequest(
            TripPurpose.EmergencyPickup, patient.Id, null, "Lakshmi (wife)", "9840012345", "12 Gandhi Road, Kumbakonam", null, "Chest pain since an hour"));
        (trip.Status, trip.Uhid).ShouldBe((TripStatus.Requested, patient.Uhid));
        (await frontOffice.GetAsync<List<AmbulanceTripDto>>("/api/v1/ambulance/trips")).ShouldContain(t => t.Id == trip.Id);

        // The pollution certificate lapsed yesterday: the vehicle stays in until it is renewed.
        (await BirthsAndDeathsTests.RefusalAsync(frontOffice, HttpMethod.Post, $"/api/v1/ambulance/trips/{trip.Id}/dispatch",
            new DispatchRequest(bls.Id, "Raju", "EMT Priya", trip.Version))).ShouldBe("ambulance.papers_lapsed");
        fleet = await admin.PostAsync<List<AmbulanceVehicleDto>>("/api/v1/ambulance/vehicles",
            vehicle with { Id = bls.Id, PollutionValidUntil = today.AddMonths(6), Version = bls.Version });
        fleet.Single(v => v.Id == bls.Id).Lapsed.ShouldBeEmpty();

        trip = await frontOffice.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{trip.Id}/dispatch",
            new DispatchRequest(bls.Id, "Raju", "EMT Priya", trip.Version));
        (trip.Status, trip.VehicleNumber, trip.StartOdometerKm).ShouldBe((TripStatus.Dispatched, plate, 45_200));

        // One trip at a time per vehicle.
        var second = await frontOffice.PostAsync<AmbulanceTripDto>("/api/v1/ambulance/trips", new RequestTripRequest(
            TripPurpose.EmergencyPickup, null, "Unknown, road accident", "Passer-by", "9840099999", "NH 36 near Thiruvidaimaruthur", null, null));
        using (var busy = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/ambulance/trips/{second.Id}/dispatch",
                   new DispatchRequest(bls.Id, "Raju", "EMT Priya", second.Version)))
        {
            busy.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await frontOffice.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{second.Id}/cancel", new CancelTripRequest("Taken by the 108 ambulance", second.Version)))
            .Status.ShouldBe(TripStatus.Cancelled);

        trip = await frontOffice.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{trip.Id}/at-scene", new TripStepRequest(trip.Version));
        trip.ResponseMinutes.ShouldNotBeNull();
        trip = await frontOffice.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{trip.Id}/left-scene", new TripStepRequest(trip.Version));
        trip = await frontOffice.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{trip.Id}/complete", new CompleteTripRequest(45_224, null, trip.Version));
        (trip.Status, trip.Kilometres).ShouldBe((TripStatus.Completed, 24));
        (await admin.GetAsync<List<AmbulanceVehicleDto>>("/api/v1/ambulance/vehicles")).Single(v => v.Id == bls.Id)
            .ShouldSatisfyAllConditions(v => v.OdometerKm.ShouldBe(45_224), v => v.OnTripId.ShouldBeNull());

        // The base fare covers the first 10 km; the other 14 are charged by the kilometre.
        var charges = (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?patientId={patient.Id}&pageSize=50")).Items
            .Where(c => c.SourceModule == AmbulanceCharges.Source).ToList();
        charges.Select(c => (c.ServiceCode, c.Quantity)).OrderBy(c => c.ServiceCode).ShouldBe([("AMB-BLS", 1m), ("AMB-KM", 14m)]);

        var log = await frontOffice.GetAsync<TripLog>($"/api/v1/ambulance/trips/log?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        log.Trips.ShouldContain(t => t.Id == trip.Id);
        log.Trips.ShouldContain(t => t.Id == second.Id && t.Status == TripStatus.Cancelled);
        log.MedianResponseMinutes.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_body_goes_only_in_the_hearse()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Female, new DateOnly(1980, 1, 1));
        using var alive = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/ambulance/trips", new RequestTripRequest(
            TripPurpose.BodyTransport, patient.Id, null, "Son", "9840011111", "Mortuary", "Home, Swamimalai", null));
        alive.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
