using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Safety;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A ward's medication order gets the prescribing safety check an e-prescription line of the same medicine gets at signing,
/// and the ward's orders are among the patient's current medicines when a doctor e-prescribes. A patient with anaphylaxis
/// to penicillins is not started on amoxicillin and clavulanic acid on the ward unless the doctor gives the clinical reason,
/// which is audited at Critical; and an aspirin e-prescription for a patient on a heparin ward order raises the interaction.
/// </summary>
public sealed class WardOrderSafetyTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_ward_order_a_blocking_alert_stops_is_written_only_with_a_reason_audited_at_critical()
    {
        var (doctor, nurse, patientId, admission) = await WardAsync();
        await nurse.PostAsync<JsonElement>($"/api/v1/emr/patients/{patientId}/allergies",
            new RecordAllergyRequest("Penicillins", AllergyType.Drug, "Anaphylaxis after an injection in 2019", AllergySeverity.Anaphylaxis, null, null));
        var orders = $"/api/v1/nursing/admissions/{admission.Id}/medication-orders";
        var augmentin = new AddMedicationOrderRequest(
            DrugItemId: null, DrugName: "Tab. Amoxicillin + Clavulanic acid 625 mg", GenericName: "Amoxicillin + Clavulanic acid", Strength: "500 mg + 125 mg",
            Dose: "1 tablet", Route: "Oral", Frequency: "BD", DurationDays: 5, StartAt: null, Instructions: null, IsControlledDrug: false,
            OrderedByPractitionerId: null);

        // The order form's check shows the allergy as the order would meet it.
        var check = await doctor.PostAsync<MedicationSafetyResult>($"{orders}/check", augmentin);
        check.IsBlocked.ShouldBeTrue();
        var allergy = check.Alerts.Single(a => a.IsBlocking);
        (allergy.Kind, allergy.Severity).ShouldBe(("ClassAllergy", "Major"));
        allergy.Message.ShouldContain("ANAPHYLAXIS");

        // Written without a reason, it is refused with every alert, and nothing goes on the chart.
        using (var refused = await doctor.SendRawAsync(HttpMethod.Post, orders, augmentin))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync(Ct));
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("code").GetString().ShouldBe("medication.safety_block");
            problem.GetProperty("data").EnumerateArray()
                .ShouldContain(a => a.GetProperty("kind").GetString() == "ClassAllergy" && a.GetProperty("isBlocking").GetBoolean());
        }

        (await nurse.GetAsync<List<MedicationOrderDto>>(orders)).ShouldBeEmpty();
        using (var tooShort = await doctor.SendRawAsync(HttpMethod.Post, orders, augmentin with { OverrideReason = "Tolerated" }))
        {
            tooShort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        // With the clinical reason it is written, and the override is in the audit trail at Critical, with what was found.
        const string reason = "Skin test negative today; given in the ICU with adrenaline at the bedside";
        var order = await doctor.PostAsync<MedicationOrderDto>(orders, augmentin with { OverrideReason = reason });
        order.Status.ShouldBe(MedicationOrderStatus.Active);
        var audit = await AuditAsync("medication.safety_override", order.Id);
        (audit.Severity, audit.PatientId).ShouldBe((AuditSeverity.Critical, (Guid?)patientId));
        audit.Summary.ShouldNotBeNull().ShouldContain(reason);
        audit.Details.ShouldNotBeNull().ShouldContain("ClassAllergy");
    }

    [Fact]
    public async Task An_aspirin_e_prescription_for_a_patient_on_a_heparin_ward_order_raises_the_interaction()
    {
        var (doctor, _, patientId, admission) = await WardAsync();

        // The hospital's table holds heparin with aspirin as a major interaction (added here when it does not yet).
        using (var added = await doctor.SendRawAsync(HttpMethod.Post, "/api/v1/clinical/drug-interactions", new SaveDrugInteractionRequest(
                   "heparin", "aspirin", InteractionSeverity.Major, "Additive bleeding risk: an anticoagulant with an antiplatelet.",
                   "Avoid unless both are indicated; watch for bleeding.", IsActive: true, Version: null)))
        {
            added.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK, HttpStatusCode.Conflict);
        }

        // Heparin on the ward chart, the doctor's own order.
        await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders", new AddMedicationOrderRequest(
            DrugItemId: null, DrugName: "Inj. Heparin 5000 IU/ml", GenericName: "Heparin", Strength: "5000 IU/ml", Dose: "5000 IU", Route: "SC", Frequency: "BD",
            DurationDays: 5, StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: null));

        // The next e-prescription, of aspirin, finds the heparin among the patient's current medicines.
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId, EncounterId: null, VisitId: null, AdmissionId: admission.Id, IsInpatient: true, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    null, "Tab. Ecosprin 75 mg", "Aspirin", "75 mg", "Tablet", Route: "Oral", Dose: "1 tablet", DoseFrequency.OD,
                    CustomFrequency: null, DosesPerDay: null, 5, Quantity: 5, Instructions: null, IsControlledDrug: false, ControlledSchedule: null),
            ]));
        var check = await doctor.PostAsync<SafetyCheckResponse>($"/api/v1/clinical/prescriptions/{draft.Id}/check");
        var interaction = check.Alerts.Single(a => a.Kind == SafetyAlertKind.Interaction);
        interaction.IsBlocking.ShouldBeTrue();
        interaction.Message.ShouldContain("Inj. Heparin 5000 IU/ml (already prescribed on the ward chart)");

        // It is not signed without the doctor's clinical reason.
        using var unsigned = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version));
        unsigned.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await unsigned.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("prescription.safety_block");
    }

    // ------------------------------------------------------------------ steps

    /// <summary>A new patient admitted under the doctor to a bed of the semi-private ward, with the ward's nurse.</summary>
    private async Task<(ApiClient Doctor, ApiClient Nurse, Guid PatientId, AdmissionCreated Admission)> WardAsync()
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
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        return (doctor, nurse, patient.Id, admission);
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.Action == action && a.EntityId == entityId.ToString(), Ct);
    }
}
