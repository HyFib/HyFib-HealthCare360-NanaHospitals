using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// PCPNDT Form F over the real API. An ultrasound in pregnancy is not started until the woman's details and her
/// declaration are recorded, its report is not signed until the performing doctor completes the form with the result
/// and the declaration that the sex was neither detected nor disclosed, and a completed form cannot change. The month's
/// forms make the register the Appropriate Authority receives.
/// </summary>
public sealed class FormFTests(HospitalServer server)
{
    private const string Registration = "PNDT/TN/2026/0042";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_ultrasound_in_pregnancy_is_scanned_and_reported_only_with_its_form_f()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var radiographer = await server.SignInAsync(HospitalServer.Users.Radiographer);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterWomanAsync(admin);
        var scan = (await admin.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?category=Radiology&search=RAD-USG-OBS"))
            .Single(s => s.Code == "RAD-USG-OBS");
        await admin.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Amenorrhoea 10 weeks",
            [new OrderLineRequest(scan.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));
        var studyId = (await admin.GetAsync<PagedResult<ImagingStudyListItem>>($"/api/v1/radiology/studies?patientId={patient.Id}")).Items
            .ShouldHaveSingleItem().Id;
        var study = $"/api/v1/radiology/studies/{studyId}";
        var form = $"{study}/form-f";

        var detail = await admin.GetAsync<ImagingStudyDetail>(study);
        (detail.RequiresFormF, detail.FormFStatus).ShouldBe((true, (FormFStatus?)null));
        (await admin.GetAsync<StudyFormF>(form)).Form.ShouldBeNull();
        (await RefusalAsync(admin, HttpMethod.Post, $"{study}/start", null)).ShouldBe("form_f.required");

        // A clinic records Form F only once its PCPNDT registration number is entered.
        (await RefusalAsync(radiographer, HttpMethod.Put, form, Declaration(womanDeclared: true))).ShouldBe("form_f.not_registered");
        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(FormFService.RegistrationSetting, Registration, facilityId, false, Ct);
        try
        {
            using (var undeclared = await radiographer.SendRawAsync(HttpMethod.Put, form, Declaration(womanDeclared: false)))
            {
                undeclared.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                (await undeclared.Content.ReadAsStringAsync(Ct)).ShouldContain("does not want to know the sex");
            }

            var declared = await radiographer.PutAsync<FormFDto>(form, Declaration(womanDeclared: true));
            (declared.Status, declared.RegistrationNumber, declared.Referral).ShouldBe((FormFStatus.Declared, Registration, FormFReferral.Doctor));
            declared.FormNumber.ShouldNotBeNullOrWhiteSpace();
            declared.Indications.ShouldBe([FormFIndication.IntraUterineOrEctopicPregnancy, FormFIndication.GestationalAge]);
            declared.DeclarationRecordedBy.ShouldNotBeNull();

            // Now the scan goes ahead, and the report is written; it is not signed while the form is open.
            await radiographer.PostAsync($"{study}/start");
            await radiographer.PostAsync($"{study}/acquire", new AcquireStudyRequest(false, null, null, null, null));
            var report = await admin.PutAsync<RadiologyReportDto>($"{study}/report",
                new SaveRadiologyReportRequest(null, "Transabdominal", "Amenorrhoea", null, "Single live intrauterine gestation.", "10 weeks 2 days.", false, null, null));
            var submitted = await admin.PostAsync<RadiologyReportDto>($"{study}/report/submit", new SubmitReportRequest(report.Version));
            (await RefusalAsync(admin, HttpMethod.Post, $"{study}/report/verify", new SignReportRequest(null, submitted.Version))).ShouldBe("form_f.incomplete");

            // Only the doctor who performed it completes the form, with the declaration.
            var result = new CompleteFormFRequest("Single live intrauterine gestation of 10 weeks.", "Dr. Kavitha, referring obstetrician",
                DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)), false, null, DoctorDeclared: true, declared.Version);
            using (var notTheDoctor = await radiographer.SendRawAsync(HttpMethod.Post, $"{form}/complete", result))
            {
                notTheDoctor.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var undeclaredDoctor = await admin.SendRawAsync(HttpMethod.Post, $"{form}/complete", result with { DoctorDeclared = false }))
            {
                undeclaredDoctor.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            }

            var completed = await admin.PostAsync<FormFDto>($"{form}/complete", result);
            completed.Status.ShouldBe(FormFStatus.Completed);
            completed.DoctorName.ShouldNotBeNullOrWhiteSpace();
            completed.ProcedureDate.ShouldNotBeNull();
            (await RefusalAsync(admin, HttpMethod.Put, form, Declaration(womanDeclared: true) with { Version = completed.Version }))
                .ShouldBe("form_f.completed");

            (await admin.PostAsync<RadiologyReportDto>($"{study}/report/verify", new SignReportRequest(null, submitted.Version))).Status
                .ShouldBe(DiagnosticReportStatus.Released);

            // The month's register, its export and the printed form carry it.
            var day = completed.ProcedureDate!.Value;
            var register = await admin.GetAsync<FormFRegister>($"/api/v1/radiology/form-f/register?year={day.Year}&month={day.Month}");
            register.RegistrationNumber.ShouldBe(Registration);
            var row = register.Rows.Single(r => r.FormNumber == completed.FormNumber);
            (row.Uhid, row.Status, row.WeeksOfPregnancy).ShouldBe((patient.Uhid, FormFStatus.Completed, (int?)10));
            using (var export = await admin.SendRawAsync(HttpMethod.Get, $"/api/v1/radiology/form-f/register/export?year={day.Year}&month={day.Month}"))
            {
                export.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await export.Content.ReadAsStringAsync(Ct)).ShouldContain(completed.FormNumber);
            }

            var printed = await admin.GetAsync<PrintableDocumentDto>($"{form}/document");
            printed.Html.ShouldContain("FORM F");
            printed.Html.ShouldContain(Registration);
            printed.Html.ShouldContain("neither detected nor disclosed");
            using (var forbidden = await radiographer.SendRawAsync(HttpMethod.Get, $"/api/v1/radiology/form-f/register?year={day.Year}&month={day.Month}"))
            {
                forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }
        }
        finally
        {
            await settings.RemoveAsync(FormFService.RegistrationSetting, facilityId, Ct);
        }
    }

    private static SaveFormFDeclarationRequest Declaration(bool womanDeclared) => new(
        AgeYears: 27, HusbandOrFatherName: "Suresh Kumar", Address: "12, Gandhi Street, Madurai 625001", Phone: "9876543210",
        LivingSons: 0, LivingDaughters: 1, LastMenstrualPeriod: null, WeeksOfPregnancy: 10, FormFReferral.Doctor,
        ReferringDoctorName: "Dr. Kavitha Raman", ReferringDoctorAddress: "Raman Clinic, Anna Nagar, Madurai",
        [FormFIndication.GestationalAge, FormFIndication.IntraUterineOrEctopicPregnancy], OtherIndication: null, SignedFormFileId: null,
        WomanDeclared: womanDeclared);

    private static async Task<string?> RefusalAsync(ApiClient api, HttpMethod method, string path, object? body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private static Task<PatientCreated> RegisterWomanAsync(ApiClient api) =>
        api.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: TestPatients.FirstName("Priya"), MiddleName: null,
            LastName: "Scan" + string.Concat(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26)))), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1999, 3, 1).AddDays(Random.Shared.Next(300)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Random.Shared.Next(100_000_000, 999_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture), AlternatePhone: null,
            Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
}
