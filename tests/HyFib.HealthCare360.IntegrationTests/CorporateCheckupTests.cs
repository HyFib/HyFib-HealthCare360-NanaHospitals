using System.Globalization;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Corporate health check-ups (MHC-05) over the real API and database: the front desk books one package for a company's
/// employees on one day. Every employee the package's sex and age rules allow is booked once, under the batch, with the
/// day's itinerary for his or her age and the package's tests ordered; every other one is reported with the reason and not
/// booked. Booking charges nothing: each package is charged when its employee checks in.
/// </summary>
public sealed class CorporateCheckupTests(HospitalServer server)
{
    private const string Bookings = "/api/v1/health-checkups/bookings";
    private const string Batches = "/api/v1/health-checkups/corporate-batches";

    [Fact]
    public async Task Books_every_eligible_employee_once_and_reports_each_of_the_others_with_the_reason()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        var (package, mammography) = await WomensPackageAsync(frontOffice);

        var priya = await RegisterAsync(Gender.Female, years: 30, at: day);
        var lakshmi = await RegisterAsync(Gender.Female, years: 45, at: day);
        var ravi = await RegisterAsync(Gender.Male, years: 35, at: day);
        var trainee = await RegisterAsync(Gender.Female, years: 16, at: day);
        var retiring = await RegisterAsync(Gender.Female, years: 60, at: day);
        var meena = await RegisterAsync(Gender.Female, years: 33, at: day);
        var unknown = Guid.CreateVersion7();

        // Meena has already booked the same check-up for that day herself.
        await frontOffice.PostAsync<BookingDto>(Bookings, new BookCheckupRequest(meena, package.Id, day, null, null));

        var result = await frontOffice.PostAsync<CorporateBatchResult>(Batches, new CorporateBatchRequest(
            "  Kaveri Software Services  ", "Anita Rao", "9840012345", "PO-" + Letters(6), package.Id, day,
            [priya, lakshmi, ravi, trainee, retiring, meena, unknown, priya], "Annual check-up"));

        // Priya, listed twice, is booked once; the others are reported, each with the reason.
        result.Booked.ShouldBe(2);
        result.BatchNumber.ShouldNotBeNullOrWhiteSpace();
        result.Skipped.ToDictionary(s => s.PatientId, s => s.Reason).ShouldBe(new Dictionary<Guid, string>
        {
            [ravi] = "This package is for female patients only.",
            [trainee] = "This package is for ages 18–55.",
            [retiring] = "This package is for ages 18–55.",
            [meena] = "This patient is already booked for this package on that date.",
            [unknown] = $"Patient '{unknown}' was not found.",
        }, ignoreOrder: true);

        var batch = await frontOffice.GetAsync<BatchDetail>($"{Batches}/{result.BatchId}");
        batch.Batch.CorporateName.ShouldBe("Kaveri Software Services");
        batch.Batch.ScheduledDate.ShouldBe(day);
        batch.Batch.BookedCount.ShouldBe(2);
        batch.Batch.SkippedCount.ShouldBe(5);
        batch.Bookings.Select(b => b.PatientId).ShouldBe([priya, lakshmi], ignoreOrder: true);
        batch.Bookings.ShouldAllBe(b => b.Status == BookingStatus.Booked && b.ScheduledDate == day && b.PackageId == package.Id
                                        && b.CorporateBatchId == result.BatchId && b.CorporateName == "Kaveri Software Services"
                                        && b.OrderId != null);

        // Each itinerary follows the package's item rules for the employee's age: mammography from 40.
        var stops = batch.Bookings.ToDictionary(b => b.PatientId, b => string.Join(" | ", b.Itinerary.Select(i => i.Description)));
        stops[lakshmi].ShouldContain(mammography);
        stops[priya].ShouldNotContain(mammography);

        // Nobody is charged until checking in, neither for the package nor for its tests.
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        foreach (var employee in new[] { priya, lakshmi })
        {
            (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?patientId={employee}&pageSize=200")).Items.ShouldBeEmpty();
        }
    }

    /// <summary>
    /// A women's check-up package of the run's own (women of 18 to 55; a blood count for everyone, mammography from 40 and
    /// a general medicine consultation), made from the seeded women's package's tests, with the mammography's name.
    /// </summary>
    private static async Task<(PackageDto Package, string Mammography)> WomensPackageAsync(ApiClient frontOffice)
    {
        var seeded = (await frontOffice.GetAsync<List<PackageDto>>("/api/v1/health-checkups/packages?search=HC-WOMEN")).Single(p => p.Code == "HC-WOMEN");
        var bloodCount = seeded.Items.Single(i => i.ServiceCode == "LAB-CBC");
        var mammography = seeded.Items.Single(i => i.ServiceCode == "RAD-MAMMO");
        var package = await frontOffice.PostAsync<PackageDto>("/api/v1/health-checkups/packages", new SavePackageRequest(
            "HC-IT-" + Letters(8).ToUpperInvariant(), "Working women's check-up", null, PackageGender.Female, MinAgeYears: 18, MaxAgeYears: 55,
            Price: 2500m, IsActive: true,
            [
                new PackageItemRequest(PackageItemKind.Investigation, bloodCount.ServiceItemId, null, PackageGender.Any, null),
                new PackageItemRequest(PackageItemKind.Investigation, mammography.ServiceItemId, null, PackageGender.Female, MinAgeYears: 40),
                new PackageItemRequest(PackageItemKind.Consultation, null, "GM", PackageGender.Any, null),
            ],
            Version: null));
        return (package, mammography.Name);
    }

    /// <summary>A new patient (an employee) who is <paramref name="years"/> old on <paramref name="at"/>.</summary>
    private async Task<Guid> RegisterAsync(Gender gender, int years, DateOnly at)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Employee", MiddleName: null, LastName: "Corporate" + Letters(8), Gender: gender,
            DateOfBirth: at.AddYears(-years).AddMonths(-6), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture),
            AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null,
            EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
            Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return patient.Id;
    }

    private static string Letters(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record BatchDetail(CorporateBatchDto Batch, List<BookingDto> Bookings);
}
