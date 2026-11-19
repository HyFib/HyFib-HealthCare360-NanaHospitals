using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The visit lists and the OPD queue follow the restricted-record rules (SECURITY.md, Restricted records), as patient search
/// and the doctor's worklist do. Twenty-four roles read visits; only those who may open restricted records, or have
/// break-the-glass access to the one patient, see a restricted (VIP) patient's name there. The queue and a visit's details
/// leave out the complaint of a chart closed to the user, a restricted one or one in a sensitive category they do not hold.
/// A visit, the patient's visit history and payer open as the patient's record does, and are recorded as access to it;
/// the visit list and its search are recorded as a patient search is.
/// </summary>
public sealed class VisitAndQueuePrivacyTests(HospitalServer server)
{
    private const string VipComplaint = "Chest pain on exertion";
    private const string HivComplaint = "Weight loss and night sweats";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_desk_finds_a_restricted_patients_visit_by_surname_but_sees_only_initials_and_the_search_is_recorded()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var lastName = "Veiled" + Letters(8);
        var patient = await RegisterAsync(frontOffice, "Rajiv", lastName, isVip: true);
        var visit = await CheckInAsync(frontOffice, patient.Id, VipComplaint);
        try
        {
            var search = $"/api/v1/visits?search={lastName}";
            var masked = (await frontOffice.GetAsync<PagedResult<VisitListItem>>(search)).Items.ShouldHaveSingleItem();
            (masked.Id, masked.Uhid).ShouldBe((visit.Id, patient.Uhid), "the visit is found, and the UHID still says whom to serve");
            masked.PatientName.ShouldBe($"R. {char.ToUpperInvariant(lastName[0])}. (restricted)");
            (await frontOffice.GetAsync<PagedResult<VisitListItem>>("/api/v1/visits?pageSize=200")).Items.Single(v => v.Id == visit.Id)
                .PatientName.ShouldBe(masked.PatientName, "today's list names him alike");

            // What was searched for, and whom it showed, is in the trail, as for a patient search.
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var searched = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
                    .Where(e => e.UserName == HospitalServer.Users.FrontOffice && e.Category == AuditCategories.PhiAccess
                                && e.Action == "visit.searched" && e.Summary!.Contains(lastName))
                    .ToListAsync(Ct);
                var entry = searched.ShouldHaveSingleItem();
                entry.Summary.ShouldBe($"Searched visits for \"{lastName}\"");
                using var details = JsonDocument.Parse(entry.Details!);
                details.RootElement.GetProperty("matched").GetInt64().ShouldBe(1);
                details.RootElement.GetProperty("shown").EnumerateArray().Select(x => x.GetGuid()).ShouldBe([patient.Id]);
            }

            // The visit, his visit history and his payer open as his record does: not to the desk.
            foreach (var path in new[] { $"/api/v1/visits/{visit.Id}", $"/api/v1/patients/{patient.Id}/visits", $"/api/v1/patients/{patient.Id}/payer" })
            {
                using var refused = await frontOffice.SendRawAsync(HttpMethod.Get, path);
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
                (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("patient.restricted", path);
            }

            // The hospital administrator may open restricted records: the name, the visit and its complaint, each opening
            // recorded as access to his record.
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            (await admin.GetAsync<PagedResult<VisitListItem>>(search)).Items.ShouldHaveSingleItem().PatientName.ShouldBe(patient.FullName);
            (await admin.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).ChiefComplaint.ShouldBe(VipComplaint);
            (await admin.GetAsync<PagedResult<VisitListItem>>($"/api/v1/patients/{patient.Id}/visits")).Items.ShouldHaveSingleItem().Id.ShouldBe(visit.Id);
            (await admin.GetAsync<PatientPayerResponse>($"/api/v1/patients/{patient.Id}/payer")).Current.ShouldNotBeNull().VisitId.ShouldBe(visit.Id);
            (await AccessedAsync(HospitalServer.Users.HospitalAdmin, patient.Id))
                .ShouldBe([$"Viewed visit {visit.VisitNumber}", "Viewed visit history", "Viewed the patient's payer"]);

            // Break-the-glass opens his record to the nurse, and with it his visits.
            var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
            (await nurse.GetAsync<PagedResult<VisitListItem>>(search)).Items.ShouldHaveSingleItem().PatientName.ShouldBe(masked.PatientName);
            await nurse.PostAsync<BreakGlassGrant>($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Collapsed in the waiting hall, needs the history"));
            (await nurse.GetAsync<PagedResult<VisitListItem>>(search)).Items.ShouldHaveSingleItem().PatientName.ShouldBe(patient.FullName);
            (await nurse.GetAsync<VisitDetail>($"/api/v1/visits/{visit.Id}")).ChiefComplaint.ShouldBe(VipComplaint);
            (await nurse.GetAsync<PagedResult<VisitListItem>>($"/api/v1/patients/{patient.Id}/visits")).Items.ShouldHaveSingleItem();
        }
        finally
        {
            await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/complete");
        }
    }

    [Fact]
    public async Task The_queue_hides_a_restricted_patients_name_and_a_confidential_patients_complaint_from_those_who_may_not_open_them()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var vip = await RegisterAsync(frontOffice, "Rajiv", "Veiled" + Letters(8), isVip: true);
        var hiv = await RegisterAsync(frontOffice, "Murugan", "Private" + Letters(8), isVip: false);
        await PutInHivCategoryAsync(hiv.Id);
        var vipVisit = await CheckInAsync(frontOffice, vip.Id, VipComplaint);
        var hivVisit = await CheckInAsync(frontOffice, hiv.Id, HivComplaint, notes: "Came with his brother");
        try
        {
            var queue = $"/api/v1/queue?departmentId={vipVisit.DepartmentId}";

            // The desk calls the restricted patient by token and initials, and reads neither complaint: the HIV patient keeps
            // his name, as on the bed board.
            var atDesk = await frontOffice.GetAsync<List<QueueEntry>>(queue);
            atDesk.Single(e => e.PatientId == vip.Id).ShouldSatisfyAllConditions(
                e => e.PatientName.ShouldBe($"R. {vip.LastName[0]}. (restricted)"),
                e => e.ChiefComplaint.ShouldBeNull(),
                e => e.Uhid.ShouldBe(vip.Uhid, "the UHID and the token still say whom to call"),
                e => e.Label.ShouldBe(vipVisit.Token.ShouldNotBeNull().Label));
            atDesk.Single(e => e.PatientId == hiv.Id).ShouldSatisfyAllConditions(
                e => e.PatientName.ShouldBe(hiv.FullName),
                e => e.ChiefComplaint.ShouldBeNull());

            // Doctors hold the HIV category, so read his complaint; the restricted patient stays veiled to them.
            var doctors = await doctor.GetAsync<List<QueueEntry>>(queue);
            doctors.Single(e => e.PatientId == hiv.Id).ChiefComplaint.ShouldBe(HivComplaint);
            doctors.Single(e => e.PatientId == vip.Id).ShouldSatisfyAllConditions(
                e => e.PatientName.ShouldEndWith("(restricted)"),
                e => e.ChiefComplaint.ShouldBeNull());

            // The hospital administrator may open restricted records: everything shows.
            var admins = await (await server.SignInAsync(HospitalServer.Users.HospitalAdmin)).GetAsync<List<QueueEntry>>(queue);
            admins.Single(e => e.PatientId == vip.Id).ShouldSatisfyAllConditions(
                e => e.PatientName.ShouldBe(vip.FullName),
                e => e.ChiefComplaint.ShouldBe(VipComplaint));
            admins.Single(e => e.PatientId == hiv.Id).ChiefComplaint.ShouldBe(HivComplaint);

            // Called to the room, he shows on the waiting-area board by his initials alone: nobody there may open his record.
            var token = vipVisit.Token.ShouldNotBeNull();
            await doctor.PostAsync<QueueTokenDto>($"/api/v1/queue/{token.Id}/call", new CallTokenRequest("Room 7"));
            var board = await frontOffice.GetAsync<List<DisplayDepartment>>($"/api/v1/queue/display?departmentIds={vipVisit.DepartmentId}");
            board.Single(d => d.DepartmentId == vipVisit.DepartmentId).NowServing.Single(t => t.Label == token.Label)
                .MaskedName.ShouldBe($"R. {vip.LastName[0]}.");

            // His visit opens to the desk, which serves him, without the complaint and the notes; the doctor reads them.
            var deskVisit = await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{hivVisit.Id}");
            (deskVisit.VisitNumber, deskVisit.ChiefComplaint, deskVisit.Notes).ShouldBe((hivVisit.VisitNumber, (string?)null, (string?)null));
            var doctorsVisit = await doctor.GetAsync<VisitDetail>($"/api/v1/visits/{hivVisit.Id}");
            (doctorsVisit.ChiefComplaint, doctorsVisit.Notes).ShouldBe((HivComplaint, "Came with his brother"));

            // Break-the-glass opens the restricted patient's record to the nurse: the queue then names him, with his complaint.
            var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
            (await nurse.GetAsync<List<QueueEntry>>(queue)).Single(e => e.PatientId == vip.Id).ChiefComplaint.ShouldBeNull();
            await nurse.PostAsync<BreakGlassGrant>($"/api/v1/patients/{vip.Id}/break-glass", new BreakGlassRequest("Breathless in the queue, needs the history"));
            var opened = (await nurse.GetAsync<List<QueueEntry>>(queue)).Single(e => e.PatientId == vip.Id);
            (opened.PatientName, opened.ChiefComplaint).ShouldBe((vip.FullName, VipComplaint));
        }
        finally
        {
            await frontOffice.PostAsync($"/api/v1/visits/{vipVisit.Id}/complete");
            await frontOffice.PostAsync($"/api/v1/visits/{hivVisit.Id}/complete");
        }
    }

    internal sealed record Registered(Guid Id, string Uhid, string FullName, string LastName);

    internal static async Task<Registered> RegisterAsync(ApiClient frontOffice, string firstName, string lastName, bool isVip)
    {
        var first = TestPatients.FirstName(firstName);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: first, MiddleName: null, LastName: lastName, Gender: Gender.Male,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
            Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null,
            AadhaarNumber: null, PhotoFileId: null, IsVip: isVip, Notes: null, ConfirmedNotDuplicate: false));
        return new Registered(created.Id, created.Uhid, $"{first} {lastName}", lastName);
    }

    /// <summary>An out-patient visit with the doctor's department and a queue token, as the front desk checks a patient in.</summary>
    internal async Task<VisitDetail> CheckInAsync(ApiClient frontOffice, Guid patientId, string complaint, string? notes = null)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        return await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, complaint,
            false, null, notes, TokenPriority.Normal, IssueToken: true, NotifyPatient: false));
    }

    /// <summary>The patient's record put in the HIV category, as an HIV diagnosis puts it (here by the Super Administrator).</summary>
    private async Task PutInHivCategoryAsync(Guid patientId)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var path = $"/api/v1/patients/{patientId}/sensitive-categories";
        var before = await admin.GetAsync<SensitiveCategoriesDto>(path);
        (await admin.PutAsync<SensitiveCategoriesDto>(path, new SetSensitiveCategoriesRequest([SensitiveCategory.Hiv], "HIV reactive at the ICTC", before.Version)))
            .Categories.ShouldBe([SensitiveCategory.Hiv]);
    }

    /// <summary>What the user was recorded opening of the patient's record, oldest first.</summary>
    private async Task<List<string?>> AccessedAsync(string userName, Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.UserName == userName && e.Category == AuditCategories.PhiAccess && e.PatientId == patientId && e.Action == "patient.accessed")
            .OrderBy(e => e.Id)
            .Select(e => e.Summary)
            .ToListAsync(Ct);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));
}
