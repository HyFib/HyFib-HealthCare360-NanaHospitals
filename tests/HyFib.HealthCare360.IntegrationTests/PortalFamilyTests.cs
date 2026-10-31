using System.Net;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Privacy.Domain;
using HyFib.HealthCare360.Modules.Privacy.Features.Portal;
using HyFib.HealthCare360.Modules.Privacy.Features.Requests;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Family members on other mobile numbers in the patient portal (PP-01): shared at the front desk, or added in the portal
/// with a code sent to the family member's own number, and ended by staff, the account holder or the patient.
/// </summary>
public sealed class PortalFamilyTests(HospitalServer server)
{
    [Fact]
    public async Task The_front_desk_shares_a_record_with_a_family_members_account_until_it_is_ended()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (mother, motherPhone) = await RegisterPatientAsync(frontOffice, "Savitri");
        var sonPhone = NewMobile();
        var path = $"/api/v1/patients/{mother.Id}/portal-family";

        using (var notChecked = await frontOffice.SendRawAsync(HttpMethod.Post, path, new LinkFamilyMemberRequest(sonPhone, FamilyRelation.Parent, "Patient", false)))
        {
            notChecked.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "Staff must check who both people are first.");
        }

        using (var ownNumber = await frontOffice.SendRawAsync(HttpMethod.Post, path, new LinkFamilyMemberRequest(motherPhone, FamilyRelation.Parent, "Patient", true)))
        {
            ownNumber.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await ownNumber.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("portal.family_same_number");
        }

        await frontOffice.PostAsync(path, new LinkFamilyMemberRequest(sonPhone, FamilyRelation.Parent, "  Patient  ", true));
        using (var again = await frontOffice.SendRawAsync(HttpMethod.Post, path, new LinkFamilyMemberRequest(sonPhone, FamilyRelation.Parent, "Patient", true)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var access = await frontOffice.GetAsync<PatientPortalAccessDto>(path);
        access.OwnPhone.ShouldBe(Phone.Normalize(motherPhone));
        var link = access.Links.ShouldHaveSingleItem();
        link.AccountPhone.ShouldBe(Phone.Normalize(sonPhone));
        link.Source.ShouldBe(FamilyLinkSource.FrontDesk);
        link.ConsentGivenBy.ShouldBe("Patient");
        link.LinkedByName.ShouldNotBeNullOrEmpty();
        link.IsActive.ShouldBeTrue();

        // The son's account sees his mother's record next to his own number's patients; the mother sees who it is.
        var son = await server.SignInToPortalAsync(sonPhone);
        (await son.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).ShouldContain(p => p.Id == mother.Id);
        var sonsFamily = await son.GetAsync<PortalFamilyDto>("/api/v1/portal/family");
        var relative = sonsFamily.Linked.ShouldHaveSingleItem();
        (relative.PatientId, relative.Uhid, relative.Relation).ShouldBe((mother.Id, mother.Uhid, FamilyRelation.Parent));
        sonsFamily.SharedWith.ShouldBeEmpty();

        var motherAccount = await server.SignInToPortalAsync(motherPhone);
        var sharing = (await motherAccount.GetAsync<PortalFamilyDto>("/api/v1/portal/family")).SharedWith.ShouldHaveSingleItem();
        sharing.LinkId.ShouldBe(link.Id);
        sharing.AccountPhoneMasked.ShouldBe(Phone.Mask(Phone.Normalize(sonPhone)));

        await frontOffice.PostAsync($"/api/v1/patients/portal-family/{link.Id}/revoke", new RevokeFamilyLinkRequest("Patient asked at the front desk"));

        (await son.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).ShouldNotContain(p => p.Id == mother.Id);
        using (var record = await son.SendRawAsync(HttpMethod.Get, $"/api/v1/portal/health-records?patientId={mother.Id}"))
        {
            record.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        var ended = (await frontOffice.GetAsync<PatientPortalAccessDto>(path)).Links.ShouldHaveSingleItem();
        (ended.IsActive, ended.RevokedReason).ShouldBe((false, "Patient asked at the front desk"));
        (await AuditActionsAsync(mother.Id)).ShouldBe(["portal.family.linked", "portal.family.ended"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_account_holder_adds_a_relative_with_a_code_sent_to_the_relatives_number()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (father, fatherPhone) = await RegisterPatientAsync(frontOffice, "Raghav");
        var daughterPhone = NewMobile();
        var daughter = await server.SignInToPortalAsync(daughterPhone);

        var sent = await daughter.PostAsync<FamilyCodeSent>("/api/v1/portal/family/codes",
            new RequestFamilyCodeRequest(father.Uhid.ToLowerInvariant(), fatherPhone, FamilyRelation.Parent));
        var message = await FamilyCodeMessageAsync(fatherPhone);
        message.Values["requester"].ShouldContain(Phone.Mask(Phone.Normalize(daughterPhone)));

        using (var wrong = await daughter.SendRawAsync(HttpMethod.Post, $"/api/v1/portal/family/codes/{sent.CodeId}/verify",
                   new VerifyFamilyCodeRequest(message.Values["code"] == "000000" ? "111111" : "000000")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // Another account cannot use the daughter's code.
        var stranger = await server.SignInToPortalAsync(NewMobile());
        using (var stolen = await stranger.SendRawAsync(HttpMethod.Post, $"/api/v1/portal/family/codes/{sent.CodeId}/verify",
                   new VerifyFamilyCodeRequest(message.Values["code"])))
        {
            stolen.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var linked = await daughter.PostAsync<PortalLinkedRelativeDto>($"/api/v1/portal/family/codes/{sent.CodeId}/verify",
            new VerifyFamilyCodeRequest(message.Values["code"]));
        (linked.PatientId, linked.Source, linked.Relation).ShouldBe((father.Id, FamilyLinkSource.PortalCode, FamilyRelation.Parent));
        (await daughter.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).ShouldContain(p => p.Id == father.Id);

        var staffView = (await frontOffice.GetAsync<PatientPortalAccessDto>($"/api/v1/patients/{father.Id}/portal-family")).Links.ShouldHaveSingleItem();
        (staffView.Source, staffView.CodeSentTo, staffView.ConsentGivenBy).ShouldBe((FamilyLinkSource.PortalCode, Phone.Normalize(fatherPhone), null));

        // A request the daughter files "for herself" about her father is not taken as checked by her sign-in: her code
        // went to her own number, not his.
        var created = await daughter.PostAsync<PortalRequestCreated>("/api/v1/portal/privacy/requests", new PortalDataRequestCreate(
            father.Id, DataRequestType.Grievance, RequesterRelation.Self, null, "The address on the card is old.", null));
        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var queued = await officer.GetAsync<PagedResult<DataRequestListItem>>($"/api/v1/privacy/requests?openOnly=true&search={created.RequestNumber}");
        queued.Items.ShouldHaveSingleItem().IdentityVerified.ShouldBeFalse();

        // The father stops sharing from his own account.
        var fatherAccount = await server.SignInToPortalAsync(fatherPhone);
        var sharing = (await fatherAccount.GetAsync<PortalFamilyDto>("/api/v1/portal/family")).SharedWith.ShouldHaveSingleItem();
        await fatherAccount.PostAsync($"/api/v1/portal/family/links/{sharing.LinkId}/end");

        (await daughter.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles")).ShouldNotContain(p => p.Id == father.Id);
        (await daughter.GetAsync<PortalFamilyDto>("/api/v1/portal/family")).Linked.ShouldBeEmpty();
        var ended = (await frontOffice.GetAsync<PatientPortalAccessDto>($"/api/v1/patients/{father.Id}/portal-family")).Links.ShouldHaveSingleItem();
        ended.RevokedReason.ShouldBe("Stopped in the portal from the patient's own number.");

        // Nobody else can end a link they are not part of.
        using (var other = await stranger.SendRawAsync(HttpMethod.Post, $"/api/v1/portal/family/links/{sharing.LinkId}/end"))
        {
            other.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task A_code_request_does_not_say_whether_a_uhid_and_a_number_belong_together()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, _) = await RegisterPatientAsync(frontOffice, "Meena");
        var account = await server.SignInToPortalAsync(NewMobile());
        var otherNumber = NewMobile();

        var sent = await account.PostAsync<FamilyCodeSent>("/api/v1/portal/family/codes",
            new RequestFamilyCodeRequest(patient.Uhid, otherNumber, FamilyRelation.Sibling));
        sent.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        (await FamilyCodeMessagesAsync(otherNumber)).ShouldBeEmpty("Nothing is sent when the number is not the patient's.");

        using (var guess = await account.SendRawAsync(HttpMethod.Post, $"/api/v1/portal/family/codes/{sent.CodeId}/verify", new VerifyFamilyCodeRequest("123456")))
        {
            guess.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "A guess fails the same way as a wrong code.");
        }

        // A number gets a few codes in 15 minutes, whoever asks.
        for (var i = 1; i < 3; i++)
        {
            await account.PostAsync<FamilyCodeSent>("/api/v1/portal/family/codes", new RequestFamilyCodeRequest(patient.Uhid, otherNumber, FamilyRelation.Sibling));
        }

        using var throttled = await account.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes",
            new RequestFamilyCodeRequest(patient.Uhid, otherNumber, FamilyRelation.Sibling));
        throttled.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await throttled.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("portal.family_code_throttled");
    }

    [Fact]
    public async Task Tries_of_a_code_sent_together_are_each_counted()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (father, fatherPhone) = await RegisterPatientAsync(frontOffice, "Kiran");
        var account = await server.SignInToPortalAsync(NewMobile());
        var sent = await account.PostAsync<FamilyCodeSent>("/api/v1/portal/family/codes",
            new RequestFamilyCodeRequest(father.Uhid, fatherPhone, FamilyRelation.Parent));
        var code = (await FamilyCodeMessageAsync(fatherPhone)).Values["code"];
        var verify = $"/api/v1/portal/family/codes/{sent.CodeId}/verify";

        var tries = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => account.SendRawAsync(HttpMethod.Post, verify, new VerifyFamilyCodeRequest(code == "000000" ? "111111" : "000000"))));
        try
        {
            tries.Count(r => r.StatusCode == HttpStatusCode.BadRequest).ShouldBe(PortalFamilyCode.MaxAttempts, "every wrong code counts");
            tries.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ShouldBe(12 - PortalFamilyCode.MaxAttempts);
        }
        finally
        {
            foreach (var response in tries)
            {
                response.Dispose();
            }
        }

        using var late = await account.SendRawAsync(HttpMethod.Post, verify, new VerifyFamilyCodeRequest(code));
        late.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, "the right code no longer works once the tries are used up");
    }

    [Fact]
    public async Task Code_requests_sent_together_stay_within_the_limit_for_a_number()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (sister, sisterPhone) = await RegisterPatientAsync(frontOffice, "Lata");
        var account = await server.SignInToPortalAsync(NewMobile());

        var requests = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => account.SendRawAsync(HttpMethod.Post, "/api/v1/portal/family/codes",
                new RequestFamilyCodeRequest(sister.Uhid, sisterPhone, FamilyRelation.Sibling))));
        try
        {
            // A number gets three codes in 15 minutes (PortalFamilyService.CodesPerNumberPer15Minutes).
            requests.Count(r => r.IsSuccessStatusCode).ShouldBe(3);
        }
        finally
        {
            foreach (var response in requests)
            {
                response.Dispose();
            }
        }

        (await FamilyCodeMessagesAsync(sisterPhone)).Count.ShouldBe(3);
    }

    [Fact]
    public async Task Merging_a_duplicate_moves_its_family_links_to_the_record_that_stays()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (duplicate, _) = await RegisterPatientAsync(frontOffice, "Kamala");
        var (kept, _) = await RegisterPatientAsync(frontOffice, "Kamala");
        var grandsonPhone = NewMobile();
        var sisterPhone = NewMobile();
        await frontOffice.PostAsync($"/api/v1/patients/{duplicate.Id}/portal-family", new LinkFamilyMemberRequest(grandsonPhone, FamilyRelation.Grandparent, "Patient", true));
        await frontOffice.PostAsync($"/api/v1/patients/{duplicate.Id}/portal-family", new LinkFamilyMemberRequest(sisterPhone, FamilyRelation.Sibling, "Patient", true));
        await frontOffice.PostAsync($"/api/v1/patients/{kept.Id}/portal-family", new LinkFamilyMemberRequest(sisterPhone, FamilyRelation.Sibling, "Patient", true));

        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        await MakerChecker.MergeAsync(mrd, await server.SignInAsync(HospitalServer.Users.MrdVerifier), duplicate.Id, kept.Id, "Registered twice");

        var grandson = await server.SignInToPortalAsync(grandsonPhone);
        var profiles = await grandson.GetAsync<List<PatientSummary>>("/api/v1/portal/profiles");
        profiles.ShouldContain(p => p.Id == kept.Id);
        profiles.ShouldNotContain(p => p.Id == duplicate.Id);

        var links = (await frontOffice.GetAsync<PatientPortalAccessDto>($"/api/v1/patients/{kept.Id}/portal-family")).Links;
        links.Count(l => l.IsActive).ShouldBe(2, "The grandson's link moved; the sister's second link was ended as she already sees the record.");
        links.ShouldContain(l => l.AccountPhone == Phone.Normalize(sisterPhone) && !l.IsActive);
    }

    [Fact]
    public async Task Closing_a_portal_account_at_the_patients_request_ends_its_family_links_both_ways()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patient, phone) = await RegisterPatientAsync(frontOffice, "Lakshmi");
        var (husband, _) = await RegisterPatientAsync(frontOffice, "Gopal");
        await frontOffice.PostAsync($"/api/v1/patients/{patient.Id}/portal-family", new LinkFamilyMemberRequest(NewMobile(), FamilyRelation.Parent, "Patient", true));
        await frontOffice.PostAsync($"/api/v1/patients/{husband.Id}/portal-family", new LinkFamilyMemberRequest(phone, FamilyRelation.Spouse, "Patient", true));

        var portal = await server.SignInToPortalAsync(phone);
        var created = await portal.PostAsync<PortalRequestCreated>("/api/v1/portal/privacy/requests", new PortalDataRequestCreate(
            patient.Id, DataRequestType.Erasure, RequesterRelation.Self, null, "Please close my portal account.", null));
        var officer = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await officer.PostAsync<ErasureOutcome>($"/api/v1/privacy/requests/{created.Id}/erasure", new EraseDataRequest([ErasureService.PortalAccount]));

        var hers = (await frontOffice.GetAsync<PatientPortalAccessDto>($"/api/v1/patients/{patient.Id}/portal-family")).Links.ShouldHaveSingleItem();
        hers.IsActive.ShouldBeFalse("Her son no longer sees her records.");
        var his = (await frontOffice.GetAsync<PatientPortalAccessDto>($"/api/v1/patients/{husband.Id}/portal-family")).Links.ShouldHaveSingleItem();
        his.IsActive.ShouldBeFalse("Her closed account no longer sees her husband's records.");
    }

    private async Task<(PatientCreated Patient, string Phone)> RegisterPatientAsync(ApiClient frontOffice, string firstName)
    {
        var phone = NewMobile();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: firstName, MiddleName: null, LastName: "Family" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (patient, phone);
    }

    private async Task<NotificationDelivery> FamilyCodeMessageAsync(string phone) => (await FamilyCodeMessagesAsync(phone)).ShouldHaveSingleItem();

    // The test server sends no SMS: the message waits in the outbox.
    private async Task<List<NotificationDelivery>> FamilyCodeMessagesAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var to = Phone.Normalize(phone);
        var deliveries = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.SmsType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(TestContext.Current.CancellationToken);
        return deliveries.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == "portal.family_code" && d.To == to).ToList();
    }

    private async Task<List<string>> AuditActionsAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        return await db.Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action.StartsWith("portal.family."))
            .Select(e => e.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static string NewMobile() => "9" + string.Concat(Enumerable.Range(0, 9).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
