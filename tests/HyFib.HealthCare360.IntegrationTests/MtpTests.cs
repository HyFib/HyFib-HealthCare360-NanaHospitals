using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Mtp;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Mtp;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Termination of pregnancy over the real API. A case is opened only at an approved place, with the woman's consent and
/// the opinion of as many doctors as the length of the pregnancy calls for; it puts her record in the MTP category, opens
/// to no one without the MTP permissions, and once done goes into the admission register and the month's report.
/// </summary>
public sealed class MtpTests(HospitalServer server)
{
    private const string Approval = "MTP/TN/MDU/2026/007";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_termination_is_certified_done_and_registered_and_its_case_stays_confidential()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterWomanAsync(admin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

        // A place without the approval of Form B records no termination.
        (await RefusalAsync(admin, HttpMethod.Post, "/api/v1/mtp/cases", Case(patient.Id, today), HttpStatusCode.UnprocessableEntity))
            .ShouldBe("mtp.not_approved");

        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(MtpService.ApprovalSetting, Approval, facilityId, false, Ct);
        try
        {
            // At 22 weeks one doctor's opinion is not enough.
            using (var oneDoctor = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/mtp/cases",
                       Case(patient.Id, today) with
                       {
                           WeeksOfGestation = 22,
                           Grounds = [MtpGround.InjuryToMentalHealth],
                           Category = MtpRule3BCategory.ChangeOfMaritalStatus,
                       }))
            {
                oneDoctor.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                (await oneDoctor.Content.ReadAsStringAsync(Ct)).ShouldContain("two doctors");
            }

            var opened = await admin.PostAsync<MtpCaseDto>("/api/v1/mtp/cases", Case(patient.Id, today));
            (opened.Status, opened.ApprovalNumber, opened.WeeksOfGestation).ShouldBe((MtpCaseStatus.Certified, Approval, 9));
            opened.SerialNumber.ShouldNotBeNullOrWhiteSpace();

            // Opening it put her record in the MTP category: her chart closes to the doctor and the nurse.
            (await admin.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patient.Id}/sensitive-categories")).Categories.ShouldBe([SensitiveCategory.Mtp]);
            using (var chart = await nurse.SendRawAsync(HttpMethod.Get, $"/api/v1/emr/patients/{patient.Id}/diagnoses"))
            {
                chart.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            // No clinical role holds the MTP permissions by default; medical records reads the case and the register.
            foreach (var outsider in new[] { doctor, nurse })
            {
                using var refused = await outsider.SendRawAsync(HttpMethod.Get, $"/api/v1/mtp/cases/{opened.Id}");
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            (await mrd.GetAsync<MtpCaseDto>($"/api/v1/mtp/cases/{opened.Id}")).SerialNumber.ShouldBe(opened.SerialNumber);
            using (var notMrds = await mrd.SendRawAsync(HttpMethod.Post, $"/api/v1/mtp/cases/{opened.Id}/cancel", new CancelMtpCaseRequest("Wrong", opened.Version)))
            {
                notMrds.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            // The termination is recorded, and then the case cannot change.
            var done = await admin.PostAsync<MtpCaseDto>($"/api/v1/mtp/cases/{opened.Id}/termination",
                new RecordMtpTerminationRequest(today, MtpMethod.Medical, "Dr. Meera Krishnan", today, "Complete abortion, discharged well", null, opened.Version));
            (done.Status, done.TerminatedOn, done.Method).ShouldBe((MtpCaseStatus.Terminated, (DateOnly?)today, (MtpMethod?)MtpMethod.Medical));
            (await RefusalAsync(admin, HttpMethod.Put, $"/api/v1/mtp/cases/{opened.Id}", Case(patient.Id, today) with { Version = done.Version },
                HttpStatusCode.UnprocessableEntity)).ShouldBe("mtp.terminated");

            // The month's register, its export, the report to the Chief Medical Officer and the printed forms carry it.
            var register = await mrd.GetAsync<MtpRegister>($"/api/v1/mtp/register?year={today.Year}&month={today.Month}");
            register.ApprovalNumber.ShouldBe(Approval);
            var row = register.Rows.Single(r => r.SerialNumber == opened.SerialNumber);
            (row.Uhid, row.WeeksOfGestation, row.PerformedBy).ShouldBe((patient.Uhid, 9, "Dr. Meera Krishnan"));
            row.OpinionBy.ShouldContain("TNMC 45120");
            using (var export = await mrd.SendRawAsync(HttpMethod.Get, $"/api/v1/mtp/register/export?year={today.Year}&month={today.Month}"))
            {
                export.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await export.Content.ReadAsStringAsync(Ct)).ShouldContain(opened.SerialNumber);
            }

            var report = await mrd.GetAsync<MtpMonthlyReport>($"/api/v1/mtp/monthly-report?year={today.Year}&month={today.Month}");
            report.Total.ShouldBeGreaterThanOrEqualTo(1);
            report.ByGestation.Single(c => c.Label == "Up to 12 weeks").Count.ShouldBeGreaterThanOrEqualTo(1);
            report.ByGestation.Sum(c => c.Count).ShouldBe(report.Total);

            var printed = await mrd.GetAsync<PrintableDocumentDto>($"/api/v1/mtp/cases/{opened.Id}/document");
            printed.Html.ShouldContain("Form C");
            printed.Html.ShouldContain("Form I");
            printed.Html.ShouldContain(Approval);
            printed.Html.ShouldContain("CONFIDENTIAL");
        }
        finally
        {
            await settings.RemoveAsync(MtpService.ApprovalSetting, facilityId, Ct);
        }
    }

    [Fact]
    public async Task A_minors_termination_needs_her_guardians_consent_and_a_cancelled_case_stays_out_of_the_register()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterWomanAsync(admin);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(MtpService.ApprovalSetting, Approval, facilityId, false, Ct);
        try
        {
            using (var ownConsent = await admin.SendRawAsync(HttpMethod.Post, "/api/v1/mtp/cases", Case(patient.Id, today) with { AgeYears = 16 }))
            {
                ownConsent.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                (await ownConsent.Content.ReadAsStringAsync(Ct)).ShouldContain("guardian");
            }

            var minor = await admin.PostAsync<MtpCaseDto>("/api/v1/mtp/cases", Case(patient.Id, today) with
            {
                AgeYears = 16,
                ConsentBy = MtpConsentBy.Guardian,
                GuardianName = "Selvi",
                GuardianRelation = "Mother",
            });
            minor.GuardianName.ShouldBe("Selvi");

            var cancelled = await admin.PostAsync<MtpCaseDto>($"/api/v1/mtp/cases/{minor.Id}/cancel", new CancelMtpCaseRequest("Withdrew consent", minor.Version));
            (cancelled.Status, cancelled.CancelReason).ShouldBe((MtpCaseStatus.Cancelled, "Withdrew consent"));
            (await admin.GetAsync<MtpRegister>($"/api/v1/mtp/register?year={today.Year}&month={today.Month}")).Rows
                .ShouldNotContain(r => r.SerialNumber == minor.SerialNumber);
        }
        finally
        {
            await settings.RemoveAsync(MtpService.ApprovalSetting, facilityId, Ct);
        }
    }

    [Fact]
    public async Task A_restricted_patients_case_shows_in_the_list_and_register_with_her_initials_only()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterWomanAsync(admin, vip: true);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(MtpService.ApprovalSetting, Approval, facilityId, false, Ct);
        try
        {
            var opened = await admin.PostAsync<MtpCaseDto>("/api/v1/mtp/cases", Case(patient.Id, today));

            // Medical records may not open a restricted record: the list and the register keep her name, UHID and address back.
            var listed = (await mrd.GetAsync<List<MtpCaseListItem>>("/api/v1/mtp/cases")).Single(c => c.Id == opened.Id);
            (listed.Uhid, listed.PatientName).ShouldBe(("Restricted", PatientNames.Restricted(opened.PatientName)));
            var row = (await mrd.GetAsync<MtpRegister>($"/api/v1/mtp/register?year={today.Year}&month={today.Month}")).Rows.Single(r => r.Id == opened.Id);
            (row.Uhid, row.Address).ShouldBe(("Restricted", "Restricted"));
            using (var closed = await mrd.SendRawAsync(HttpMethod.Get, $"/api/v1/mtp/cases/{opened.Id}"))
            {
                closed.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            // Those who may open it see her as she is.
            (await admin.GetAsync<List<MtpCaseListItem>>("/api/v1/mtp/cases")).Single(c => c.Id == opened.Id).Uhid.ShouldBe(patient.Uhid);
        }
        finally
        {
            await settings.RemoveAsync(MtpService.ApprovalSetting, facilityId, Ct);
        }
    }

    private static SaveMtpCaseRequest Case(Guid patientId, DateOnly today) => new(
        patientId, AdmissionId: null, AgeYears: 27, MentallyIll: false, MtpConsentBy.Woman, GuardianName: null, GuardianRelation: null,
        ConsentOn: today, ConsentFileId: null, WeeksOfGestation: 9, Grounds: [MtpGround.ContraceptiveFailure], Category: null,
        MedicalBoardReference: null, Opinions: [new MtpOpinion("Dr. Meera Krishnan", "TNMC 45120", today)]);

    private static async Task<string?> RefusalAsync(ApiClient api, HttpMethod method, string path, object? body, HttpStatusCode status)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private static Task<PatientCreated> RegisterWomanAsync(ApiClient api, bool vip = false) =>
        api.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Anitha"), MiddleName: null,
            LastName: "Mtp" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1999, 3, 1).AddDays(Random.Shared.Next(300)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture), AlternatePhone: null,
            Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: "Hindu", Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: vip, Notes: null,
            ConfirmedNotDuplicate: true));
}
