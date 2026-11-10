using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A health check-up package's price is its billable service's, and changes only when two people agree (maker-checker),
/// over the real API and database: the front office, who keeps the check-up packages, asks; the accountant decides. A new
/// package is out of use, at nothing, until its price is approved, and is not booked meanwhile; a new price waits while
/// the package is booked and charged at the price in use; whichever screen sets the service's price (the package's, or
/// the services master), the package follows. Billing's own quote for the package's service is the witness.
/// </summary>
public sealed class CheckupPriceApprovalTests(HospitalServer server)
{
    private const string PackagesPath = "/api/v1/health-checkups/packages";
    private const string BookingsPath = "/api/v1/health-checkups/bookings";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_new_checkup_package_is_out_of_use_at_nothing_until_a_second_person_approves_its_price()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var created = await frontOffice.PostAsync<PackageDto>(PackagesPath, Package(1_800m) with { Reason = "Price agreed for the diabetes camp." });
        (created.IsActive, created.Price).ShouldBe((false, 0m), "out of use, at nothing, until its price is approved");
        var pending = created.PendingPrice.ShouldNotBeNull();
        (pending.Asked.BasePrice, pending.Asked.IsActive, pending.Asked.IsDiscountAllowed).ShouldBe((1_800m, true, true));
        var patient = await RegisterAsync(frontOffice);
        await BookingRefusedAsync(frontOffice, patient, created.Id);

        // The front office keeps the check-up packages but may not approve prices; the accountant decides.
        var request = await frontOffice.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{pending.ApprovalId}");
        (request.Kind, request.CanDecide).ShouldBe((ServicePrices.Kind, false));
        await ApproveAsAccountantAsync(pending.ApprovalId);

        var live = await frontOffice.GetAsync<PackageDto>($"{PackagesPath}/{created.Id}");
        (live.IsActive, live.Price, live.PendingPrice).ShouldBe((true, 1_800m, (PendingServicePrice?)null));
        (await frontOffice.PostAsync<BookingDto>(BookingsPath, new BookCheckupRequest(patient, created.Id, Today(), null, null))).PackageId.ShouldBe(created.Id);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        (await QuoteAsync(billing, created.ServiceItemId!.Value)).ShouldBe(1_800m, "billing charges the price the desk shows");
        (await AuditCountAsync("service.price_changed", created.ServiceItemId!.Value)).ShouldBe(1);
    }

    [Fact]
    public async Task A_checkup_price_waits_and_the_package_is_charged_at_the_price_in_use_until_it_is_approved()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var package = await server.SaveCheckupPackageAsync(frontOffice, Package(1_800m));
        var service = package.ServiceItemId!.Value;
        using (var answer = await frontOffice.SendRawAsync(HttpMethod.Put, $"{PackagesPath}/{package.Id}", Package(2_100m, package) with { Reason = "New laboratory rates." }))
        {
            answer.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await answer.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull().Summary.ShouldContain("₹1,800.00 → ₹2,100.00");
        }

        var waiting = await frontOffice.GetAsync<PackageDto>($"{PackagesPath}/{package.Id}");
        (waiting.Price, waiting.PendingPrice.ShouldNotBeNull().Asked.BasePrice).ShouldBe((1_800m, 2_100m));
        (await QuoteAsync(billing, service)).ShouldBe(1_800m, "the price in use until the change is approved");

        // While it waits, another price is refused; the same price with a new name saves the name and asks nothing twice.
        using (var other = await frontOffice.SendRawAsync(HttpMethod.Put, $"{PackagesPath}/{package.Id}", Package(2_400m, waiting)))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var renamed = await frontOffice.PutAsync<PackageDto>($"{PackagesPath}/{package.Id}", Package(2_100m, waiting) with { Name = waiting.Name + " 2027" });
        (renamed.Name, renamed.Price, renamed.PendingPrice.ShouldNotBeNull().ApprovalId).ShouldBe((waiting.Name + " 2027", 1_800m, waiting.PendingPrice.ApprovalId));

        await ApproveAsAccountantAsync(waiting.PendingPrice.ApprovalId);
        (await frontOffice.GetAsync<PackageDto>($"{PackagesPath}/{package.Id}")).Price.ShouldBe(2_100m);
        (await QuoteAsync(billing, service)).ShouldBe(2_100m);
    }

    [Fact]
    public async Task A_checkup_package_follows_its_services_price_set_in_the_services_master()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var package = await server.SaveCheckupPackageAsync(frontOffice, Package(1_800m));
        var servicePath = $"/api/v1/masters/services/{package.ServiceItemId}";

        // A new price asked in the services master and approved: the package shows the price billing charges.
        var asked = await admin.PutAsync<ApprovalRequestDto>(servicePath, Service(await admin.GetAsync<ServiceItemDto>(servicePath), 2_000m, isActive: true));
        await ApproveAsAccountantAsync(asked.Id);
        (await frontOffice.GetAsync<PackageDto>($"{PackagesPath}/{package.Id}")).Price.ShouldBe(2_000m);

        // Taken out of use there, at once: the package is out of use too, and is not booked.
        using (var stopped = await admin.SendRawAsync(HttpMethod.Put, servicePath, Service(await admin.GetAsync<ServiceItemDto>(servicePath), 2_000m, isActive: false)))
        {
            stopped.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await frontOffice.GetAsync<PackageDto>($"{PackagesPath}/{package.Id}")).IsActive.ShouldBeFalse();
        await BookingRefusedAsync(frontOffice, await RegisterAsync(frontOffice), package.Id);
    }

    [Fact]
    public async Task Taking_a_checkup_package_out_of_use_is_at_once_and_putting_it_back_waits()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var package = await server.SaveCheckupPackageAsync(frontOffice, Package(1_800m));
        var stopped = await frontOffice.PutAsync<PackageDto>($"{PackagesPath}/{package.Id}", Package(1_800m, package) with { IsActive = false });
        (stopped.IsActive, stopped.PendingPrice).ShouldBe((false, (PendingServicePrice?)null));
        await BookingRefusedAsync(frontOffice, await RegisterAsync(frontOffice), package.Id);

        using (var back = await frontOffice.SendRawAsync(HttpMethod.Put, $"{PackagesPath}/{package.Id}", Package(1_800m, stopped)))
        {
            back.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        (await frontOffice.GetAsync<PackageDto>($"{PackagesPath}/{package.Id}")).IsActive.ShouldBeFalse("putting it back in use waits for a second person");
    }

    // ---------------------------------------------------------------------- helpers

    /// <summary>A check-up for anyone, with a general medicine consultation.</summary>
    private static SavePackageRequest Package(decimal price, PackageDto? package = null) => new(
        package?.Code ?? "HC-PR" + Letters(8).ToUpperInvariant(), package?.Name ?? "Diabetes screen", null, PackageGender.Any, MinAgeYears: null, MaxAgeYears: null,
        Price: price, IsActive: true, [new PackageItemRequest(PackageItemKind.Consultation, null, "GM", PackageGender.Any, null)], package?.Version);

    /// <summary>The service saved again from the services master, with this price and use.</summary>
    private static SaveServiceItemRequest Service(ServiceItemDto service, decimal price, bool isActive) => new(
        service.Code, service.Name, service.ShortName, service.Category, service.DepartmentId, price, service.TaxRate, service.SacCode,
        service.IsDiscountAllowed, service.IsPriceEditable, service.Description, isActive, service.Version, service.LocalNames);

    private async Task BookingRefusedAsync(ApiClient frontOffice, Guid patient, Guid packageId)
    {
        using var response = await frontOffice.SendRawAsync(HttpMethod.Post, BookingsPath, new BookCheckupRequest(patient, packageId, Today(), null, null));
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("package.not_in_use");
    }

    /// <summary>What billing quotes for the service for a self-paying patient, today.</summary>
    private static async Task<decimal> QuoteAsync(ApiClient client, Guid serviceId) =>
        (await client.GetAsync<Quote>($"/api/v1/billing/tariffs/quote?serviceItemId={serviceId}")).UnitPrice;

    private async Task ApproveAsAccountantAsync(Guid approvalId)
    {
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var request = await accountant.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        request.CanDecide.ShouldBeTrue();
        (await accountant.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Agreed.", request.Version)))
            .Status.ShouldBe(ApprovalStatus.Approved);
    }

    private async Task<int> AuditCountAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = entityId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().CountAsync(e => e.Action == action && e.EntityId == id, Ct);
    }

    private static async Task<Guid> RegisterAsync(ApiClient frontOffice) =>
        (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arun"), MiddleName: null, LastName: "Checkup" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;

    private DateOnly Today() => server.Services.GetRequiredService<TimeProvider>().Today();

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Quote(decimal UnitPrice);
}
