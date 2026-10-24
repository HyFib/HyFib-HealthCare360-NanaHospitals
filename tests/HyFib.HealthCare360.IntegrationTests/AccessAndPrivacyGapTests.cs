using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Privacy.Services;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Who may look patients up, and what a restricted record shows in the list; the CERT-In point of contact; the privacy
/// notice given at registration; and default grants a role no longer has being taken back from existing installations.
/// </summary>
public sealed class AccessAndPrivacyGapTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Staff_who_never_see_patients_cannot_look_them_up()
    {
        var cssd = await server.SignInAsync(HospitalServer.Users.CssdTechnician);
        using var refused = await cssd.SendRawAsync(HttpMethod.Get, "/api/v1/patients?pageSize=5");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        await frontOffice.GetAsync<PagedResult<PatientListItem>>("/api/v1/patients?pageSize=5");
    }

    [Fact]
    public async Task A_restricted_record_shows_only_initials_in_the_list_to_those_who_cannot_open_it()
    {
        var lastName = "Hidden" + Letters(8);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration("Rajiv", lastName, isVip: true));

        var search = $"/api/v1/patients?search={lastName}&pageSize=5";
        var masked = (await frontOffice.GetAsync<PagedResult<PatientListItem>>(search)).Items.ShouldHaveSingleItem();
        masked.Id.ShouldBe(patient.Id);
        masked.FullName.ShouldBe("R. " + lastName[0] + ". (restricted)");

        // The hospital administrator may open restricted records, so sees the name.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.GetAsync<PagedResult<PatientListItem>>(search)).Items.ShouldHaveSingleItem().FullName.ShouldContain(lastName);

        // The duplicate check at registration masks it alike.
        var check = new DuplicateCheckRequest("Rajiv", lastName, masked.DateOfBirth, null, null, null);
        (await frontOffice.PostAsync<List<DuplicateCandidate>>("/api/v1/patients/duplicates", check)).Single(c => c.Id == patient.Id)
            .FullName.ShouldBe(masked.FullName);
        (await admin.PostAsync<List<DuplicateCandidate>>("/api/v1/patients/duplicates", check)).Single(c => c.Id == patient.Id)
            .FullName.ShouldContain(lastName);
    }

    [Fact]
    public async Task The_cert_in_point_of_contact_needs_a_name_an_email_and_a_phone_and_is_saved_with_its_version()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        const string Path = "/api/v1/privacy/settings/cert-in";
        try
        {
            using (var incomplete = await admin.SendRawAsync(HttpMethod.Put, Path, new CertInContact("Priya Raman", "CISO", null, null, null)))
            {
                incomplete.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                var errors = (await incomplete.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
                errors.TryGetProperty("email", out _).ShouldBeTrue();
                errors.TryGetProperty("phone", out _).ShouldBeTrue();
            }

            var before = await admin.GetAsync<CertInContact>(Path);
            await admin.PutAsync(Path, new CertInContact(" Priya Raman ", "CISO", "ciso@nanahospitals.in", "9876543210", null, before.Version));

            var saved = await admin.GetAsync<CertInContact>(Path);
            (saved.Name, saved.Designation, saved.Email).ShouldBe(("Priya Raman", "CISO", "ciso@nanahospitals.in"));
            saved.Phone.ShouldNotBeNull().ShouldEndWith("9876543210");
            saved.IsComplete.ShouldBeTrue();

            // Saved over a change made since it was read: refused.
            using var stale = await admin.SendRawAsync(HttpMethod.Put, Path, new CertInContact("Someone Else", null, "x@y.in", "9876500000", null, before.Version));
            stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(PrivacySettings.CertInContactSetting, null, Ct);
        }
    }

    [Fact]
    public async Task The_privacy_notice_given_at_registration_is_recorded_with_the_notice_version_in_force()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        var first = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration("Lakshmi", "Notice" + Letters(8), privacyNoticeGiven: true));
        var notGiven = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration("Lakshmi", "NoNotice" + Letters(8)));

        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(PrivacyNotice.SettingKey, "2026-10", null, false, Ct);
        try
        {
            var second = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration("Lakshmi", "Notice" + Letters(8), privacyNoticeGiven: true));

            (await NoticesAsync(first)).ShouldHaveSingleItem().ConsentVersion.ShouldBe(PrivacyNotice.DefaultVersion);
            (await NoticesAsync(notGiven)).ShouldBeEmpty();
            var recorded = (await NoticesAsync(second)).ShouldHaveSingleItem();
            (recorded.ConsentVersion, recorded.Granted, recorded.IsActive).ShouldBe(("2026-10", true, true));
        }
        finally
        {
            await settings.RemoveAsync(PrivacyNotice.SettingKey, null, Ct);
        }

        async Task<IEnumerable<ConsentDto>> NoticesAsync(PatientCreated patient) =>
            (await frontOffice.GetAsync<List<ConsentDto>>($"/api/v1/patients/{patient.Id}/consents")).Where(c => c.Type == ConsentType.PrivacyNotice);
    }

    [Fact]
    public async Task A_default_grant_a_role_no_longer_has_is_taken_back_but_a_grant_an_administrator_gave_stays()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var cssd = await db.Set<Role>().Include(r => r.Permissions).SingleAsync(r => r.Name == SystemRoles.CssdTechnician, Ct);
        var hr = await db.Set<Role>().Include(r => r.Permissions).SingleAsync(r => r.Name == SystemRoles.HrManager, Ct);

        // As an installation seeded before patients.read left the CSSD technician's defaults; and the HR manager given visits.read by hand.
        cssd.Permissions.Add(new RolePermission { RoleId = cssd.Id, Permission = PatientsPermissionsRead });
        db.Add(new SeededGrant { RoleName = cssd.Name, Permission = PatientsPermissionsRead, SeededAt = DateTimeOffset.UtcNow.AddYears(-1) });
        hr.Permissions.Add(new RolePermission { RoleId = hr.Id, Permission = VisitsRead });
        await db.SaveChangesAsync(Ct);
        try
        {
            await scope.ServiceProvider.GetServices<ISeeder>().OfType<IdentitySeeder>().Single().SeedAsync(db, Ct);

            db.ChangeTracker.Clear();
            var permissions = await db.Set<RolePermission>().AsNoTracking()
                .Where(p => (p.RoleId == cssd.Id && p.Permission == PatientsPermissionsRead) || (p.RoleId == hr.Id && p.Permission == VisitsRead))
                .Select(p => p.RoleId).ToListAsync(Ct);
            permissions.ShouldBe([hr.Id], "The seeded grant is withdrawn; the one given by hand is not.");
            (await db.Set<SeededGrant>().AnyAsync(g => g.RoleName == cssd.Name && g.Permission == PatientsPermissionsRead, Ct)).ShouldBeFalse();
        }
        finally
        {
            await db.Set<RolePermission>().Where(p => p.RoleId == hr.Id && p.Permission == VisitsRead).ExecuteDeleteAsync(Ct);
        }
    }

    private const string PatientsPermissionsRead = "patients.read";
    private const string VisitsRead = "visits.read";

    private static RegisterPatientRequest Registration(string firstName, string lastName, bool isVip = false, bool privacyNoticeGiven = false) => new(
        Title: null, FirstName: firstName, MiddleName: null, LastName: lastName, Gender: Gender.Female,
        DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(9000)), AgeYears: null, AgeMonths: null,
        Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
        GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
        EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
        Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: isVip, Notes: null,
        ConfirmedNotDuplicate: true, PrivacyNoticeGiven: privacyNoticeGiven);

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
