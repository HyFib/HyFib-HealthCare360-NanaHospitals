using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Radiology;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A pregnancy ultrasound cancelled after the scan (maker-checker, slice 9; PCPNDT Act), over the real API and PostgreSQL:
/// once the scan has begun the study is not cancelled, from the radiology desk or by the clinician withdrawing the order,
/// so its Form F is never closed as cancelled; it is completed with the result. Each refusal is audited at Warning.
/// </summary>
public sealed class FormFCancellationTests(HospitalServer server)
{
    private const string Registration = "PNDT/TN/2026/0042";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_pregnancy_ultrasound_once_scanned_is_not_cancelled_and_its_form_f_is_completed()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var radiographer = await server.SignInAsync(HospitalServer.Users.Radiographer);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterWomanAsync(admin);
        var (studyId, _) = await OrderScanAsync(admin, patient);
        var study = $"/api/v1/radiology/studies/{studyId}";

        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(FormFService.RegistrationSetting, Registration, facilityId, false, Ct);
        try
        {
            var declared = await radiographer.PutAsync<FormFDto>($"{study}/form-f", Declaration());

            // The scan begins: from now on the study is not cancelled, and the refusal is on the record.
            await radiographer.PostAsync($"{study}/start");
            (await RefusalAsync(radiographer, HttpMethod.Post, $"{study}/cancel", new CancelStudyRequest("Patient felt unwell"))).ShouldBe("form_f.scan_performed");
            (await admin.GetAsync<ImagingStudyDetail>(study)).Status.ShouldBe(ImagingStudyStatus.InProgress);
            (await AuditAsync(patient.Id, "form_f.cancel_refused")).ShouldContain("Patient felt unwell");

            await radiographer.PostAsync($"{study}/acquire", new AcquireStudyRequest(false, null, null, null, null));
            (await RefusalAsync(admin, HttpMethod.Post, $"{study}/cancel", new CancelStudyRequest("Ordered in error"))).ShouldBe("form_f.scan_performed");
            var form = (await admin.GetAsync<StudyFormF>($"{study}/form-f")).Form.ShouldNotBeNull();
            (form.Status, form.CancelReason).ShouldBe((FormFStatus.Declared, (string?)null));

            // The form is completed with the result, as the Act asks.
            var completed = await admin.PostAsync<FormFDto>($"{study}/form-f/complete", new CompleteFormFRequest(
                "Single live intrauterine gestation of 10 weeks.", "The woman herself", DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5)), false, null,
                DoctorDeclared: true, declared.Version));
            completed.Status.ShouldBe(FormFStatus.Completed);
        }
        finally
        {
            await settings.RemoveAsync(FormFService.RegistrationSetting, facilityId, Ct);
        }
    }

    [Fact]
    public async Task The_clinician_withdrawing_the_order_after_the_scan_leaves_the_study_and_its_form_open_to_be_completed()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var radiographer = await server.SignInAsync(HospitalServer.Users.Radiographer);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterWomanAsync(admin);
        var (studyId, itemId) = await OrderScanAsync(admin, patient);
        var study = $"/api/v1/radiology/studies/{studyId}";

        await using var scope = server.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        await settings.SetAsync(FormFService.RegistrationSetting, Registration, facilityId, false, Ct);
        try
        {
            await radiographer.PutAsync<FormFDto>($"{study}/form-f", Declaration());
            await radiographer.PostAsync($"{study}/start");
            await radiographer.PostAsync($"{study}/acquire", new AcquireStudyRequest(false, null, null, null, null));

            await admin.PostAsync<OrderDto>($"/api/v1/clinical/orders/items/{itemId}/cancel", new CancelOrderRequest("Duplicate order"));

            (await admin.GetAsync<ImagingStudyDetail>(study)).Status.ShouldBe(ImagingStudyStatus.Acquired);
            (await admin.GetAsync<StudyFormF>($"{study}/form-f")).Form.ShouldNotBeNull().Status.ShouldBe(FormFStatus.Declared);
            (await AuditAsync(patient.Id, "form_f.cancel_refused")).ShouldContain("withdrew the order");
        }
        finally
        {
            await settings.RemoveAsync(FormFService.RegistrationSetting, facilityId, Ct);
        }
    }

    private static async Task<(Guid StudyId, Guid ItemId)> OrderScanAsync(ApiClient admin, PatientCreated patient)
    {
        var scan = (await admin.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?category=Radiology&search=RAD-USG-OBS"))
            .Single(s => s.Code == "RAD-USG-OBS");
        var order = await admin.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, EncounterId: null, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Amenorrhoea 10 weeks",
            [new OrderLineRequest(scan.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));
        var studyId = (await admin.GetAsync<PagedResult<ImagingStudyListItem>>($"/api/v1/radiology/studies?patientId={patient.Id}")).Items
            .ShouldHaveSingleItem().Id;
        return (studyId, order.Items.ShouldHaveSingleItem().Id);
    }

    private static SaveFormFDeclarationRequest Declaration() => new(
        AgeYears: 27, HusbandOrFatherName: "Suresh Kumar", Address: "12, Gandhi Street, Madurai 625001", Phone: "9876543210",
        LivingSons: 0, LivingDaughters: 1, LastMenstrualPeriod: null, WeeksOfPregnancy: 10, FormFReferral.Self,
        ReferringDoctorName: null, ReferringDoctorAddress: null, [FormFIndication.GestationalAge], OtherIndication: null, SignedFormFileId: null,
        WomanDeclared: true);

    private async Task<string> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId && e.Action == action)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => e.Summary ?? string.Empty)
            .FirstAsync(Ct);
    }

    private static async Task<string?> RefusalAsync(ApiClient api, HttpMethod method, string path, object? body)
    {
        using var response = await api.SendRawAsync(method, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    private static Task<PatientCreated> RegisterWomanAsync(ApiClient api) => MtpSecondOpinionTests.RegisterWomanAsync(api, "Scan");
}
