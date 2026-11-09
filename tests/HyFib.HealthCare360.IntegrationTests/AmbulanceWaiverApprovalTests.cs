using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.SupportServices.Domain.Ambulance;
using HyFib.HealthCare360.Modules.SupportServices.Features.Ambulance;
using HyFib.HealthCare360.Modules.SupportServices.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Waiving an ambulance trip's charge waits for a second person (maker-checker, slice 15), over the real API: a completed
/// trip is charged at once; a waiver is asked for (202) and the charge stands until someone who approves waivers approves
/// it, never the one who asked; then the charges come off the bill. A hospital lets the desk waive alone only by switching
/// <c>ambulance.waiverApproval</c> off, which a second administrator approves; the waiver is then made at once, audited.
/// </summary>
public sealed class AmbulanceWaiverApprovalTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_waiver_waits_for_a_second_person_while_the_charge_stands_then_the_charges_come_off_the_bill()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (trip, patientId) = await CompletedTripAsync(frontOffice, 24);
        (await ChargesAsync(billing, patientId)).ShouldAllBe(c => c.Status == ChargeStatus.Unbilled);

        using var response = await frontOffice.SendRawAsync(HttpMethod.Post, Waive(trip.Id), new WaiveTripChargeRequest("Free transport under the state scheme", trip.Version));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
        var asked = await ApiClient.ReadAsync<ApprovalRequestDto>(response, HttpMethod.Post, Waive(trip.Id));
        asked.Kind.ShouldBe("ambulance.charge_waiver");
        asked.Details.Single(d => d.Label == "Charge withdrawn").Value.ShouldContain("base fare and kilometres");

        // The charge stands while the waiver waits, and the trip says so.
        (await ChargesAsync(billing, patientId)).ShouldAllBe(c => c.Status == ChargeStatus.Unbilled);
        var waiting = (await frontOffice.GetAsync<TripLog>("/api/v1/ambulance/trips/log")).Trips.Single(t => t.Id == trip.Id);
        waiting.ShouldSatisfyAllConditions(
            t => t.ChargeWaivedReason.ShouldBeNull(),
            t => t.WaiverPending.ShouldNotBeNull().AskedByYou.ShouldBeTrue());

        // The desk does not approve waivers; the accountant does.
        (await RefusalAsync(frontOffice, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        var approved = await MakerChecker.ApproveAsync(accountant, asked);
        approved.Outcome.ShouldNotBeNull().ShouldContain("waived");

        (await ChargesAsync(billing, patientId)).ShouldAllBe(c => c.Status == ChargeStatus.Cancelled);
        var waived = (await frontOffice.GetAsync<TripLog>("/api/v1/ambulance/trips/log")).Trips.Single(t => t.Id == trip.Id);
        waived.ShouldSatisfyAllConditions(
            t => t.ChargeWaivedReason.ShouldBe("Free transport under the state scheme"),
            t => t.ChargeWaivedByName.ShouldBe(approved.DecidedByName),
            t => t.WaiverRequestedByName.ShouldBe(asked.RequestedByName),
            t => t.WaiverPending.ShouldBeNull());
    }

    [Fact]
    public async Task A_waiver_given_at_completion_is_charged_and_asked_for_and_the_one_who_asked_never_approves_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (trip, patientId) = await CompletedTripAsync(admin, 8, waiveReason: "Staff member's mother");
        trip.ShouldSatisfyAllConditions(
            t => t.Status.ShouldBe(TripStatus.Completed),
            t => t.ChargeWaivedReason.ShouldBeNull(),
            t => t.WaiverPending.ShouldNotBeNull().Reason.ShouldBe("Staff member's mother"));
        (await ChargesAsync(billing, patientId)).ShouldHaveSingleItem().Status.ShouldBe(ChargeStatus.Unbilled);

        // The administrator, who may approve waivers, asked for this one.
        var request = await admin.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{trip.WaiverPending!.ApprovalId}");
        (await RefusalAsync(admin, $"/api/v1/approvals/{request.Id}/approve", new ApproveRequest(null, request.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await ChargesAsync(billing, patientId)).ShouldHaveSingleItem().Status.ShouldBe(ChargeStatus.Unbilled);
    }

    [Fact]
    public async Task With_the_second_person_switched_off_by_two_administrators_the_desk_waives_at_once_audited()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId ?? throw new InvalidOperationException("No facility.");
        await server.SaveSettingAsync(admin, AmbulanceService.WaiverApprovalSetting, new SaveSettingRequest(JsonValue.Create(false), facilityId, false, null));
        try
        {
            var (trip, patientId) = await CompletedTripAsync(frontOffice, 12);
            var waived = await frontOffice.PostAsync<AmbulanceTripDto>(Waive(trip.Id), new WaiveTripChargeRequest("Brought in dead, family in distress", trip.Version));
            waived.ShouldSatisfyAllConditions(
                t => t.ChargeWaivedReason.ShouldBe("Brought in dead, family in distress"),
                t => t.ChargeWaivedByName.ShouldNotBeNull(),
                t => t.WaiverRequestedByName.ShouldBeNull());
            (await ChargesAsync(billing, patientId)).ShouldAllBe(c => c.Status == ChargeStatus.Cancelled);
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(AmbulanceService.WaiverApprovalSetting, facilityId, CancellationToken.None);
        }
    }

    private static string Waive(Guid id) => $"/api/v1/ambulance/trips/{id}/waive";

    private static async Task<List<ChargeDto>> ChargesAsync(ApiClient billing, Guid patientId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?patientId={patientId}&pageSize=50")).Items
            .Where(c => c.SourceModule == AmbulanceCharges.Source).ToList();

    /// <summary>A trip of <paramref name="km"/> kilometres for a newly registered patient, on a new vehicle, completed.</summary>
    private async Task<(AmbulanceTripDto Trip, Guid PatientId)> CompletedTripAsync(ApiClient desk, int km, string? waiveReason = null)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        var plate = $"TN 50 W{Random.Shared.Next(1000, 9999)}";
        var fleet = await admin.PostAsync<List<AmbulanceVehicleDto>>("/api/v1/ambulance/vehicles",
            new SaveVehicleRequest(null, plate, AmbulanceKind.PatientTransport, today.AddMonths(6), today.AddMonths(6), today.AddMonths(6), 10_000, true, null));
        var vehicle = fleet.Single(v => v.RegistrationNumber == plate);
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Female, new DateOnly(1960, 5, 5));
        var trip = await desk.PostAsync<AmbulanceTripDto>("/api/v1/ambulance/trips", new RequestTripRequest(
            TripPurpose.ScheduledTransport, patient.Id, null, "Daughter", "9840055555", "Ward 2", "Home, Papanasam", null));
        trip = await desk.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{trip.Id}/dispatch", new DispatchRequest(vehicle.Id, "Murugan", null, trip.Version));
        trip = await desk.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{trip.Id}/at-scene", new TripStepRequest(trip.Version));
        trip = await desk.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{trip.Id}/left-scene", new TripStepRequest(trip.Version));
        trip = await desk.PostAsync<AmbulanceTripDto>($"/api/v1/ambulance/trips/{trip.Id}/complete", new CompleteTripRequest(10_000 + km, waiveReason, trip.Version));
        return (trip, patient.Id);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return (response.StatusCode, problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }
}
