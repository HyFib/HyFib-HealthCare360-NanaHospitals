using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A patient registered by her age has a date of birth the desk worked out: the year from the age, the day and month
/// of the registration. In the production-style run the portal showed it as her date of birth. The portal's profiles
/// and its summary of the data held now say it was estimated, so the portal can say so too.
/// </summary>
public sealed class EstimatedBirthDateTests(HospitalServer server)
{
    [Fact]
    public async Task The_portal_is_told_a_date_of_birth_worked_out_from_the_age()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var phone = "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
        var byAge = await RegisterAsync(frontOffice, phone, dateOfBirth: null, ageYears: 52);
        var byDate = await RegisterAsync(frontOffice, phone, new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(3650)), ageYears: null);
        var portal = await server.SignInToPortalAsync(phone);

        var profiles = await portal.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles");
        var estimated = profiles.Single(p => p.Id == byAge.Id);
        estimated.DateOfBirth.ShouldNotBeNull();
        estimated.IsDateOfBirthEstimated.ShouldBeTrue();
        profiles.Single(p => p.Id == byDate.Id).IsDateOfBirthEstimated.ShouldBeFalse();

        (await portal.GetAsync<DataSummary>($"/api/v1/portal/privacy/summary?patientId={byAge.Id}")).Person.IsDateOfBirthEstimated.ShouldBeTrue();
        (await portal.GetAsync<DataSummary>($"/api/v1/portal/privacy/summary?patientId={byDate.Id}")).Person.IsDateOfBirthEstimated.ShouldBeFalse();
    }

    private static Task<PatientCreated> RegisterAsync(ApiClient frontOffice, string phone, DateOnly? dateOfBirth, int? ageYears) =>
        frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: "Kamala", MiddleName: null, LastName: "Estimate" + Letters(8), Gender: Gender.Female,
            DateOfBirth: dateOfBirth, AgeYears: ageYears, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
