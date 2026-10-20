using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// FO-01 over the real API: registering someone who is likely registered already is refused with the matches, until the
/// front office confirms it is someone else. The same mobile number and first name (90), the same first name and date of
/// birth (85), and the same mobile number with an age within a year (75) are refused; the same mobile number alone, as one
/// family shares a phone, is not. The matches show the mobile number masked.
/// </summary>
public sealed class PatientDuplicateCheckTests(HospitalServer server)
{
    private static readonly DateOnly Born = new(1979, 5, 23);

    [Fact]
    public async Task A_likely_duplicate_is_refused_with_its_matches_until_the_front_office_confirms_it_is_someone_else()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var firstName = TestPatients.FirstName("Sunita");
        var mobile = NewMobile();
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Patient(firstName, Born, mobile));

        var sameBirthday = await RefusedAsync(frontOffice, Patient(firstName, Born, NewMobile()), mobile);
        sameBirthday.ShouldContain(m => m.Id == registered.Id && m.Score == 85 && m.Reason == "Same name and date of birth");

        var sameMobile = await RefusedAsync(frontOffice, Patient(firstName, Born.AddYears(-6), mobile), mobile);
        sameMobile.ShouldContain(m => m.Id == registered.Id && m.Score == 90 && m.Reason == "Same mobile number and first name");
        sameMobile.Single(m => m.Id == registered.Id).PhoneMasked.ShouldBe(Phone.Mask("+91" + mobile));

        var sameMobileAndAge = await RefusedAsync(frontOffice, Patient(TestPatients.FirstName("Sunita"), Born.AddMonths(7), mobile), mobile);
        sameMobileAndAge.ShouldContain(m => m.Id == registered.Id && m.Score == 75 && m.Reason == "Same mobile number and similar age");

        // Confirmed as someone else, the same first name and date of birth is registered.
        var confirmed = await frontOffice.PostAsync<PatientCreated>(
            "/api/v1/patients", Patient(firstName, Born, NewMobile()) with { ConfirmedNotDuplicate = true });
        confirmed.Id.ShouldNotBe(registered.Id);

        // A parent on the same mobile number is not refused.
        var parent = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Patient(TestPatients.FirstName("Sunita"), Born.AddYears(-30), mobile));
        parent.Id.ShouldNotBe(registered.Id);
    }

    /// <summary>The matches of a refused registration; <paramref name="matchedMobile"/>, theirs, must not be in the answer.</summary>
    private static async Task<List<DuplicateCandidate>> RefusedAsync(ApiClient frontOffice, RegisterPatientRequest request, string matchedMobile)
    {
        using var response = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/patients", request);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldNotContain(matchedMobile, Case.Sensitive, "The matches show the mobile number masked.");
        var problem = JsonSerializer.Deserialize<JsonElement>(body, ApiClient.Json);
        problem.GetProperty("code").GetString().ShouldBe("patient.possible_duplicate");
        return problem.GetProperty("data").Deserialize<List<DuplicateCandidate>>(ApiClient.Json).ShouldNotBeNull();
    }

    private static RegisterPatientRequest Patient(string firstName, DateOnly born, string mobile) => new(
        Title: "Mrs.", FirstName: firstName, MiddleName: null, LastName: "Rao", Gender: Gender.Female, DateOfBirth: born, AgeYears: null,
        AgeMonths: null, Phone: mobile, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
        GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
        Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null,
        AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false);

    // Other tests' mobile numbers start 7 to 9.
    private static string NewMobile() => "6" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
