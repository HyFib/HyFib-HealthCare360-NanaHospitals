using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Interop.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.Testing.Abdm;
using Hl7.Fhir.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// ABDM health records over the real API (EHR-08): medical records download a signed discharge summary as an NRCeS
/// FHIR document bundle, only while an administrator has ABDM health records switched on, only for a patient who
/// consented to share their records, and every export (and every refusal for consent) is in the audit trail (PRV-04).
/// The downloaded bundle validates against the NRCeS ndhm.in 6.5.0 profiles. The test turns a hospital-wide switch on, so
/// it runs with the feature switch tests, by itself (<see cref="FeatureSwitchCollection"/>), and turns it back off and
/// cancels its admission in a <c>finally</c> block.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmExportTests(HospitalServer server)
{
    private static readonly JsonSerializerOptions Fhir = new JsonSerializerOptions().ForFhir(R4.ModelInfo.ModelInspector);

    [Fact]
    public async Task A_signed_discharge_summary_leaves_as_an_abdm_record_only_when_switched_on_and_consented_and_is_audited()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var records = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var abha = "91" + Digits(12);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Meera", MiddleName: null, LastName: "Abdm" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: abha, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWF");
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patient.Id}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Emergency, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null, NotifyPatient: false));
        try
        {
            // Doctor: drafts the discharge summary.
            var draft = await doctor.PutAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/by-admission/{admission.Id}",
                new SaveDischargeSummaryRequest(
                    PatientId: patient.Id, EncounterId: null, PractitionerId: null, AdmittedAt: null, DischargedAt: null,
                    Diagnoses: "Community-acquired pneumonia, right lower lobe", DiagnosisCodes: ["J18.9"],
                    PresentingComplaints: "Fever and cough for 4 days", CourseInHospital: "IV antibiotics; afebrile from day 3.",
                    Procedures: null, InvestigationsSummary: "Chest X-ray: right lower lobe consolidation", ConditionAtDischarge: "Stable",
                    Medications: "Tab. Amoxicillin-clavulanate 625 mg BD x 5 days", Advice: "Plenty of fluids",
                    FollowUpDate: null, FollowUpInstructions: "Review in the OPD after a week", Version: null));
            var path = $"/api/v1/abdm/discharge-summaries/{draft.Id}/bundle";

            // ABDM health records are off until an administrator turns them on: 503 feature.disabled.
            using (var off = await records.SendRawAsync(HttpMethod.Get, path))
            {
                off.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
                var problem = await ProblemAsync(off);
                problem.GetProperty("code").GetString().ShouldBe(FeatureProblems.Code);
                problem.GetProperty("data").GetProperty("feature").GetString().ShouldBe("abdm");
            }

            await SwitchAsync(admin, enabled: true);

            // A doctor may write the summary but not export it (abdm.export: hospital administrators and medical records).
            using (var notPermitted = await doctor.SendRawAsync(HttpMethod.Get, path))
            {
                notPermitted.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            // No record-sharing consent: refused, and the refusal is a security event in the audit trail.
            using (var noConsent = await records.SendRawAsync(HttpMethod.Get, path))
            {
                noConsent.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                (await ProblemAsync(noConsent)).GetProperty("code").GetString().ShouldBe("abdm.consent_required");
            }

            var refusal = (await AuditAsync(AbdmAuditTrail.Refused, draft.Id)).ShouldHaveSingleItem();
            (refusal.Category, refusal.Severity, refusal.PatientId).ShouldBe((AuditCategories.Security, AuditSeverity.Warning, patient.Id));

            // The patient agrees to share their records. A draft still does not leave the hospital.
            var consent = await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient.Id}/consents",
                new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, "Agreed to share with other providers"));
            using (var unsigned = await records.SendRawAsync(HttpMethod.Get, path))
            {
                unsigned.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await ProblemAsync(unsigned)).GetProperty("code").GetString().ShouldBe("abdm.not_signed");
            }

            await doctor.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version));

            // Medical records: download the ABDM record.
            R4.Bundle bundle;
            using (var download = await records.SendRawAsync(HttpMethod.Get, path))
            {
                download.StatusCode.ShouldBe(HttpStatusCode.OK, await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                download.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/fhir+json");
                var disposition = download.Content.Headers.ContentDisposition.ShouldNotBeNull();
                disposition.DispositionType.ShouldBe("attachment");
                (disposition.FileNameStar ?? disposition.FileName).ShouldNotBeNull().Trim('"').ShouldStartWith($"discharge-summary-{patient.Uhid}-");
                download.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
                bundle = JsonSerializer.Deserialize<R4.Bundle>(await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), Fhir)
                    .ShouldNotBeNull();
            }

            // It validates against the NRCeS ndhm.in 6.5.0 profiles, with no error or warning.
            var validation = AbdmProfileValidator.Validate(bundle, AbdmProfileValidator.DocumentBundle);
            validation.Errors.ShouldBeEmpty(validation.Describe());
            validation.Warnings.ShouldBeEmpty(validation.Describe());

            // ... and is the summary: the patient with the ABHA number, the ICD-10 diagnosis, the doctor and the hospital.
            var resources = bundle.Entry.Select(e => e.Resource).ToList();
            var composition = resources[0].ShouldBeOfType<R4.Composition>();
            composition.Id.ShouldBe(draft.Id.ToString("D"));
            composition.Status.ShouldBe(R4.CompositionStatus.Final);
            var person = resources.OfType<R4.Patient>().ShouldHaveSingleItem();
            var formatted = $"{abha[..2]}-{abha[2..6]}-{abha[6..10]}-{abha[10..]}";
            person.Identifier.ShouldContain(i => i.System == "https://healthid.ndhm.gov.in" && i.Value == formatted);
            person.Identifier.ShouldContain(i => i.Value == patient.Uhid);
            var diagnosis = resources.OfType<R4.Condition>().ShouldHaveSingleItem().Code.ShouldNotBeNull().Coding.ShouldHaveSingleItem();
            (diagnosis.System, diagnosis.Code, diagnosis.Display).ShouldBe(("http://hl7.org/fhir/sid/icd-10", "J18.9", "Pneumonia, unspecified"));
            resources.OfType<R4.Practitioner>().ShouldHaveSingleItem().Id.ShouldBe(consultantId.ToString("D"));
            resources.OfType<R4.Organization>().ShouldHaveSingleItem();
            resources.OfType<R4.Encounter>().ShouldHaveSingleItem().Identifier.ShouldContain(i => i.Value == admission.AdmissionNumber);

            // The export is in the patient's record access history (PRV-04), naming the document.
            var exported = (await AuditAsync(AbdmAuditTrail.Exported, draft.Id)).ShouldHaveSingleItem();
            (exported.Category, exported.PatientId, exported.EntityType).ShouldBe((AuditCategories.PhiAccess, patient.Id, "DischargeSummary"));
            exported.UserName.ShouldBe(HospitalServer.Users.MrdOfficer);
            exported.Details.ShouldNotBeNull().ShouldContain(bundle.Identifier.ShouldNotBeNull().Value.ShouldNotBeNull());

            // Withdrawing the consent stops sharing at once.
            await frontOffice.PostAsync($"/api/v1/patients/consents/{consent.Id}/revoke", new RevokeConsentRequest("No longer wants records shared"));
            using (var withdrawn = await records.SendRawAsync(HttpMethod.Get, path))
            {
                withdrawn.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            (await AuditAsync(AbdmAuditTrail.Exported, draft.Id)).Count.ShouldBe(1, "a refused export exports nothing");
        }
        finally
        {
            await SwitchAsync(admin, enabled: false);
            await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of ABDM exports"));
        }
    }

    /// <summary>Turns ABDM health records on or off hospital-wide, as the Features screen does.</summary>
    private static async Task SwitchAsync(ApiClient admin, bool enabled) =>
        (await admin.PutAsync<FeatureDto>($"/api/v1/admin/features/{FeatureKeys.Abdm}", new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);

    private async Task<List<AuditEvent>> AuditAsync(string action, Guid summaryId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var id = summaryId.ToString("D");
        return await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Action == action && a.EntityId == id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/problem+json");
        return await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, TestContext.Current.CancellationToken);
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Created(Guid Id);
}
