using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A stopped medicine is not given. The doctor stops ceftriaxone at 09:20 for a rash while its 09:00 dose waits, 40 minutes
/// late at 09:40: the stop cancels that dose as well as those to come, so the ward's round no longer lists it, the screen
/// that still shows it does not record it as given, and no extra dose of the stopped order is given. A prescription
/// cancelled cancels its doses still due in the same way.
/// </summary>
public sealed class MedicationStopTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_stop_cancels_an_overdue_dose_and_no_dose_of_the_stopped_order_is_given()
    {
        var (doctor, nurse, _, admission, wardId) = await WardAsync();

        // The doctor's own order, Inj. Ceftriaxone 1 g IV BD; its first dose is 40 minutes late, as the 09:00 dose is at 09:40.
        var order = await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders", new AddMedicationOrderRequest(
            DrugItemId: null, DrugName: "Inj. Ceftriaxone 1 g", GenericName: "Ceftriaxone", Strength: "1 g", Dose: "1 g", Route: "IV", Frequency: "BD",
            DurationDays: 5, StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: null));
        var late = (await DosesAsync(order.Id))[0];
        await ChangeAsync<MedicationAdministration>(d => d.Id == late.Id, d => d.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-40));
        var listed = (await RoundAsync(nurse, wardId)).Single(i => i.EntryId == late.Id);
        (listed.IsOverdue, listed.HeldBecause).ShouldBe((true, (string?)null));

        // The doctor stops it: rash, ?allergy. Every dose still due is cancelled, the late one too, and the round drops it.
        var stopped = await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/medication-orders/{order.Id}/stop",
            new StopMedicationOrderRequest("Rash, ?allergy to ceftriaxone"));
        stopped.Status.ShouldBe(MedicationOrderStatus.Stopped);
        var doses = await DosesAsync(order.Id);
        doses.ShouldNotBeEmpty();
        doses.ShouldAllBe(d => d.Status == MarStatus.Cancelled && d.Reason == "Medication order stopped");
        (await RoundAsync(nurse, wardId)).ShouldNotContain(i => i.EntryId == late.Id);

        // The screen still showing the dose does not record it as given, nor is an extra dose of the stopped order given.
        (await RefusalAsync(nurse, $"/api/v1/nursing/mar/{late.Id}/record",
            new RecordAdministrationRequest(MarStatus.Given, null, null, null, null, null, listed.Version))).ShouldBe("mar.cancelled");
        (await RefusalAsync(nurse, $"/api/v1/nursing/admissions/{admission.Id}/mar",
            new AddAdHocDoseRequest(order.Id, null, "1 g", "IV", null, GivenNow: true, null, null, null, null))).ShouldBe("medication.not_active");
        (await DosesAsync(order.Id)).ShouldAllBe(d => d.Status == MarStatus.Cancelled);
    }

    [Fact]
    public async Task A_cancelled_prescription_cancels_its_doses_still_due_an_overdue_one_too()
    {
        var (doctor, nurse, patientId, admission, _) = await WardAsync();

        // An in-patient e-prescription given from ward stock: the nurse puts its doses on the round with the schedule button.
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId, EncounterId: null, VisitId: null, AdmissionId: admission.Id, IsInpatient: true, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    null, "Tab. Paracetamol 650 mg", "Paracetamol", "650 mg", "Tablet", Route: "Oral", Dose: "650 mg", DoseFrequency.TDS,
                    CustomFrequency: null, DosesPerDay: null, 3, Quantity: 9, Instructions: null, IsControlledDrug: false, ControlledSchedule: null),
            ]));
        var signed = (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version))).Prescription;
        var order = (await nurse.GetAsync<List<MedicationOrderDto>>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders"))
            .Single(o => o.PrescriptionId == signed.Id);
        (await nurse.PostAsync<ScheduleResult>($"/api/v1/nursing/medication-orders/{order.Id}/schedule")).DosesAdded.ShouldBeGreaterThan(0);
        var late = (await DosesAsync(order.Id))[0];
        await ChangeAsync<MedicationAdministration>(d => d.Id == late.Id, d => d.ScheduledAt = DateTimeOffset.UtcNow.AddMinutes(-40));

        // The doctor cancels the prescription: its line stops and every dose still due is cancelled, the late one too.
        await doctor.PostAsync<PrescriptionDto>($"/api/v1/clinical/prescriptions/{signed.Id}/cancel", new CancelPrescriptionRequest("Changed to IV paracetamol"));
        (await nurse.GetAsync<List<MedicationOrderDto>>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders"))
            .Single(o => o.Id == order.Id).Status.ShouldBe(MedicationOrderStatus.Stopped);
        (await DosesAsync(order.Id)).ShouldAllBe(d => d.Status == MarStatus.Cancelled && d.Reason == "Prescription cancelled");
        (await RefusalAsync(nurse, $"/api/v1/nursing/mar/{late.Id}/record",
            new RecordAdministrationRequest(MarStatus.Given, null, null, null, null, null, late.Version))).ShouldBe("mar.cancelled");
    }

    // ------------------------------------------------------------------ steps

    /// <summary>A new patient admitted under the doctor to a bed of the semi-private ward, with the ward's nurse.</summary>
    private async Task<(ApiClient Doctor, ApiClient Nurse, Guid PatientId, AdmissionCreated Admission, Guid WardId)> WardAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "SPW");
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, ward.Id);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Cellulitis of the left leg", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        return (doctor, nurse, patient.Id, admission, ward.Id);
    }

    /// <summary>The ward's medication round for the next hour, overdue doses first among them.</summary>
    private static Task<List<WardMarItem>> RoundAsync(ApiClient nurse, Guid wardId) =>
        nurse.GetAsync<List<WardMarItem>>($"/api/v1/nursing/wards/{wardId}/mar/due?withinMinutes=60");

    /// <summary>The order's doses on the MAR, by the time they are due.</summary>
    private async Task<List<MedicationAdministration>> DosesAsync(Guid orderId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<MedicationAdministration>().AsNoTracking()
            .Where(d => d.MedicationOrderId == orderId)
            .OrderBy(d => d.ScheduledAt)
            .ToListAsync(Ct);
    }

    private async Task ChangeAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> which, Action<T> change) where T : class
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        change(await db.Set<T>().IgnoreQueryFilters().SingleAsync(which, Ct));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<string?> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }
}
