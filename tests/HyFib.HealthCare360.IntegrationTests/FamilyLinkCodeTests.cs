using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Sharing a patient's reports and bills with another mobile number at the front desk (MAKER-CHECKER.md, slice 14): a code
/// goes by SMS to the patient's own registered number, the one a new number is confirmed with (PR #279), and no link exists
/// until the desk enters it: wrong codes count, a patient with no number cannot share this way, and a code sent to a number
/// the patient no longer has shares nothing.
/// </summary>
public sealed class FamilyLinkCodeTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_code_goes_to_the_patients_own_number_and_wrong_codes_share_nothing()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var (patient, patientPhone) = await RegisterAsync(frontOffice, NewMobile());
        var familyPhone = NewMobile();
        var path = $"/api/v1/patients/{patient.Id}/portal-family";

        var sent = await frontOffice.PostAsync<FamilyDeskCodeSent>(path, new LinkFamilyMemberRequest(familyPhone, FamilyRelation.Child, "Patient", true));
        var messages = await DeskMessagesAsync();
        messages.ShouldContain(m => m.To == Phone.Normalize(patientPhone), "The code goes to the patient's own number.");
        messages.ShouldNotContain(m => m.To == Phone.Normalize(familyPhone), "Nothing goes to the number asking to see the records.");
        var code = await server.DeskFamilyCodeAsync(patient.Id);
        var wrong = code == "000000" ? "111111" : "000000";

        for (var attempt = 1; attempt <= PortalFamilyCodeMaxAttempts; attempt++)
        {
            using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, $"{path}/codes/{sent.CodeId}/confirm", new ConfirmFamilyLinkRequest(wrong, "Patient"));
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"try {attempt}");
        }

        // Five wrong tries spend the code: even the right one now shares nothing.
        using (var spent = await frontOffice.SendRawAsync(HttpMethod.Post, $"{path}/codes/{sent.CodeId}/confirm", new ConfirmFamilyLinkRequest(code, "Patient")))
        {
            spent.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await spent.Content.ReadAsStringAsync(Ct)).ShouldContain("portal.family_code_expired");
        }

        (await frontOffice.GetAsync<PatientPortalAccessDto>(path)).Links.ShouldBeEmpty();
        var family = await server.SignInToPortalAsync(familyPhone);
        (await family.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).ShouldNotContain(p => p.Id == patient.Id);

        // A new code, entered right, makes the link: named to who agreed and the number the code went to.
        var link = await server.LinkFamilyAtDeskAsync(frontOffice, patient.Id, new LinkFamilyMemberRequest(familyPhone, FamilyRelation.Child, "Patient", true));
        var made = (await frontOffice.GetAsync<PatientPortalAccessDto>(path)).Links.ShouldHaveSingleItem();
        (made.Id, made.Source, made.CodeSentTo, made.IsActive).ShouldBe((link.Id, FamilyLinkSource.FrontDesk, Phone.Normalize(patientPhone), true));
        (await family.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).ShouldContain(p => p.Id == patient.Id);
    }

    [Fact]
    public async Task A_patient_with_no_mobile_number_of_their_own_cannot_share_this_way()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var (patient, _) = await RegisterAsync(frontOffice, phone: null);

        using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/patients/{patient.Id}/portal-family",
            new LinkFamilyMemberRequest(NewMobile(), FamilyRelation.Spouse, "Patient", true));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("portal.family_no_patient_phone");
    }

    [Fact]
    public async Task A_code_sent_to_a_number_the_patient_no_longer_has_shares_nothing()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var (patient, _) = await RegisterAsync(frontOffice, NewMobile());
        var path = $"/api/v1/patients/{patient.Id}/portal-family";
        var sent = await frontOffice.PostAsync<FamilyDeskCodeSent>(path, new LinkFamilyMemberRequest(NewMobile(), FamilyRelation.Sibling, "Patient", true));
        var code = await server.DeskFamilyCodeAsync(patient.Id);

        // The patient's number changes (with its own code, to the new number) before the desk enters the first code.
        var newPhone = NewMobile();
        var change = await frontOffice.PostAsync<PhoneChangeDto>($"/api/v1/patients/{patient.Id}/phone-change", new RequestPhoneChangeRequest(newPhone));
        await frontOffice.PostAsync<PhoneChangeConfirmed>($"/api/v1/patients/{patient.Id}/phone-change/{change.ChangeId}/confirm",
            new ConfirmPhoneChangeRequest(await PhoneCodeAsync(newPhone)));

        using var refused = await frontOffice.SendRawAsync(HttpMethod.Post, $"{path}/codes/{sent.CodeId}/confirm", new ConfirmFamilyLinkRequest(code, "Patient"));
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("portal.family_number_changed");
        (await frontOffice.GetAsync<PatientPortalAccessDto>(path)).Links.ShouldBeEmpty();
    }

    private const int PortalFamilyCodeMaxAttempts = PortalFamilyCode.MaxAttempts;

    private async Task<List<NotificationDelivery>> DeskMessagesAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var deliveries = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(Ct);
        return [.. deliveries.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == PortalFamilyService.DeskCodeTemplate)];
    }

    private async Task<string> PhoneCodeAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var deliveries = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(Ct);
        return deliveries.Select(m => m.Read<NotificationDelivery>())
            .First(d => d.TemplateKey == PatientPhoneChangeService.Template && d.To == Phone.Normalize(phone)).Values["code"];
    }

    private static async Task<(PatientCreated Patient, string? Phone)> RegisterAsync(ApiClient frontOffice, string? phone)
    {
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Kamala", MiddleName: null, LastName: "Code" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray()), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1948, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null, Phone: phone, AlternatePhone: null,
            Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: true));
        return (patient, phone);
    }

    private static string NewMobile() => "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
