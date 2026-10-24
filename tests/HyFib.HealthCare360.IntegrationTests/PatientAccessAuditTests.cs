using System.Globalization;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Every way staff see a patient is in the audit trail (EHR Standards 2016; SECURITY.md): the record, and also a search
/// (what was searched for and which patients were shown), the summary the banner shows and a lookup by UHID.
/// </summary>
public sealed class PatientAccessAuditTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_search_the_summary_and_a_UHID_lookup_are_recorded_as_access()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surname = "Audit" + new string([.. Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: TestPatients.FirstName("Meena"), MiddleName: null, LastName: surname, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(3000)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture), AlternatePhone: null, Email: null,
            Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);

        await nurse.GetAsync<PagedResult<PatientListItem>>($"/api/v1/patients?search={surname}");
        await nurse.GetAsync<JsonElement>($"/api/v1/patients/{created.Id}/summary");
        await nurse.GetAsync<JsonElement>($"/api/v1/patients/by-uhid/{created.Uhid}");

        await using var scope = server.Services.CreateAsyncScope();
        var events = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.UserName == HospitalServer.Users.Nurse && e.Category == AuditCategories.PhiAccess
                        && (e.PatientId == created.Id || (e.Action == "patient.searched" && e.Summary!.Contains(surname))))
            .OrderBy(e => e.Id).ToListAsync(Ct);

        var search = events.Where(e => e.Action == "patient.searched").ShouldHaveSingleItem();
        search.Summary.ShouldBe($"Searched patients for \"{surname}\"");
        using (var details = JsonDocument.Parse(search.Details!))
        {
            details.RootElement.GetProperty("matched").GetInt64().ShouldBe(1);
            details.RootElement.GetProperty("shown").EnumerateArray().Select(x => x.GetGuid()).ShouldBe([created.Id]);
        }

        events.Where(e => e.PatientId == created.Id).Select(e => e.Summary)
            .ShouldBe(["Viewed patient summary", $"Looked up patient by UHID {created.Uhid}"]);
    }
}
