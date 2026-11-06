using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The FHIR API's <c>_sort</c> (gap #21) on the real server and PostgreSQL: a provider orders the patients it may read by
/// the values they are served with, not as they are stored, and pages through them in that order by the bundle's links.
/// </summary>
public sealed class FhirSortTests(HospitalServer server)
{
    [Fact]
    public async Task A_provider_orders_patients_by_the_values_they_are_served_with_and_pages_in_that_order()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (partner, _, _) = await PartnerAsync(server);
        var family = "Sortq" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))));

        // Registered from a stated age, the birth date is served as its year only, which then covers the whole year. The
        // others were born that year, in June and in October.
        var estimated = await RegisterAsync(frontOffice, "Chitra", family, Gender.Female, null, ageYears: 46);
        var year = int.Parse((await FhirAsync(partner, $"Patient/{estimated}", "TREAT")).Body.GetProperty("birthDate").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        var june = await RegisterAsync(frontOffice, "anand", family, Gender.Female, new DateOnly(year, 6, 15), null);
        var october = await RegisterAsync(frontOffice, "Banu", family + "z", Gender.Male, new DateOnly(year, 10, 1), null);

        async Task<List<string>> Sorted(string sort) =>
            Matches((await FhirAsync(partner, $"Patient?name={family}&_sort={sort}", "TREAT")).Body);

        // By family name, then given names, whatever their case. Banu's family name comes after the others', so by given
        // name first Banu would be second; and "anand" comes before "Chitra" only lower-cased.
        (await Sorted("name")).ShouldBe([june, estimated, october]);
        (await Sorted("-name")).ShouldBe([october, estimated, june]);

        // The year starts before June and ends after October: first either way. By the stored day it would not be.
        (await Sorted("birthdate")).ShouldBe([estimated, june, october]);
        (await Sorted("-birthdate")).ShouldBe([estimated, october, june]);
        (await Sorted("gender,-name")).ShouldBe([estimated, june, october]);

        // Changed within the same millisecond, the three are served with the same last update. They tie, and fall back to
        // the newest registration first, not to the microseconds PostgreSQL keeps apart.
        var changed = DateTimeOffset.UtcNow.AddDays(1);
        changed = changed.AddTicks(-(changed.Ticks % TimeSpan.TicksPerMillisecond));
        await SetUpdatedAtAsync(estimated, changed.AddTicks(9_000));
        await SetUpdatedAtAsync(june, changed.AddTicks(5_000));
        await SetUpdatedAtAsync(october, changed.AddTicks(1_000));
        (await Sorted("-_lastUpdated")).ShouldBe([october, june, estimated]);

        // One at a time, by the links: the same order, and every link keeps the order asked for.
        var paged = new List<string>();
        var path = $"Patient?name={family}&_sort=-birthdate&_count=1";
        while (path is not null)
        {
            var bundle = (await FhirAsync(partner, path, "TREAT")).Body;
            paged.AddRange(Matches(bundle));
            var next = bundle.GetProperty("link").EnumerateArray().FirstOrDefault(l => l.GetProperty("relation").GetString() == "next");
            var url = next.ValueKind == System.Text.Json.JsonValueKind.Undefined ? null : next.GetProperty("url").GetString()!;
            url?.ShouldContain("_sort=-birthdate");
            path = url?[(url.IndexOf("/fhir/r4/", StringComparison.Ordinal) + "/fhir/r4/".Length)..];
        }

        paged.ShouldBe([estimated, october, june]);

        // An order it cannot follow is refused, never ignored.
        (await FhirAsync(partner, $"Patient?name={family}&_sort=identifier", "TREAT")).Refusal.ShouldBe((400, "invalid-parameter"));
    }

    [Fact]
    public async Task A_birth_date_estimated_from_an_age_is_found_by_the_year_it_is_served_with_not_the_day_stored_behind_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (partner, _, _) = await PartnerAsync(server);
        var family = "Birthq" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))));

        // Registered from a stated age, the birth date is stored as a day but served as its year; the other was born on
        // a day of that year.
        var estimated = await RegisterAsync(frontOffice, "Chitra", family, Gender.Female, null, ageYears: 46);
        var year = int.Parse((await FhirAsync(partner, $"Patient/{estimated}", "TREAT")).Body.GetProperty("birthDate").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        var june = await RegisterAsync(frontOffice, "Anand", family, Gender.Male, new DateOnly(year, 6, 15), null);
        DateOnly stored;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            stored = (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().AsNoTracking()
                .Where(p => p.Id == Guid.Parse(estimated)).Select(p => p.DateOfBirth).SingleAsync(TestContext.Current.CancellationToken))!.Value;
        }

        async Task<List<string>> Found(string birthdate) =>
            Matches((await FhirAsync(partner, $"Patient?name={family}&birthdate={birthdate}", "TREAT")).Body);

        (await Found($"{year}")).ShouldBe([estimated, june], ignoreOrder: true, "the year holds both");
        (await Found($"{year}-06-15")).ShouldBe([june]);
        (await Found(stored.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)))
            .ShouldNotContain(estimated, "the day stored behind an estimate is not what is served");
        (await Found($"lt{year}-02-01")).ShouldBe([estimated], "the served year starts on 1 January, whatever day is stored");
        (await Found($"gt{year}-11-30")).ShouldBe([estimated], "and ends on 31 December");
        (await Found($"ge{year + 1}")).ShouldBeEmpty();
    }

    /// <summary>Sets when a patient was last changed, to the microsecond, as the database keeps it.</summary>
    private async Task SetUpdatedAtAsync(string patientId, DateTimeOffset at)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Database.ExecuteSqlAsync(
            $"UPDATE patients.patients SET updated_at = {at} WHERE id = {Guid.Parse(patientId)}", TestContext.Current.CancellationToken);
    }

    /// <summary>A patient registered at the front desk, with a consent to share their records.</summary>
    private static async Task<string> RegisterAsync(ApiClient frontOffice, string firstName, string lastName, Gender gender, DateOnly? born, int? ageYears)
    {
        var phone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: firstName, MiddleName: null, LastName: lastName,
            Gender: gender, DateOfBirth: born, AgeYears: ageYears, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient.Id}/consents",
            new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null));
        return patient.Id.ToString("D");
    }
}
