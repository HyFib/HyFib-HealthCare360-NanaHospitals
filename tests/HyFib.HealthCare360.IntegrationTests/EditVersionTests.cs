using System.Net;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Engagement.Features.Portal;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.HR.Domain;
using HyFib.HealthCare360.Modules.HR.Features.Payroll;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// SRS REL-04 over the real API: an edit says which version of the record it changes, a change saved in between is
/// refused (409) and never silently overwritten, and an edit that leaves the version out is refused too. The
/// architecture tests check that every edit takes part; these show the three ways it does.
/// </summary>
public sealed class EditVersionTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_edit_that_does_not_say_which_version_it_changes_is_refused()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var code = "RV" + Letters(6);
        var created = await admin.PostAsync<Created>("/api/v1/masters/specialties", new SaveSpecialtyRequest(code, "Version check", null, true, null));
        var path = $"/api/v1/masters/specialties/{created.Id}";
        var loaded = await admin.GetAsync<SpecialtyDto>(path);

        using (var unversioned = await admin.SendRawAsync(HttpMethod.Put, path, new SaveSpecialtyRequest(code, "Without a version", null, true, null)))
        {
            unversioned.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var body = await unversioned.Content.ReadAsStringAsync(Ct);
            body.ShouldContain("\"version\"");
            body.ShouldContain("did not say which version");
        }

        (await admin.GetAsync<SpecialtyDto>(path)).Name.ShouldBe("Version check");

        // Two administrators loaded the same version; the first save wins and the second is refused.
        await admin.PutAsync(path, new SaveSpecialtyRequest(code, "First", null, true, loaded.Version));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, new SaveSpecialtyRequest(code, "Second", null, true, loaded.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        (await admin.GetAsync<SpecialtyDto>(path)).Name.ShouldBe("First");
    }

    [Fact]
    public async Task A_record_its_first_save_creates_is_not_saved_over_without_its_version()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var userId = await NewUserAsync(admin);

        // The first save creates the limit: there was nothing to load, so there is no version to send.
        await admin.PutAsync("/api/v1/billing/discount-limits/", new SaveDiscountLimitRequest(userId, 10m, null, "First"));
        var first = await LimitAsync(admin, userId);

        // A second "first" save, from someone who never saw the limit, would overwrite it unseen.
        using (var unseen = await admin.SendRawAsync(HttpMethod.Put, "/api/v1/billing/discount-limits/", new SaveDiscountLimitRequest(userId, 50m, null, "Unseen")))
        {
            unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        await admin.PutAsync("/api/v1/billing/discount-limits/", new SaveDiscountLimitRequest(userId, 15m, null, "Seen", first.Version));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, "/api/v1/billing/discount-limits/", new SaveDiscountLimitRequest(userId, 20m, null, "Stale", first.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var limit = await LimitAsync(admin, userId);
        (limit.MaxPercent, limit.Notes).ShouldBe((15m, "Seen"));
    }

    [Fact]
    public async Task A_states_professional_tax_slabs_are_replaced_only_as_they_were_loaded()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var state = Letters(4).ToUpperInvariant();
        var path = $"/api/v1/hr/payroll/professional-tax/{state}";
        SaveProfessionalTaxRequest Slabs(decimal amount, uint? version = null) => new(
            "Test state " + state, ProfessionalTaxFrequency.Monthly, [], [new ProfessionalTaxBandDto(0m, null, 0m, null), new(15000m, null, amount, null)], version);

        await admin.PutAsync(path, Slabs(150m));
        using (var unseen = await admin.SendRawAsync(HttpMethod.Put, path, Slabs(175m)))
        {
            unseen.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var loaded = await ScheduleAsync(admin, state);
        await admin.PutAsync(path, Slabs(200m, loaded.Version));
        using (var stale = await admin.SendRawAsync(HttpMethod.Put, path, Slabs(250m, loaded.Version)))
        {
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var saved = await ScheduleAsync(admin, state);
        saved.Bands.Max(b => b.Amount).ShouldBe(200m);
        saved.Version.ShouldNotBe(loaded.Version);
    }

    [Fact]
    public async Task A_portal_switch_changes_only_its_own_choice()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var mobile = "9" + Digits(9);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Asha", MiddleName: null, LastName: "Rv" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: mobile, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true, WhatsAppOptIn: false));
        var phone = await server.SignInToPortalAsync(Phone.Normalize(mobile)!);
        var laptop = await server.SignInToPortalAsync(Phone.Normalize(mobile)!);
        var path = $"/api/v1/portal/communication-preferences?patientId={patient.Id}";
        var before = await laptop.GetAsync<CommunicationPreferences>(path);

        // On the phone, the patient changes their marketing choice; the laptop still shows the old one when they
        // change their updates choice there. Each switch sends only itself, so the laptop does not undo the phone.
        await phone.PutAsync("/api/v1/portal/communication-preferences", new CommunicationPreferencesRequest(patient.Id, Marketing: !before.Marketing));
        await laptop.PutAsync("/api/v1/portal/communication-preferences", new CommunicationPreferencesRequest(patient.Id, Updates: !before.Updates));

        var after = await laptop.GetAsync<CommunicationPreferences>(path);
        (after.Marketing, after.Updates).ShouldBe((!before.Marketing, !before.Updates));
    }

    private async Task<Guid> NewUserAsync(ApiClient admin)
    {
        var userName = "it-rel04-" + Letters(8);
        await server.CreateStaffAccountAsync(userName, SystemRoles.Billing);
        return (await admin.GetAsync<PagedResult<UserListItem>>($"/api/v1/admin/users?search={userName}")).Items.Single(u => u.UserName == userName).Id;
    }

    private static async Task<DiscountLimitDto> LimitAsync(ApiClient admin, Guid userId) =>
        (await admin.GetAsync<List<DiscountLimitDto>>("/api/v1/billing/discount-limits")).Single(l => l.UserId == userId);

    private static async Task<ProfessionalTaxScheduleDto> ScheduleAsync(ApiClient admin, string state) =>
        (await admin.GetAsync<List<ProfessionalTaxScheduleDto>>("/api/v1/hr/payroll/professional-tax")).Single(s => s.StateCode == state);

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private sealed record Created(Guid Id);
}
