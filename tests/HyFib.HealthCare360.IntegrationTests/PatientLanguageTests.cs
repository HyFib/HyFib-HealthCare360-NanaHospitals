using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS PP-06 through the whole server: a refusal the portal shows is written in the patient's language (the preferred
/// language on their record, else the portal's Accept-Language), with its machine code unchanged; staff answers stay in
/// English.
/// </summary>
public sealed class PatientLanguageTests(HospitalServer server)
{
    private const string OwnNumberHindi = "आपके अपने नंबर से पंजीकृत सभी लोग पहले से आपके परिवार में हैं।";
    private const string OwnNumberEnglish = "Everyone registered with your own number is already in your family.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_records_preference_wins_over_the_browsers_language()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, "Hindi");
        var portal = await server.SignInToPortalAsync(phone);
        portal.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");

        using var response = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes",
            new RequestFamilyCodeRequest(patient.Uhid, phone, FamilyRelation.Parent));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentLanguage.ShouldBe(["hi"]);
        var problem = await ProblemOf(response);
        problem.GetProperty("code").GetString().ShouldBe("portal.family_own_number");
        problem.GetProperty("title").GetString().ShouldBe("यह काम पूरा नहीं किया जा सकता");
        problem.GetProperty("detail").GetString().ShouldBe(OwnNumberHindi);
    }

    [Fact]
    public async Task Without_a_preference_the_portals_language_decides_and_english_is_the_default()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, null);
        var portal = await server.SignInToPortalAsync(phone);
        var request = new RequestFamilyCodeRequest(patient.Uhid, phone, FamilyRelation.Parent);

        portal.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("hi-IN");
        using (var hindi = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes", request))
        {
            (await ProblemOf(hindi)).GetProperty("detail").GetString().ShouldBe(OwnNumberHindi);
        }

        portal.Http.DefaultRequestHeaders.AcceptLanguage.Clear();
        using (var english = await portal.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes", request))
        {
            (await ProblemOf(english)).GetProperty("detail").GetString().ShouldBe(OwnNumberEnglish);
        }
    }

    [Fact]
    public async Task Staff_answers_stay_in_english_whatever_the_browser_says()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, "Hindi");
        frontOffice.Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("hi-IN");

        using var response = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{patient.Id}/portal-family",
            new LinkFamilyMemberRequest(phone, FamilyRelation.Parent, "Patient", true));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        response.Content.Headers.ContentLanguage.ShouldBeEmpty();
        (await ProblemOf(response)).GetProperty("detail").GetString()
            .ShouldBe("The patient is registered with this mobile number, so its portal account sees the record already.");
    }

    private static async Task<JsonElement> ProblemOf(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static async Task<(PatientCreated Patient, string Phone)> RegisterPatientAsync(ApiClient frontOffice, string? preferredLanguage)
    {
        var phone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Language", MiddleName: null, LastName: "Test" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))),
            Gender: Gender.Female, DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: preferredLanguage,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (patient, phone);
    }
}
