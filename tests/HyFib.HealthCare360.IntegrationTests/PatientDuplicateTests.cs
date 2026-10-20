using System.Net;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Duplicate detection at registration (SRS FO-01) over the real API. The same person registered again is found by the
/// mobile number and first name, by the name and date of birth, by the ABHA number or by the Aadhaar number, and the
/// registration is refused with a 409 that names the existing UHID. A match by name or mobile number is a likeness the
/// desk may confirm is somebody else, and the patient is then registered; an ABHA or Aadhaar number already on another
/// record is not, as one number belongs to one person. A family member sharing the mobile number is shown to the desk as
/// a possible match without holding the registration, unless their age is within a year of the patient's.
/// </summary>
public sealed class PatientDuplicateTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_same_person_registered_again_is_refused_with_the_existing_uhid_until_the_desk_confirms()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mobile = NewMobile();
        var first = Registration(NewFirstName(), NewDateOfBirth(), mobile);
        var existing = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", first);

        // Typed again at the desk: the name in capitals with stray spaces, the number with its country code.
        var again = first with { FirstName = $" {first.FirstName.ToUpperInvariant()} ", Phone = $"+91 {mobile[..5]} {mobile[5..]}" };
        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/patients", again))
        {
            var problem = await ProblemAsync(refused, HttpStatusCode.Conflict);
            problem.Code.ShouldBe("patient.possible_duplicate");
            var match = problem.Data.ShouldNotBeNull().ShouldHaveSingleItem();
            (match.Id, match.Uhid, match.Score, match.Reason).ShouldBe((existing.Id, existing.Uhid, 90, "Same mobile number and first name"));
        }

        // The desk has checked, and it is somebody else: registered under a UHID of their own.
        var confirmed = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", again with { ConfirmedNotDuplicate = true });

        confirmed.Uhid.ShouldNotBe(existing.Uhid);
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{confirmed.Id}")).Phone.ShouldBe("+91" + mobile);
    }

    [Fact]
    public async Task The_same_name_and_date_of_birth_is_found_under_another_mobile_number_or_none()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var first = Registration(NewFirstName(), NewDateOfBirth(), NewMobile());
        var existing = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", first);

        foreach (var phone in new[] { NewMobile(), null })
        {
            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/patients", first with { Phone = phone });
            var match = (await ProblemAsync(refused, HttpStatusCode.Conflict)).Data.ShouldNotBeNull().ShouldHaveSingleItem();
            (match.Uhid, match.Score, match.Reason).ShouldBe((existing.Uhid, 85, "Same name and date of birth"), phone ?? "no mobile number");
        }
    }

    [Fact]
    public async Task A_family_member_sharing_the_mobile_number_is_shown_but_registers_without_confirmation()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mother = Registration(NewFirstName(), NewDateOfBirth(), NewMobile());
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", mother);

        // Her son, born 25 years after her and registered on her number, as families often are: a shared number alone
        // scores below the strong matches that hold a registration.
        var son = mother with { FirstName = NewFirstName(), Gender = Gender.Male, DateOfBirth = mother.DateOfBirth!.Value.AddYears(25) };
        var matches = await frontOffice.PostAsync<List<DuplicateCandidate>>("/api/v1/patients/duplicates",
            new DuplicateCheckRequest(son.FirstName, son.LastName, son.DateOfBirth, son.Phone, null, null));
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", son);

        var match = matches.ShouldHaveSingleItem();
        (match.Uhid, match.Score, match.Reason).ShouldBe((registered.Uhid, 50, "Same mobile number (possibly a family member)"));
        created.Uhid.ShouldNotBe(registered.Uhid);
    }

    [Fact]
    public async Task Someone_of_a_similar_age_sharing_the_mobile_number_is_held_for_the_desk_to_confirm()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var wife = Registration(NewFirstName(), NewDateOfBirth(), NewMobile());
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", wife);

        // Her husband, born months before her: with another first name it may still be her, so the desk must look.
        var husband = wife with { FirstName = NewFirstName(), Gender = Gender.Male, DateOfBirth = wife.DateOfBirth!.Value.AddDays(-200) };
        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/patients", husband))
        {
            var match = (await ProblemAsync(refused, HttpStatusCode.Conflict)).Data.ShouldNotBeNull().ShouldHaveSingleItem();
            (match.Uhid, match.Score, match.Reason).ShouldBe((registered.Uhid, 75, "Same mobile number and similar age"));
        }

        var confirmed = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", husband with { ConfirmedNotDuplicate = true });
        confirmed.Uhid.ShouldNotBe(registered.Uhid);
    }

    [Fact]
    public async Task An_abha_number_already_on_a_record_is_found_and_not_registered_again_even_when_confirmed()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var abha = "91" + Digits(12);
        var holder = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients",
            Registration(NewFirstName(), NewDateOfBirth(), NewMobile()) with { AbhaNumber = $"{abha[..2]}-{abha[2..6]}-{abha[6..10]}-{abha[10..]}" });

        // Somebody else in every other detail, under the same ABHA number written without its dashes.
        var other = Registration(NewFirstName(), NewDateOfBirth(), NewMobile()) with { AbhaNumber = abha };
        var matches = await frontOffice.PostAsync<List<DuplicateCandidate>>("/api/v1/patients/duplicates",
            new DuplicateCheckRequest(other.FirstName, other.LastName, other.DateOfBirth, other.Phone, abha, null));

        var match = matches.ShouldHaveSingleItem();
        (match.Uhid, match.Score, match.Reason).ShouldBe((holder.Uhid, 100, "Same ABHA number"));
        foreach (var confirmed in new[] { false, true })
        {
            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/patients", other with { ConfirmedNotDuplicate = confirmed });
            var problem = await ProblemAsync(refused, HttpStatusCode.Conflict);
            (problem.Code, problem.Detail).ShouldBe(("patient.abha_in_use", $"This ABHA number is already linked to UHID {holder.Uhid}."), $"confirmed: {confirmed}");
        }

        (await RegisteredWithAsync(frontOffice, other.Phone!)).ShouldBeEmpty("Nobody was registered under the number.");
    }

    [Fact]
    public async Task An_aadhaar_number_already_on_a_record_is_found_and_not_registered_again_even_when_confirmed()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var aadhaar = NewAadhaar();
        var holder = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients",
            Registration(NewFirstName(), NewDateOfBirth(), NewMobile()) with { AadhaarNumber = $"{aadhaar[..4]} {aadhaar[4..8]} {aadhaar[8..]}" });

        var other = Registration(NewFirstName(), NewDateOfBirth(), NewMobile()) with { AadhaarNumber = aadhaar };
        var matches = await frontOffice.PostAsync<List<DuplicateCandidate>>("/api/v1/patients/duplicates",
            new DuplicateCheckRequest(other.FirstName, other.LastName, other.DateOfBirth, other.Phone, null, aadhaar));

        var match = matches.ShouldHaveSingleItem();
        (match.Uhid, match.Score, match.Reason).ShouldBe((holder.Uhid, 100, "Same Aadhaar number"));
        foreach (var confirmed in new[] { false, true })
        {
            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/patients", other with { ConfirmedNotDuplicate = confirmed });
            var problem = await ProblemAsync(refused, HttpStatusCode.Conflict);
            (problem.Code, problem.Detail).ShouldBe(("patient.aadhaar_in_use", $"This Aadhaar number is already linked to UHID {holder.Uhid}."), $"confirmed: {confirmed}");
        }

        (await RegisteredWithAsync(frontOffice, other.Phone!)).ShouldBeEmpty("Nobody was registered under the number.");
    }

    [Fact]
    public async Task Only_staff_who_register_patients_may_look_for_duplicates()
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);

        using var refused = await lab.SendRawAsync(HttpMethod.Post, "/api/v1/patients/duplicates",
            new DuplicateCheckRequest(NewFirstName(), null, NewDateOfBirth(), NewMobile(), null, null));

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static RegisterPatientRequest Registration(string firstName, DateOnly dateOfBirth, string? phone) => new(
        Title: null, FirstName: firstName, MiddleName: null, LastName: "Duplicate" + Letters(8), Gender: Gender.Female,
        DateOfBirth: dateOfBirth, AgeYears: null, AgeMonths: null, Phone: phone, AlternatePhone: null, Email: null, Address: null,
        BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
        EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null,
        PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null,
        IsVip: false, Notes: null, ConfirmedNotDuplicate: false);

    private static async Task<Problem> ProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<Problem>(ApiClient.Json, Ct)).ShouldNotBeNull();
    }

    private static async Task<IReadOnlyList<PatientListItem>> RegisteredWithAsync(ApiClient client, string mobile) =>
        (await client.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={mobile}")).Items;

    // Names, numbers and dates no other test uses (the database outlives a test run), so a match is always this test's
    // own patient: a first name is matched with the date of birth whatever the rest of the record says.
    private static string NewFirstName() => "Kavya" + Letters(6);

    private static DateOnly NewDateOfBirth() => new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(15000));

    private static string NewMobile() => "9" + Digits(9);

    // A valid Aadhaar number: the first digit 2 to 9 and the one Verhoeff check digit that fits.
    private static string NewAadhaar()
    {
        var start = (char)('2' + Random.Shared.Next(8)) + Digits(10);
        return Enumerable.Range(0, 10).Select(d => start + d).Single(AadhaarNumber.IsAadhaar);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    /// <summary>The problem details of a refusal, with the possible duplicates a 409 lists.</summary>
    private sealed record Problem(string Code, string Detail, List<DuplicateCandidate>? Data);
}
