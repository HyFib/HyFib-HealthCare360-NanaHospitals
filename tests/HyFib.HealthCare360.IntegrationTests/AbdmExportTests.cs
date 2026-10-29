using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Cardiology;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using HyFib.HealthCare360.Testing.Abdm;
using Hl7.Fhir.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using R4 = Hl7.Fhir.Model;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// ABDM health records over the real API (SRS EHR-08): a patient registered with an ABHA number and address; a signed
/// consultation, a signed prescription, a released lab report, a released radiology report and a signed discharge
/// summary exported as ABDM documents,
/// each refused while it is still a draft, downloaded as a named file that validates against the NRCeS profiles (Firely,
/// <see cref="AbdmProfileValidator"/>), without the patient's record-sharing consent (a staff copy, like the printout);
/// a prescription only where the hospital's DSC rule lets it issue one; and who may do it. The ABDM switch is
/// hospital-wide and off by default, so
/// these tests are in the feature-switch collection and turn it off again in a <c>finally</c> block. The in-patient
/// records come from a stay in the female general ward, which the journeys don't use, and the admission is cancelled at
/// the end, as the ward-round test does.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmExportTests(HospitalServer server)
{
    // The document types of the IG's records: the SNOMED CT code of each one's Composition.type.
    private const string PrescriptionRecord = "440545006";
    private const string DiagnosticReportRecord = "721981007";
    private const string DischargeSummaryRecord = "373942005";

    /// <summary>The CRP the lab reports, in mg/L: within the reference interval, so it needs no critical call.</summary>
    private const decimal Crp = 3.2m;
    private const string CrpLoincName = "C reactive protein [Mass/volume] in Serum or Plasma";

    /// <summary>A prescription's notes for the pharmacist and nurse: not printed for the patient, and not shared.</summary>
    private const string PharmacistNotes = "Check the patient's allergy card before dispensing";

    /// <summary>The conclusion of the ECG the cardiology tests report.</summary>
    private const string EcgConclusion = "Normal sinus rhythm. No ST-T changes.";

    /// <summary>The impression of the chest X-ray the radiology tests report.</summary>
    private const string XrayImpression = "No active lung lesion.";

    private static readonly JsonSerializerOptions Fhir = new JsonSerializerOptions().ForFhir(R4.ModelInfo.ModelInspector);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_abha_number_that_is_not_14_digits_is_refused_at_registration_and_a_valid_one_is_kept_masked_and_audited()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var abha = NewAbha();
        var wrong = abha[..13]; // a digit left out

        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/patients", Registration(wrong, "meera.k@abdm")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("abhaNumber");
        }

        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Post, "/api/v1/patients", Registration(abha, "meera.k@gmail.com")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("abhaAddress");
        }

        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(Abha.Format(abha), "Meera.K" + Letters(4) + "@ABDM"));
        var record = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");
        record.AbhaNumberMasked.ShouldBe("XX-XXXX-XXXX-" + abha[^4..]);
        record.AbhaAddress.ShouldNotBeNull().ShouldEndWith("@abdm");
        record.AbhaAddress.ShouldBe(record.AbhaAddress.ToLowerInvariant());

        var linked = await AuditAsync(patient.Id, "patient.abha_changed");
        linked.ShouldNotBeNull().Summary.ShouldBe($"At registration: ABHA number XX-XXXX-XXXX-{abha[^4..]} linked; ABHA address added");
        linked.Details.ShouldNotBeNull().ShouldNotContain(abha);
    }

    [Fact]
    public async Task An_abha_number_a_merge_brings_to_the_surviving_record_is_audited_as_linked()
    {
        // The ABHA number was given only under the duplicate UHID; the merge links it to the surviving record.
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var abha = NewAbha();
        var kept = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null) with { AbhaNumber = null });
        var duplicate = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));

        await medicalRecords.PostAsync($"/api/v1/patients/{duplicate.Id}/merge", new MergePatientRequest(kept.Id, "Registered twice"));

        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{kept.Id}")).AbhaNumberMasked.ShouldBe("XX-XXXX-XXXX-" + abha[^4..]);
        var linked = (await AuditAsync(kept.Id, "patient.abha_changed")).ShouldNotBeNull();
        linked.Summary.ShouldBe($"Merged from {duplicate.Uhid}: ABHA number XX-XXXX-XXXX-{abha[^4..]} linked");
        linked.Details.ShouldNotBeNull().ShouldNotContain(abha);

        // A surviving record with a number of its own keeps it, and nothing is linked.
        var other = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(NewAbha(), null));
        var otherDuplicate = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(NewAbha(), null));
        await medicalRecords.PostAsync($"/api/v1/patients/{otherDuplicate.Id}/merge", new MergePatientRequest(other.Id, "Registered twice"));
        (await AuditAsync(other.Id, "patient.abha_changed")).ShouldNotBeNull().Summary.ShouldStartWith("At registration:");
    }

    [Fact]
    public async Task A_record_whose_abha_address_predates_the_check_can_still_be_edited_and_a_changed_address_is_checked()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(NewAbha(), null));

        // An address saved before registration checked its format, as an old @ndhm handle could be.
        var legacy = "meera." + Letters(6) + "@ndhm";
        await using (var scope = server.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().IgnoreQueryFilters()
                .Where(p => p.Id == patient.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.AbhaAddress, legacy), Ct);
        }

        // The form sends it back with every edit: a new phone number is saved and the address is kept.
        var record = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");
        record.AbhaAddress.ShouldBe(legacy);
        var phone = "9" + Digits(9);
        await frontOffice.PutAsync($"/api/v1/patients/{patient.Id}", Edit(record, phone, legacy));
        record = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}");
        record.Phone.ShouldBe(Phone.Normalize(phone), "Phone numbers are stored in E.164.");
        record.AbhaAddress.ShouldBe(legacy);

        // A changed address must be an ABHA address.
        using (var refused = await frontOffice.SendRawAsync(HttpMethod.Put, $"/api/v1/patients/{patient.Id}", Edit(record, phone, "meera." + Letters(6) + "@ndhm")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
            problem.GetProperty("errors").GetProperty("abhaAddress")[0].GetString().ShouldBe(CommonRules.AbhaAddressMessage);
        }

        var current = "meera." + Letters(6) + "@abdm";
        await frontOffice.PutAsync($"/api/v1/patients/{patient.Id}", Edit(record, phone, current));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).AbhaAddress.ShouldBe(current);
        (await AuditAsync(patient.Id, "patient.abha_changed")).ShouldNotBeNull().Summary.ShouldBe("ABHA address changed");
    }

    [Fact]
    public async Task A_signed_consultation_is_exported_as_an_abdm_document_only_while_abdm_is_on_and_only_with_abdm_export()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var abha = NewAbha();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));
        var signed = await ConsultAsync(doctor, patient.Id, sign: true);
        var unsigned = await ConsultAsync(doctor, patient.Id, sign: false);
        var path = $"/api/v1/abdm/bundles/op-consultations/{signed}";

        // Off by default: nothing is exported, for anyone.
        using (var off = await admin.SendRawAsync(HttpMethod.Get, path))
        {
            off.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await off.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe(FeatureProblems.Code);
        }

        try
        {
            await SwitchAsync(enabled: true);

            string? version;
            using (var exported = await admin.SendRawAsync(HttpMethod.Get, path))
            {
                exported.StatusCode.ShouldBe(HttpStatusCode.OK);
                exported.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/fhir+json");
                exported.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
                var bundle = await exported.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
                bundle.GetProperty("resourceType").GetString().ShouldBe("Bundle");
                bundle.GetProperty("type").GetString().ShouldBe("document");
                bundle.GetProperty("meta").GetProperty("profile")[0].GetString().ShouldBe("https://nrces.in/ndhm/fhir/r4/StructureDefinition/DocumentBundle");
                var resources = bundle.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).ToList();
                var composition = resources[0];
                composition.GetProperty("type").GetProperty("coding")[0].GetProperty("code").GetString().ShouldBe("371530004");
                var identifiers = resources.Single(r => r.GetProperty("resourceType").GetString() == "Patient").GetProperty("identifier").EnumerateArray();
                identifiers.Single(i => i.GetProperty("system").GetString() == "https://healthid.ndhm.gov.in").GetProperty("value").GetString()
                    .ShouldBe(Abha.Format(abha));
                resources.ShouldContain(r => r.GetProperty("resourceType").GetString() == "Condition");
                version = bundle.GetProperty("meta").GetProperty("versionId").GetString();
            }

            // An allergy recorded after the note was signed is not in it: exported again, it is the same document.
            await doctor.PostAsync<AllergyDto>($"/api/v1/emr/patients/{patient.Id}/allergies",
                new RecordAllergyRequest("Sulfonamides", AllergyType.Drug, "Rash", AllergySeverity.Moderate, OnsetDate: null, Notes: null));
            var again = await ExportAsync(path, "371530004", abha, $"consultation-{patient.Uhid}-");
            again.GetProperty("meta").GetProperty("versionId").GetString().ShouldBe(version);
            Resources(again, "AllergyIntolerance").ShouldBeEmpty();

            var exportAudit = await AuditAsync(patient.Id, "patient.accessed");
            exportAudit.ShouldNotBeNull().Summary.ShouldBe("ABDM health record exported: consultation note");
            exportAudit.EntityId.ShouldBe(signed.ToString("D"));

            // A doctor may write the note but not export records for ABDM.
            using (var denied = await doctor.SendRawAsync(HttpMethod.Get, path))
            {
                denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var draft = await admin.SendRawAsync(HttpMethod.Get, $"/api/v1/abdm/bundles/op-consultations/{unsigned}"))
            {
                draft.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
                (await draft.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("abdm.not_exportable");
            }

            using (var missing = await admin.SendRawAsync(HttpMethod.Get, $"/api/v1/abdm/bundles/prescriptions/{Guid.NewGuid()}"))
            {
                missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }

            // The gateway is set up in settings but sends nothing yet. Its set-up is for those who manage integrations (the
            // super administrator), not for every hospital administrator.
            using (var notForHospitalAdmins = await admin.SendRawAsync(HttpMethod.Get, "/api/v1/abdm/gateway"))
            {
                notForHospitalAdmins.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            var superAdmin = await server.SignInAsync(HospitalServer.Users.Admin);
            var gateway = await superAdmin.GetAsync<JsonElement>("/api/v1/abdm/gateway");
            gateway.GetProperty("live").GetBoolean().ShouldBeFalse();
            gateway.GetProperty("configured").GetBoolean().ShouldBeFalse();
            gateway.GetProperty("remaining").GetArrayLength().ShouldBeGreaterThan(0);
        }
        finally
        {
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_restricted_record_is_refused_before_anything_about_it_is_told()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(NewAbha(), null) with { IsVip = true });
        await doctor.PostAsync($"/api/v1/patients/{patient.Id}/break-glass", new BreakGlassRequest("Seen in casualty, needs the history"));
        var consultation = await ConsultAsync(doctor, patient.Id, sign: false);

        // A chest X-ray of the same record, whose report is signed off but not released yet.
        var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
        var xray = await ChestXrayAsync(facilityId, patient.Id);
        await RadiologyAsync(xray, (report, study, now) => SignOff(report, study, null, now));

        // And an ECG, its report also signed off but not released.
        var ecg = await EcgAsync(facilityId, patient.Id);
        await CardiologyAsync(ecg, (report, study, now) => SignOffCardiology(report, study, null, now));

        try
        {
            await SwitchAsync(enabled: true);
            (string Path, Guid Id)[] records =
            [
                ($"/api/v1/abdm/bundles/op-consultations/{consultation}", consultation),
                ($"/api/v1/abdm/bundles/radiology-reports/{xray}", xray),
                ($"/api/v1/abdm/bundles/cardiology-reports/{ecg}", ecg),
            ];
            foreach (var (path, id) in records)
            {
                // The medical records officer exports records but does not open restricted ones: they learn nothing about
                // the record, not even that it is unsigned or unreleased.
                using (var refused = await medicalRecords.SendRawAsync(HttpMethod.Get, path))
                {
                    refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
                    (await refused.Content.ReadAsStringAsync(Ct)).ShouldNotContain("abdm.not_exportable");
                }

                // The refusal is a security event, as the FHIR API's are.
                var refusal = (await AuditAsync(patient.Id, "abdm.export_denied")).ShouldNotBeNull();
                (refusal.Category, refusal.Severity, refusal.EntityId).ShouldBe((AuditCategories.Security, AuditSeverity.Warning, id.ToString("D")));

                // Someone who may open the record is told why it cannot be exported yet.
                using var notYet = await admin.SendRawAsync(HttpMethod.Get, path);
                notYet.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, path);
                (await notYet.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("abdm.not_exportable");
            }
        }
        finally
        {
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_prescription_is_exported_as_a_prescription_record_with_a_medication_request_per_medicine_once_signed()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var abha = NewAbha();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));

        // Paracetamol for the fever and cetirizine at bedtime, written in the consultation and not signed yet.
        var consultation = await ConsultAsync(doctor, patient.Id, sign: false);
        var paracetamol = await DrugAsync(doctor, "Paracetamol", "DRG-PCM500");
        var cetirizine = await DrugAsync(doctor, "Cetirizine", "DRG-CETI10");
        var prescription = await PrescribeAsync(doctor, patient.Id, consultation,
            Tablet(paracetamol, DoseFrequency.TDS, days: 3, "After food"), Tablet(cetirizine, DoseFrequency.HS, days: 5, "May cause drowsiness"));
        var path = $"/api/v1/abdm/bundles/prescriptions/{prescription.Id}";

        try
        {
            await SwitchAsync(enabled: true);

            // Unsigned, it is not a prescription yet.
            await NotExportableAsync(path);

            await SignAsync(doctor, prescription);

            // Where the hospital requires the doctor's DSC, the document is a copy of the prescription like the printout:
            // not without a valid DSC signature, with the printout's own code.
            var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
            await DscTestPki.SetPolicyAsync(server, facilityId, "required", Ct);
            try
            {
                (await RefusedAsync(path)).ShouldBe(PrescriptionFinalCopies.RequiredCode);
            }
            finally
            {
                await DscTestPki.SetPolicyAsync(server, facilityId, null, Ct);
            }

            // The patient has not given the record-sharing consent of the FHIR API: a staff copy does not need it.
            var bundle = await ExportAsync(path, PrescriptionRecord, abha, $"prescription-{prescription.PrescriptionNumber}");
            Resources(bundle, "MedicationRequest").Select(r => r.GetProperty("medicationCodeableConcept").GetProperty("text").GetString())
                .ShouldBe([paracetamol.Name, cetirizine.Name], ignoreOrder: true);
            ShouldCarryThePrintedCopy(Resources(bundle, "Binary"));

            // The advice printed for the patient travels; the notes for the pharmacist do not.
            var text = bundle.GetRawText();
            text.ShouldContain("Drink plenty of fluids and rest.");
            text.ShouldNotContain(PharmacistNotes, Case.Insensitive);

            // Exported again it is the same document, byte for byte: its printed PDF is made again (where this server
            // makes PDFs, as CI's Windows runner does with Edge) and prints the same.
            ShouldBeTheSameDocument(await ExportAsync(path, PrescriptionRecord, abha, $"prescription-{prescription.PrescriptionNumber}"), bundle);

            var exportAudit = await AuditAsync(patient.Id, "patient.accessed");
            exportAudit.ShouldNotBeNull().Summary.ShouldBe("ABDM health record exported: prescription");
            exportAudit.EntityId.ShouldBe(prescription.Id.ToString("D"));
        }
        finally
        {
            await SwitchAsync(enabled: false);
        }
    }

    /// <summary>
    /// A doctor's id in ABDM's Healthcare Professionals Registry (the HPR ID, 14 digits), kept in the practitioner master,
    /// identifies them in their ABDM documents as the NRCeS guide's own examples identify a doctor: at
    /// <c>https://doctor.ndhm.gov.in</c>, typed as a medical licence number (v2-0203 <c>MD</c>), written
    /// <c>xx-xxxx-xxxx-xxxx</c>. Without it, a document named the doctor and gave only their council registration and
    /// HC360 staff code, which no other ABDM system can look up.
    /// </summary>
    [Fact]
    public async Task A_doctors_hpr_id_identifies_them_in_their_abdm_documents()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var master = $"/api/v1/masters/practitioners/{practitionerId}";
        var hpr = Digits(14);
        var written = $"{hpr[..2]}-{hpr[2..6]}-{hpr[6..10]}-{hpr[10..]}";

        // The hospital administrator enters it as the registry's card shows it, in groups of digits.
        await hospitalAdmin.PutAsync(master, With(await hospitalAdmin.GetAsync<JsonObject>(master), "hprId", written.Replace('-', ' ')));
        try
        {
            var abha = NewAbha();
            var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));
            var consultation = await ConsultAsync(doctor, patient.Id, sign: false);
            var paracetamol = await DrugAsync(doctor, "Paracetamol", "DRG-PCM500");
            var prescription = await PrescribeAsync(doctor, patient.Id, consultation, Tablet(paracetamol, DoseFrequency.TDS, days: 3, "After food"));
            await SignAsync(doctor, prescription);
            await SwitchAsync(enabled: true);

            var bundle = await ExportAsync(
                $"/api/v1/abdm/bundles/prescriptions/{prescription.Id}", PrescriptionRecord, abha, $"prescription-{prescription.PrescriptionNumber}");

            // The doctor who wrote it (the Composition's author) is identified by the HPR ID first.
            var author = Entry(bundle, Composition(bundle).GetProperty("author")[0].GetProperty("reference").GetString()!);
            var identifier = author.GetProperty("identifier")[0];
            identifier.GetProperty("system").GetString().ShouldBe("https://doctor.ndhm.gov.in");
            identifier.GetProperty("value").GetString().ShouldBe(written);
            var type = identifier.GetProperty("type").GetProperty("coding")[0];
            (type.GetProperty("system").GetString(), type.GetProperty("code").GetString())
                .ShouldBe(("http://terminology.hl7.org/CodeSystem/v2-0203", "MD"));

            // The master keeps it written as the registry writes it, and refuses a number that is not 14 digits.
            var record = await hospitalAdmin.GetAsync<JsonObject>(master);
            record["hprId"].ShouldNotBeNull().GetValue<string>().ShouldBe(written);
            using var refused = await hospitalAdmin.SendRawAsync(HttpMethod.Put, master, With(record, "hprId", hpr[..13]));
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("errors").TryGetProperty("hprId", out _).ShouldBeTrue();
        }
        finally
        {
            await SwitchAsync(enabled: false);
            await hospitalAdmin.PutAsync(master, With(await hospitalAdmin.GetAsync<JsonObject>(master), "hprId", null));
        }
    }

    [Fact]
    public async Task A_lab_report_is_exported_as_a_diagnostic_report_record_with_its_results_once_released()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var abha = NewAbha();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));
        var admission = await AdmitAsync(patient.Id);

        // From the admission on, the bed is given back whatever happens.
        try
        {
            // An in-patient's CRP, resulted by the lab and waiting for the pathologist.
            var crp = await CrpAsync(patient, admission);
            var path = $"/api/v1/abdm/bundles/lab-reports/{crp.ReportId}";
            await SwitchAsync(enabled: true);

            // Neither verified nor released, it is not a report yet.
            await NotExportableAsync(path);

            // The laboratory gives CRP's LOINC code its LOINC name in the test master.
            await NameCrpsLoincCodeAsync();

            await ReleaseAsync(crp);
            var bundle = await ExportAsync(path, DiagnosticReportRecord, abha, "lab-report-");
            ShouldBeTheSameDocument(await ExportAsync(path, DiagnosticReportRecord, abha, "lab-report-"), bundle);
            ShouldCarryThePrintedCopy(Attachments(bundle));
            var result = Results(bundle, Resources(bundle, "DiagnosticReport").ShouldHaveSingleItem()).ShouldHaveSingleItem();
            result.GetProperty("resourceType").GetString().ShouldBe("Observation");
            result.GetProperty("valueQuantity").GetProperty("value").GetDecimal().ShouldBe(Crp);
            var coding = result.GetProperty("code").GetProperty("coding").EnumerateArray().ShouldHaveSingleItem();
            (coding.GetProperty("system").GetString(), coding.GetProperty("code").GetString(), coding.GetProperty("display").GetString())
                .ShouldBe(("http://loinc.org", "1988-5", CrpLoincName));

            var exportAudit = await AuditAsync(patient.Id, "patient.accessed");
            exportAudit.ShouldNotBeNull().Summary.ShouldBe("ABDM health record exported: lab report");
            exportAudit.EntityId.ShouldBe(crp.ReportId.ToString("D"));
        }
        finally
        {
            await SwitchAsync(enabled: false);
            await CancelAdmissionAsync(admission);
        }
    }

    [Fact]
    public async Task A_radiology_report_is_exported_as_an_imaging_report_with_its_study_once_released_and_again_with_an_addendum()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var abha = NewAbha();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));

        // A chest X-ray whose images PACS linked. One doctor writes the report and another signs it off: radiologists are
        // users linked to the practitioner master, as these two are.
        var reporter = await server.SignInAsync(HospitalServer.Users.Doctor);
        var signer = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var reporterUser = (await reporter.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();
        var signerMe = await signer.GetAsync<JsonElement>("/api/v1/auth/me");
        var signerUser = signerMe.GetProperty("id").GetGuid();
        var xray = await ChestXrayAsync(signerMe.GetProperty("facilityId").GetGuid(), patient.Id);
        var path = $"/api/v1/abdm/bundles/radiology-reports/{xray}";

        try
        {
            await SwitchAsync(enabled: true);

            // A draft, a report waiting for sign-off and a report signed off but not released are not exported.
            await NotExportableAsync(path);
            await RadiologyAsync(xray, (report, study, now) => Submit(report, study, reporterUser, now));
            await NotExportableAsync(path);
            await RadiologyAsync(xray, (report, study, now) => SignOff(report, study, signerUser, now));
            (await NotExportableAsync(path)).ShouldBe("Only a released radiology report can be exported.");

            // Released, it is exported, though the patient has not given the FHIR API's record-sharing consent: a staff copy
            // does not need it. The same report exports as the same document.
            await RadiologyAsync(xray, (report, study, now) => report.Release(study, signerUser, now));
            var bundle = await ExportAsync(path, DiagnosticReportRecord, abha, "radiology-report-NHRDA");
            ShouldBeTheSameDocument(await ExportAsync(path, DiagnosticReportRecord, abha, "radiology-report-NHRDA"), bundle);
            Composition(bundle).GetProperty("section").EnumerateArray().ShouldHaveSingleItem()
                .GetProperty("code").GetProperty("coding")[0].GetProperty("code").GetString().ShouldBe("4201000179104");

            var diagnostic = Resources(bundle, "DiagnosticReport").ShouldHaveSingleItem();
            diagnostic.GetProperty("meta").GetProperty("profile")[0].GetString().ShouldBe("https://nrces.in/ndhm/fhir/r4/StructureDefinition/DiagnosticReportImaging");
            var code = diagnostic.GetProperty("code").GetProperty("coding").EnumerateArray().ShouldHaveSingleItem();
            (code.GetProperty("system").GetString(), code.GetProperty("code").GetString()).ShouldBe(("http://loinc.org", "18748-4"));
            diagnostic.GetProperty("conclusion").GetString().ShouldBe(XrayImpression);

            // Who wrote it and who signed it off interpret it; the one who signed it off attests the document.
            var interpreters = diagnostic.GetProperty("resultsInterpreter").EnumerateArray().Select(r => r.GetProperty("reference").GetString()).ToList();
            interpreters.Count.ShouldBe(2);
            interpreters.Select(r => Entry(bundle, r!).GetProperty("resourceType").GetString()).ShouldBe(["Practitioner", "Practitioner"]);
            Composition(bundle).GetProperty("attester").EnumerateArray().ShouldHaveSingleItem().GetProperty("party").GetProperty("reference").GetString()
                .ShouldBe(interpreters[1]);

            // The study: its accession number, the images PACS holds, and the modality as the worklist sent it.
            var imaging = Resources(bundle, "ImagingStudy").ShouldHaveSingleItem();
            imaging.GetProperty("identifier").EnumerateArray().ShouldContain(i => i.GetProperty("value").GetString()!.StartsWith("NHRDA", StringComparison.Ordinal));
            imaging.GetProperty("numberOfInstances").GetInt32().ShouldBe(2);
            imaging.GetProperty("modality")[0].GetProperty("code").GetString().ShouldBe("CR");
            ShouldCarryThePrintedCopy(Attachments(bundle));

            var exportAudit = await AuditAsync(patient.Id, "patient.accessed");
            exportAudit.ShouldNotBeNull().Summary.ShouldBe("ABDM health record exported: radiology report");
            exportAudit.EntityId.ShouldBe(xray.ToString("D"));

            // An addendum after the release is a new version of the document: amended, with the addendum in the report and
            // in its conclusion, and attested by the doctor who added it.
            await RadiologyAsync(xray, (report, study, now) => report.AddAddendum(study, "Small left pleural effusion on review.", "Second read", false, reporterUser, now));
            var amended = await ExportAsync(path, DiagnosticReportRecord, abha, "radiology-report-NHRDA");
            amended.GetProperty("meta").GetProperty("versionId").GetString().ShouldNotBe(bundle.GetProperty("meta").GetProperty("versionId").GetString());
            Composition(amended).GetProperty("status").GetString().ShouldBe("amended");
            var appended = Resources(amended, "DiagnosticReport").ShouldHaveSingleItem();
            appended.GetProperty("status").GetString().ShouldBe("appended");
            appended.GetProperty("conclusion").GetString().ShouldNotBeNull().ShouldContain("Small left pleural effusion on review.");
            Composition(amended).GetProperty("attester").EnumerateArray().ShouldHaveSingleItem().GetProperty("party").GetProperty("reference").GetString()
                .ShouldBe(appended.GetProperty("resultsInterpreter")[0].GetProperty("reference").GetString());
        }
        finally
        {
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_cardiology_report_is_exported_as_an_imaging_report_of_the_cardiology_department_once_released()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var abha = NewAbha();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));

        // A resting ECG, recorded by a technician and reported and signed off by a doctor.
        var cardiologist = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var me = await cardiologist.GetAsync<JsonElement>("/api/v1/auth/me");
        var cardiologistUser = me.GetProperty("id").GetGuid();
        var ecg = await EcgAsync(me.GetProperty("facilityId").GetGuid(), patient.Id);
        var path = $"/api/v1/abdm/bundles/cardiology-reports/{ecg}";

        try
        {
            await SwitchAsync(enabled: true);

            // A draft, a report waiting for sign-off and a report signed off but not released are not exported.
            await NotExportableAsync(path);
            await CardiologyAsync(ecg, (report, study, now) => SubmitCardiology(report, study, cardiologistUser, now));
            await NotExportableAsync(path);
            await CardiologyAsync(ecg, (report, study, now) => SignOffCardiology(report, study, cardiologistUser, now));
            (await NotExportableAsync(path)).ShouldBe("Only a released cardiology report can be exported.");

            // Released, it is exported, and exports as the same document again.
            await CardiologyAsync(ecg, (report, study, now) => report.Release(study, cardiologistUser, now));
            var bundle = await ExportAsync(path, DiagnosticReportRecord, abha, "cardiology-report-NHCD");
            ShouldBeTheSameDocument(await ExportAsync(path, DiagnosticReportRecord, abha, "cardiology-report-NHCD"), bundle);
            Composition(bundle).GetProperty("status").GetString().ShouldBe("final");

            var diagnostic = Resources(bundle, "DiagnosticReport").ShouldHaveSingleItem();
            diagnostic.GetProperty("meta").GetProperty("profile")[0].GetString().ShouldBe("https://nrces.in/ndhm/fhir/r4/StructureDefinition/DiagnosticReportImaging");
            diagnostic.GetProperty("category")[0].GetProperty("coding")[0].GetProperty("code").GetString().ShouldBe("394579002");
            var code = diagnostic.GetProperty("code").GetProperty("coding").EnumerateArray().ShouldHaveSingleItem();
            (code.GetProperty("system").GetString(), code.GetProperty("code").GetString()).ShouldBe(("http://loinc.org", "11524-6"));
            diagnostic.GetProperty("conclusion").GetString().ShouldBe(EcgConclusion);
            diagnostic.TryGetProperty("imagingStudy", out _).ShouldBeFalse("an ECG is a recording, not an imaging study");
            Resources(bundle, "ImagingStudy").ShouldBeEmpty();

            // The doctor who wrote it and signed it off interprets it and attests the document.
            var interpreter = diagnostic.GetProperty("resultsInterpreter").EnumerateArray().ShouldHaveSingleItem().GetProperty("reference").GetString();
            Entry(bundle, interpreter!).GetProperty("resourceType").GetString().ShouldBe("Practitioner");
            Composition(bundle).GetProperty("attester").EnumerateArray().ShouldHaveSingleItem().GetProperty("party").GetProperty("reference").GetString()
                .ShouldBe(interpreter);
            ShouldCarryThePrintedCopy(Attachments(bundle));

            var exportAudit = await AuditAsync(patient.Id, "patient.accessed");
            exportAudit.ShouldNotBeNull().Summary.ShouldBe("ABDM health record exported: cardiology report");
            exportAudit.EntityId.ShouldBe(ecg.ToString("D"));
        }
        finally
        {
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_discharge_summary_is_exported_with_the_stays_diagnoses_medicines_and_lab_report_once_signed()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var abha = NewAbha();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));
        var admission = await AdmitAsync(patient.Id);

        // From the admission on, the bed is given back whatever happens.
        try
        {
            // The stay: the doctor's admission note with its diagnosis, a CRP, and paracetamol for the fever.
            var note = await AdmissionNoteAsync(doctor, patient.Id, admission);
            await ReleaseAsync(await CrpAsync(patient, admission));
            var paracetamol = await DrugAsync(doctor, "Paracetamol", "DRG-PCM500");
            await SignAsync(doctor, await PrescribeAsync(doctor, patient.Id, note, Tablet(paracetamol, DoseFrequency.TDS, days: 3, "After food")));
            var admissionNote = await doctor.GetAsync<EncounterDetail>($"/api/v1/emr/encounters/{note}");
            await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{note}/sign", new SignEncounterRequest(admissionNote.Version));

            var summary = await SummaryAsync(doctor, patient.Id, admission);
            var path = $"/api/v1/abdm/bundles/discharge-summaries/{summary.Id}";
            await SwitchAsync(enabled: true);

            // A draft is still being written.
            await NotExportableAsync(path);

            // The admission note, signed, is part of the stay's record rather than a document of its own.
            (await NotExportableAsync($"/api/v1/abdm/bundles/op-consultations/{note}"))
                .ShouldBe("An in-patient note is not exported on its own: its diagnoses and prescriptions are in the stay's discharge summary.");

            await doctor.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{summary.Id}/sign", new SignDischargeSummaryRequest(summary.Version));
            var bundle = await ExportAsync(path, DischargeSummaryRecord, abha, $"discharge-summary-{patient.Uhid}-");
            Resources(bundle, "Condition").ShouldHaveSingleItem().GetProperty("code").GetProperty("coding")[0].GetProperty("code").GetString()
                .ShouldBe("R50.9");
            Resources(bundle, "MedicationRequest").ShouldHaveSingleItem().GetProperty("medicationCodeableConcept").GetProperty("text").GetString()
                .ShouldBe(paracetamol.Name);

            // The section reads whole: the doctor's list of medicines on discharge, then what was prescribed during the stay.
            var medications = Composition(bundle).GetProperty("section").EnumerateArray().Single(s => s.GetProperty("title").GetString() == "Medications");
            var narrative = medications.GetProperty("text");
            narrative.GetProperty("status").GetString().ShouldBe("additional");
            narrative.GetProperty("div").GetString().ShouldNotBeNull().ShouldSatisfyAllConditions(
                div => div.ShouldContain("Medications on discharge: Paracetamol 500 mg when needed for fever"),
                div => div.ShouldContain("Prescribed during the stay:"),
                div => div.ShouldContain(paracetamol.Name));
            Results(bundle, Resources(bundle, "DiagnosticReport").ShouldHaveSingleItem()).ShouldHaveSingleItem()
                .GetProperty("valueQuantity").GetProperty("value").GetDecimal().ShouldBe(Crp);

            // The summary itself, as a LOINC discharge summary document.
            Resources(bundle, "DocumentReference").ShouldHaveSingleItem().GetProperty("type").GetProperty("coding")[0].GetProperty("code").GetString()
                .ShouldBe("18842-5");
            ShouldCarryThePrintedCopy(Attachments(bundle));

            var exportAudit = await AuditAsync(patient.Id, "patient.accessed");
            exportAudit.ShouldNotBeNull().Summary.ShouldBe("ABDM health record exported: discharge summary");
            exportAudit.EntityId.ShouldBe(summary.Id.ToString("D"));
        }
        finally
        {
            await SwitchAsync(enabled: false);
            await CancelAdmissionAsync(admission);
        }
    }

    [Fact]
    public async Task An_amended_consultation_is_attested_by_the_doctor_who_amended_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var colleague = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var abha = NewAbha();
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));
        var signed = await ConsultAsync(doctor, patient.Id, sign: true);

        // A colleague corrects the plan after the note was signed.
        var note = await colleague.GetAsync<EncounterDetail>($"/api/v1/emr/encounters/{signed}");
        var amended = await colleague.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{signed}/amend", new AmendEncounterRequest(
            "Review date added", note.ChiefComplaint, note.HistoryOfPresentIllness, note.Examination, note.Assessment,
            "Paracetamol, fluids. Review in two days.", note.FollowUpDate, note.FollowUpAdvice, note.ReferralDepartmentId,
            note.ReferralPractitionerId, note.ReferralExternal, note.ReferralReason, note.Version));

        try
        {
            await SwitchAsync(enabled: true);
            var composition = Composition(await ExportAsync($"/api/v1/abdm/bundles/op-consultations/{signed}", "371530004", abha, $"consultation-{patient.Uhid}-"));

            // Both doctors wrote this version; the one who amended it answers for it, from the moment they did.
            composition.GetProperty("status").GetString().ShouldBe("amended");
            var authors = composition.GetProperty("author").EnumerateArray().Select(a => a.GetProperty("reference").GetString()).ToList();
            authors.Count.ShouldBe(2);
            var attester = composition.GetProperty("attester").EnumerateArray().ShouldHaveSingleItem();
            attester.GetProperty("mode").GetString().ShouldBe("legal");
            attester.GetProperty("party").GetProperty("reference").GetString().ShouldBe(authors[1]);
            DateTimeOffset.Parse(attester.GetProperty("time").GetString()!, CultureInfo.InvariantCulture)
                .ShouldBe(amended.AmendedAt.ShouldNotBeNull(), TimeSpan.FromSeconds(1));
        }
        finally
        {
            await SwitchAsync(enabled: false);
        }
    }

    [Fact]
    public async Task A_lab_report_kept_under_a_merged_duplicate_is_exported_as_the_surviving_patients_once_both_may_be_opened()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var abha = NewAbha();
        var kept = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));
        var duplicate = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(NewAbha(), null));
        var admission = await AdmitAsync(duplicate.Id);

        // From the admission on, the bed is given back whatever happens.
        try
        {
            // The CRP was done under the duplicate UHID. Merging moves the visits, but the laboratory keeps its reports
            // under the UHID they were made for.
            var crp = await CrpAsync(duplicate, admission);
            await ReleaseAsync(crp);
            await medicalRecords.PostAsync($"/api/v1/patients/{duplicate.Id}/merge", new MergePatientRequest(kept.Id, "Registered twice"));
            var path = $"/api/v1/abdm/bundles/lab-reports/{crp.ReportId}";
            await SwitchAsync(enabled: true);

            // The report is the surviving patient's, who carries the duplicate's UHID (the one its printed copy names) as old.
            var bundle = await ExportAsync(path, DiagnosticReportRecord, abha, "lab-report-");
            var patient = Resources(bundle, "Patient").ShouldHaveSingleItem();
            patient.GetProperty("identifier").EnumerateArray().Where(i => i.GetProperty("system").GetString() == "urn:hc360:uhid")
                .Select(i => (i.GetProperty("value").GetString(), i.GetProperty("use").GetString()))
                .ShouldBe([(kept.Uhid, "usual"), (duplicate.Uhid, "old")]);
            Results(bundle, Resources(bundle, "DiagnosticReport").ShouldHaveSingleItem()).ShouldHaveSingleItem()
                .GetProperty("valueQuantity").GetProperty("value").GetDecimal().ShouldBe(Crp);
            (await AuditAsync(kept.Id, "patient.accessed")).ShouldNotBeNull().EntityId.ShouldBe(crp.ReportId.ToString("D"));

            // Both records are the patient's: the medical records officer, who does not open restricted records, is refused
            // when either is restricted, and the refusal is on that record's audit trail.
            foreach (var restricted in new[] { duplicate.Id, kept.Id })
            {
                await SetPatientAsync(restricted, vip: true);
                (await DeniedAsync(medicalRecords, path)).ShouldBe("patient.restricted");
                (await AuditAsync(restricted, "abdm.export_denied")).ShouldNotBeNull().EntityId.ShouldBe(crp.ReportId.ToString("D"));
                await SetPatientAsync(restricted, vip: false);
            }

            // Once the surviving record is deleted the report is not exported. That is checked after the restricted
            // duplicate, and who may open it is told only that the report is not found, not whose it was.
            await SetPatientAsync(duplicate.Id, vip: true);
            await SetPatientAsync(kept.Id, deleted: true);
            (await DeniedAsync(medicalRecords, path)).ShouldBe("patient.restricted");
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            using var gone = await admin.SendRawAsync(HttpMethod.Get, path);
            gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await gone.Content.ReadAsStringAsync(Ct)).ShouldSatisfyAllConditions(
                body => body.ShouldNotContain(duplicate.Id.ToString()),
                body => body.ShouldNotContain(kept.Id.ToString()));
        }
        finally
        {
            await SetPatientAsync(kept.Id, deleted: false);
            await SwitchAsync(enabled: false);
            await CancelAdmissionAsync(admission);
        }
    }

    [Fact]
    public async Task A_discharge_summary_holds_a_stays_lab_report_kept_under_a_merged_duplicate_only_for_who_may_open_the_duplicate()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var medicalRecords = await server.SignInAsync(HospitalServer.Users.MrdOfficer);
        var abha = NewAbha();
        var kept = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(abha, null));
        var duplicate = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(NewAbha(), null));
        var admission = await AdmitAsync(duplicate.Id);

        // From the admission on, the bed is given back whatever happens.
        try
        {
            // The stay began under the duplicate UHID and its CRP was done there. The two records are merged, and the
            // doctor writes and signs the summary under the surviving one.
            var crp = await CrpAsync(duplicate, admission);
            await ReleaseAsync(crp);
            await medicalRecords.PostAsync($"/api/v1/patients/{duplicate.Id}/merge", new MergePatientRequest(kept.Id, "Registered twice"));
            var summary = await SummaryAsync(doctor, kept.Id, admission);
            await doctor.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{summary.Id}/sign", new SignDischargeSummaryRequest(summary.Version));
            var path = $"/api/v1/abdm/bundles/discharge-summaries/{summary.Id}";
            await SwitchAsync(enabled: true);

            // The CRP was done in the stay, so it is in the summary.
            var bundle = await ExportAsync(path, DischargeSummaryRecord, abha, $"discharge-summary-{kept.Uhid}-");
            Results(bundle, Resources(bundle, "DiagnosticReport").ShouldHaveSingleItem()).ShouldHaveSingleItem()
                .GetProperty("valueQuantity").GetProperty("value").GetDecimal().ShouldBe(Crp);

            // Once the duplicate is restricted, only who may open it exports the summary. The medical records officer is
            // refused without being told which record it is, and the refusal is on the duplicate's audit trail.
            await SetPatientAsync(duplicate.Id, vip: true);
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            using (var refused = await medicalRecords.SendRawAsync(HttpMethod.Get, path))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                var body = await refused.Content.ReadAsStringAsync(Ct);
                JsonSerializer.Deserialize<JsonElement>(body, ApiClient.Json).GetProperty("code").GetString().ShouldBe("abdm.restricted_duplicate");
                body.ShouldNotContain(duplicate.Uhid);
                body.ShouldNotContain(duplicate.Id.ToString());
            }

            (await AuditAsync(duplicate.Id, "abdm.export_denied")).ShouldNotBeNull().EntityId.ShouldBe(summary.Id.ToString("D"));
            bundle = await ExportAsync(path, DischargeSummaryRecord, abha, $"discharge-summary-{kept.Uhid}-");
            Resources(bundle, "DiagnosticReport").ShouldHaveSingleItem();

            // A report of the stay kept under someone else's record, not merged into this one, is not the patient's.
            var stranger = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", Registration(NewAbha(), null));
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<LabReport>().IgnoreQueryFilters()
                    .Where(r => r.Id == crp.ReportId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.PatientId, stranger.Id), Ct);
            }

            Resources(await ExportAsync(path, DischargeSummaryRecord, abha, $"discharge-summary-{kept.Uhid}-"), "DiagnosticReport").ShouldBeEmpty();

            // A report of the stay first released after the summary was signed is not in it either, so the restricted
            // duplicate it is kept under does not stop the medical records officer's export.
            var afterSigning = DateTimeOffset.UtcNow.AddDays(1);
            await using (var scope = server.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<LabReport>().IgnoreQueryFilters()
                    .Where(r => r.Id == crp.ReportId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.PatientId, duplicate.Id).SetProperty(r => r.FirstReleasedAt, afterSigning)
                        .SetProperty(r => r.ReleasedAt, afterSigning), Ct);
            }

            using var exported = await medicalRecords.SendRawAsync(HttpMethod.Get, path);
            exported.StatusCode.ShouldBe(HttpStatusCode.OK, await exported.Content.ReadAsStringAsync(Ct));
            Resources(await exported.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct), "DiagnosticReport").ShouldBeEmpty();
        }
        finally
        {
            await SwitchAsync(enabled: false);
            await CancelAdmissionAsync(admission);
        }
    }

    [Fact]
    public async Task A_loinc_name_is_kept_only_with_the_code_it_names()
    {
        // A test of its own, copied from CRP: the shared CRP master stays as the other tests expect it.
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var crp = await LabTestAsync(admin, "LAB-CRP");
        var code = "ABDM-" + Digits(8);
        var id = (await admin.PostAsync<JsonElement>("/api/v1/lab/tests", LabTestRequest(crp, code, "1988-5", CrpLoincName, version: null)))
            .GetProperty("id").GetGuid();
        var test = await admin.GetAsync<LabTestDetail>($"/api/v1/lab/tests/{id}");
        (test.Loinc, test.LoincName).ShouldBe(("1988-5", CrpLoincName));

        // The code corrected, with the old code's name sent back as it was: refused, as that name is not the new code's.
        using (var refused = await admin.SendRawAsync(HttpMethod.Put, $"/api/v1/lab/tests/{id}", LabTestRequest(test, code, "30522-7", CrpLoincName, test.Version)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("loincName");
        }

        // With the new code's own name it is saved; with no name, the code goes without one.
        const string highSensitivity = "C reactive protein [Mass/volume] in Serum or Plasma by High sensitivity method";
        await admin.PutAsync($"/api/v1/lab/tests/{id}", LabTestRequest(test, code, "30522-7", highSensitivity, test.Version));
        test = await admin.GetAsync<LabTestDetail>($"/api/v1/lab/tests/{id}");
        (test.Loinc, test.LoincName).ShouldBe(("30522-7", highSensitivity));
        await admin.PutAsync($"/api/v1/lab/tests/{id}", LabTestRequest(test, code, "1988-5", loincName: null, test.Version));
        test = await admin.GetAsync<LabTestDetail>($"/api/v1/lab/tests/{id}");
        (test.Loinc, test.LoincName).ShouldBe(("1988-5", null));
    }

    /// <summary>A consultation without a visit: complaint, assessment and a coded diagnosis; signed or left open.</summary>
    internal static async Task<Guid> ConsultAsync(ApiClient doctor, Guid patientId, bool sign)
    {
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, VisitId: null, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: "Fever for three days"));
        encounter = await doctor.PutAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}", new UpdateEncounterRequest(
            ChiefComplaint: "Fever for three days", HistoryOfPresentIllness: "Intermittent fever with body ache.", Examination: "Chest clear.",
            Assessment: "Acute febrile illness, probably viral.", Plan: "Paracetamol, fluids.", FollowUpDate: null, FollowUpAdvice: null,
            ReferralDepartmentId: null, ReferralPractitionerId: null, ReferralExternal: null, ReferralReason: null, encounter.Version));
        await doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounter.Id}/diagnoses",
            new AddDiagnosisRequest("R50.9", Title: null, DiagnosisType.Provisional, IsPrimary: true, OnsetDate: null, DiagnosisStatus.Active, Notes: null));
        if (sign)
        {
            await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}/sign", new SignEncounterRequest(encounter.Version));
        }

        return encounter.Id;
    }

    /// <summary>A medicine of the drug master, as the doctor's search finds it.</summary>
    private static async Task<PrescribableDrug> DrugAsync(ApiClient doctor, string name, string code) =>
        (await doctor.GetAsync<List<PrescribableDrug>>($"/api/v1/emr/drugs?search={name}")).Single(d => d.Code == code);

    /// <summary>One tablet by mouth, as often and for as many days as given.</summary>
    private static PrescriptionItemRequest Tablet(PrescribableDrug drug, DoseFrequency frequency, int days, string instructions) => new(
        drug.Id, drug.Name, drug.GenericName, drug.Strength, drug.DosageForm, Route: "Oral", Dose: "1 tablet", frequency, CustomFrequency: null,
        DosesPerDay: null, days, Quantity: null, instructions, drug.IsControlled, drug.ControlledSchedule);

    /// <summary>A prescription written in the encounter (an in-patient one when the encounter is in a stay), not signed yet.</summary>
    private static async Task<PrescriptionDto> PrescribeAsync(ApiClient doctor, Guid patientId, Guid encounterId, params PrescriptionItemRequest[] lines)
    {
        var prescription = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId, encounterId, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: PharmacistNotes,
            Advice: "Drink plenty of fluids and rest.", RefillsAllowed: 0, lines));
        prescription.Status.ShouldBe(PrescriptionStatus.Draft);
        return prescription;
    }

    private static async Task SignAsync(ApiClient doctor, PrescriptionDto prescription) =>
        (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{prescription.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: false, ConfirmControlledDrugs: false, prescription.Version)))
            .Prescription.Status.ShouldBe(PrescriptionStatus.Signed);

    /// <summary>The front office admits the patient under the doctor to a free bed of the female general ward.</summary>
    private async Task<AdmissionCreated> AdmitAsync(Guid patientId)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWF");
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patientId}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Emergency, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "High fever for three days", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null, NotifyPatient: false));
    }

    private async Task CancelAdmissionAsync(AdmissionCreated admission)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await admin.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of ABDM health records"));
    }

    /// <summary>The doctor's admission note in the stay, with its coded diagnosis.</summary>
    private static async Task<Guid> AdmissionNoteAsync(ApiClient doctor, Guid patientId, AdmissionCreated admission)
    {
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, admission.VisitId, admission.Id, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: "High fever for three days"));
        await doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounter.Id}/diagnoses",
            new AddDiagnosisRequest("R50.9", Title: null, DiagnosisType.Provisional, IsPrimary: true, OnsetDate: null, DiagnosisStatus.Active, Notes: null));
        return encounter.Id;
    }

    /// <summary>
    /// The doctor orders a CRP for the stay, and the lab draws, receives and results it: an in-patient's test is billed to
    /// the stay, so it is drawn without waiting for a payment. The report is a draft until the pathologist releases it.
    /// </summary>
    private async Task<LabWork> CrpAsync(PatientCreated patient, AdmissionCreated admission)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var crp = (await doctor.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?category=Laboratory&search=LAB-CRP"))
            .Single(s => s.Code == "LAB-CRP");
        await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, EncounterId: null, admission.VisitId, admission.Id, OrderPriority.Routine, "High fever for three days",
            [new OrderLineRequest(crp.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));

        var tube = (await lab.GetAsync<PagedResult<PendingCollectionItem>>($"/api/v1/lab/samples/pending-collection?search={patient.Uhid}"))
            .Items.ShouldHaveSingleItem();
        var label = (await lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{tube.LabOrderId}/collect",
            new CollectSamplesRequest(null, null, "Ward sample collection"))).ShouldHaveSingleItem();
        (await lab.PostAsync<List<ReceiveOutcome>>("/api/v1/lab/samples/receive", new ReceiveSamplesRequest([label.Barcode])))
            .ShouldHaveSingleItem().Received.ShouldBeTrue();
        var line = (await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.SampleId}/results")).Lines.ShouldHaveSingleItem();
        await lab.PostAsync<List<ResultSheetLine>>("/api/v1/lab/results",
            new EnterResultsRequest([new ResultEntry(line.ResultId, Crp.ToString(CultureInfo.InvariantCulture), null, null, null, null)]));

        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{tube.LabOrderId}");
        order.ReportStatus.ShouldBe(LabReportStatus.Draft);
        return new LabWork(order.ReportId.ShouldNotBeNull(), line.ResultId);
    }

    /// <summary>
    /// A chest X-ray of the patient, acquired an hour ago with its images linked from PACS, and the radiologist's report
    /// saved as a draft (its id). The radiology screens' steps are taken through the domain's own rules, as
    /// ChartResultTests does, so these tests do not depend on the radiology API's request shapes.
    /// </summary>
    private async Task<Guid> ChestXrayAsync(Guid facilityId, Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        var study = new ImagingStudy
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AccessionNumber = "NHRDA" + Digits(7),
            ServiceCode = "RAD-XR-CHEST",
            StudyName = "X-ray chest PA",
            Modality = Modality.XR,
            BodyPart = "Chest",
            Priority = OrderPriority.Routine,
            OrderedAt = now.AddHours(-2),
            Status = ImagingStudyStatus.Ordered,
            StudyInstanceUid = DicomConventions.NewUid(),
        };
        study.Start(null, now.AddHours(-1)).IsSuccess.ShouldBeTrue();
        study.Acquire(new AcquisitionDetails(ContrastUsed: false, null, null, null, null), null, now.AddMinutes(-55)).IsSuccess.ShouldBeTrue();
        study.LinkPacs(study.StudyInstanceUid, seriesCount: 1, instanceCount: 2, now.AddMinutes(-50)).IsSuccess.ShouldBeTrue();
        var report = new RadiologyReport { FacilityId = facilityId, ImagingStudyId = study.Id, PatientId = patientId };
        report.Save(new RadiologyReportContent(null, "PA view", "Cough for two weeks", null, "Both lung fields are clear. Heart size is normal.",
            XrayImpression, IsCriticalFinding: false, CriticalFindingNote: null)).IsSuccess.ShouldBeTrue();
        db.AddRange(study, report);
        await db.SaveChangesAsync(Ct);
        return report.Id;
    }

    /// <summary>One step of the radiology workflow on the saved report and its study: submit, sign off, release, addendum.</summary>
    private async Task RadiologyAsync(Guid reportId, Func<RadiologyReport, ImagingStudy, DateTimeOffset, Result> step)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var report = await db.Set<RadiologyReport>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Include(r => r.Addenda)
            .SingleAsync(r => r.Id == reportId, Ct);
        var study = await db.Set<ImagingStudy>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).SingleAsync(s => s.Id == report.ImagingStudyId, Ct);
        step(report, study, DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The radiologist submits the report for sign-off.</summary>
    private static Result Submit(RadiologyReport report, ImagingStudy study, Guid? by, DateTimeOffset now)
    {
        study.MarkReported(now);
        return report.Submit(by, now);
    }

    /// <summary>The radiologist signs the report off, which does not release it.</summary>
    private static Result SignOff(RadiologyReport report, ImagingStudy study, Guid? by, DateTimeOffset now)
    {
        study.MarkVerified(now);
        return report.Verify(by, now);
    }

    /// <summary>
    /// A resting ECG of the patient, recorded twenty minutes ago with its tracing attached, and the cardiologist's report
    /// saved as a draft (its id). As for radiology, the steps are taken through the domain's own rules.
    /// </summary>
    private async Task<Guid> EcgAsync(Guid facilityId, Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var now = DateTimeOffset.UtcNow;
        var study = new CardiologyStudy
        {
            FacilityId = facilityId,
            PatientId = patientId,
            AccessionNumber = "NHCD" + Digits(7),
            ServiceCode = "CARD-ECG",
            StudyName = "ECG 12 lead",
            StudyType = CardiologyStudyType.Ecg,
            Priority = OrderPriority.Routine,
            OrderedAt = now.AddMinutes(-40),
            Status = CardiologyStudyStatus.Ordered,
        };
        study.Start(null, "ECG cart 1", now.AddMinutes(-25)).IsSuccess.ShouldBeTrue();
        study.AttachFiles([Guid.CreateVersion7()]).IsSuccess.ShouldBeTrue();
        study.Complete(null, null, now.AddMinutes(-20)).IsSuccess.ShouldBeTrue();
        var report = new CardiologyReport { FacilityId = facilityId, CardiologyStudyId = study.Id, PatientId = patientId, StudyType = study.StudyType };
        report.Save(new CardiologyReportContent(
            new EcgFindings { HeartRate = 78, Rhythm = "Sinus rhythm", PrInterval = 160, QrsDuration = 88, QtcInterval = 420, Interpretation = "Normal ECG" },
            null, null, null, null, EcgConclusion, null, IsCriticalFinding: false, CriticalFindingNote: null)).IsSuccess.ShouldBeTrue();
        db.AddRange(study, report);
        await db.SaveChangesAsync(Ct);
        return report.Id;
    }

    /// <summary>One step of the cardiology workflow on the saved report and its study: submit, sign off, release.</summary>
    private async Task CardiologyAsync(Guid reportId, Func<CardiologyReport, CardiologyStudy, DateTimeOffset, Result> step)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var report = await db.Set<CardiologyReport>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).SingleAsync(r => r.Id == reportId, Ct);
        var study = await db.Set<CardiologyStudy>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).SingleAsync(s => s.Id == report.CardiologyStudyId, Ct);
        step(report, study, DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The cardiologist submits the report for sign-off.</summary>
    private static Result SubmitCardiology(CardiologyReport report, CardiologyStudy study, Guid? by, DateTimeOffset now)
    {
        study.MarkReported(now);
        return report.Submit(by, now);
    }

    /// <summary>The cardiologist signs the report off, which does not release it.</summary>
    private static Result SignOffCardiology(CardiologyReport report, CardiologyStudy study, Guid? by, DateTimeOffset now)
    {
        study.MarkVerified(now);
        return report.Verify(by, now);
    }

    /// <summary>The pathologist verifies the result and releases the report in one step.</summary>
    private async Task ReleaseAsync(LabWork crp)
    {
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        (await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify",
            new VerifyResultsRequest([crp.ResultId], QcOverrideReason: null, CriticalCall: null, Release: true))).ReleasedItemIds.ShouldHaveSingleItem();
    }

    private static async Task<LabTestDetail> LabTestAsync(ApiClient admin, string code)
    {
        var listed = (await admin.GetAsync<PagedResult<LabTestListItem>>($"/api/v1/lab/tests?search={code}")).Items.Single(t => t.Code == code);
        return await admin.GetAsync<LabTestDetail>($"/api/v1/lab/tests/{listed.Id}");
    }

    /// <summary>The test as the masters screen saves it, under <paramref name="code"/>: not orderable, so it needs no billable service.</summary>
    private static SaveLabTestRequest LabTestRequest(LabTestDetail t, string code, string? loinc, string? loincName, uint? version) => new(
        code, t.Name, t.ShortName, t.Section, t.SpecimenType, t.Container, t.Method, t.TatHours, loinc, loincName, ServiceItemId: null,
        IsOrderable: false, t.IsPanel, t.ResultType, t.Unit, t.Decimals, t.Options, InstrumentChannelCodes: null, t.QcGroup, t.DeltaThresholdPercent,
        t.AllowAutoValidation, t.Preparation, t.DrawSeparately, t.Interpretation, t.DefaultReferralLabId, t.DisplayOrder, IsActive: false, version);

    /// <summary>Gives the CRP test's LOINC code (1988-5) its LOINC name in the test master, as the hospital administrator.</summary>
    private async Task NameCrpsLoincCodeAsync()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var listed = (await admin.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=LAB-CRP")).Items.Single(t => t.Code == "LAB-CRP");
        var t = await admin.GetAsync<LabTestDetail>($"/api/v1/lab/tests/{listed.Id}");
        t.Loinc.ShouldBe("1988-5");
        await admin.PutAsync($"/api/v1/lab/tests/{t.Id}", new SaveLabTestRequest(
            t.Code, t.Name, t.ShortName, t.Section, t.SpecimenType, t.Container, t.Method, t.TatHours, t.Loinc, CrpLoincName, t.ServiceItemId,
            t.IsOrderable, t.IsPanel, t.ResultType, t.Unit, t.Decimals, t.Options, t.InstrumentChannelCodes, t.QcGroup, t.DeltaThresholdPercent,
            t.AllowAutoValidation, t.Preparation, t.DrawSeparately, t.Interpretation, t.DefaultReferralLabId, t.DisplayOrder, t.IsActive, t.Version));
        (await admin.GetAsync<LabTestDetail>($"/api/v1/lab/tests/{t.Id}")).LoincName.ShouldBe(CrpLoincName);
    }

    /// <summary>The doctor's discharge summary of the stay, saved and not signed yet.</summary>
    private static Task<DischargeSummaryDto> SummaryAsync(ApiClient doctor, Guid patientId, AdmissionCreated admission) =>
        doctor.PutAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/by-admission/{admission.Id}",
            new SaveDischargeSummaryRequest(
                PatientId: patientId, EncounterId: null, PractitionerId: null, AdmittedAt: null, DischargedAt: null,
                Diagnoses: "Acute febrile illness, probably viral", DiagnosisCodes: ["R50.9"], PresentingComplaints: "High fever for three days",
                CourseInHospital: "The fever settled in two days on paracetamol and oral fluids. CRP was normal.", Procedures: null,
                InvestigationsSummary: "CRP 3.2 mg/L", ConditionAtDischarge: "Afebrile and well", Medications: "Paracetamol 500 mg when needed for fever",
                Advice: "Drink plenty of fluids", FollowUpDate: null, FollowUpInstructions: "Come back if the fever returns", Version: null));

    /// <summary>
    /// Marks a patient's record restricted (VIP) or not, or deleted or not, directly: no screen edits a record once it has
    /// been merged.
    /// </summary>
    private async Task SetPatientAsync(Guid patientId, bool? vip = null, bool? deleted = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var patients = scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().IgnoreQueryFilters().Where(p => p.Id == patientId);
        if (vip is { } restricted)
        {
            await patients.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsVip, restricted), Ct);
        }

        if (deleted is { } isDeleted)
        {
            await patients.ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDeleted, isDeleted), Ct);
        }
    }

    /// <summary>The code of an export refused to <paramref name="caller"/> (403).</summary>
    private static async Task<string?> DeniedAsync(ApiClient caller, string path)
    {
        using var refused = await caller.SendRawAsync(HttpMethod.Get, path);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await refused.Content.ReadAsStringAsync(Ct));
        return (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    /// <summary>The code of a refused export (422).</summary>
    private async Task<string?> RefusedAsync(string path)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using var refused = await admin.SendRawAsync(HttpMethod.Get, path);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync(Ct));
        return (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();
    }

    /// <summary>The hospital administrator, who exports records for ABDM, is told the record cannot be exported yet, and why.</summary>
    private async Task<string?> NotExportableAsync(string path)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using var refused = await admin.SendRawAsync(HttpMethod.Get, path);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("abdm.not_exportable");
        return problem.GetProperty("detail").GetString();
    }

    /// <summary>
    /// The record as the hospital administrator exports it, checked for what every ABDM document is: a FHIR document whose
    /// first entry is the Composition of the record's type, about the patient with their ABHA number, and which stands
    /// alone: each entry has a <c>urn:uuid:</c> address, and every reference in the document is to one of them.
    /// </summary>
    private async Task<JsonElement> ExportAsync(string path, string documentType, string abha, string fileName)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        using var exported = await admin.SendRawAsync(HttpMethod.Get, path);
        var body = await exported.Content.ReadAsStringAsync(Ct);
        exported.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        exported.Content.Headers.ContentType.ShouldNotBeNull().MediaType.ShouldBe("application/fhir+json");
        exported.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        var disposition = exported.Content.Headers.ContentDisposition.ShouldNotBeNull();
        disposition.DispositionType.ShouldBe("attachment");
        var saved = (disposition.FileNameStar ?? disposition.FileName).ShouldNotBeNull().Trim('"');
        saved.ShouldStartWith(fileName);
        saved.ShouldEndWith("-abdm.json");

        // It validates against the NRCeS ndhm.in 6.5.0 profiles (Firely, with the FHIR R4 core definitions).
        var validation = AbdmProfileValidator.Validate(JsonSerializer.Deserialize<R4.Bundle>(body, Fhir).ShouldNotBeNull(), AbdmProfileValidator.DocumentBundle);
        validation.Errors.ShouldBeEmpty(validation.Describe());
        var bundle = JsonSerializer.Deserialize<JsonElement>(body, ApiClient.Json);
        bundle.GetProperty("resourceType").GetString().ShouldBe("Bundle");
        bundle.GetProperty("type").GetString().ShouldBe("document");

        var entries = bundle.GetProperty("entry").EnumerateArray().ToList();
        var composition = entries[0].GetProperty("resource");
        composition.GetProperty("resourceType").GetString().ShouldBe("Composition");
        composition.GetProperty("type").GetProperty("coding")[0].GetProperty("code").GetString().ShouldBe(documentType);
        Resources(bundle, "Patient").ShouldHaveSingleItem().GetProperty("identifier").EnumerateArray()
            .Single(i => i.GetProperty("system").GetString() == "https://healthid.ndhm.gov.in").GetProperty("value").GetString()
            .ShouldBe(Abha.Format(abha));

        var addresses = entries.Select(e => e.GetProperty("fullUrl").GetString()!).ToList();
        addresses.ShouldAllBe(a => a.StartsWith("urn:uuid:", StringComparison.Ordinal));
        var references = References(bundle).ToList();
        references.ShouldNotBeEmpty();
        references.Except(addresses).ShouldBeEmpty("Every reference in the document is to one of its entries.");
        return bundle;
    }

    private static JsonElement Composition(JsonElement bundle) => bundle.GetProperty("entry")[0].GetProperty("resource");

    /// <summary>The resource of the entry a reference in the document points to.</summary>
    private static JsonElement Entry(JsonElement bundle, string fullUrl) =>
        bundle.GetProperty("entry").EnumerateArray().Single(e => e.GetProperty("fullUrl").GetString() == fullUrl).GetProperty("resource");

    /// <summary>
    /// The same record exported again is the same document, byte for byte. When it is not, the failure names the entries
    /// that changed and, for a printed PDF, the bytes where the two prints part: the whole document is too long to show.
    /// </summary>
    private static void ShouldBeTheSameDocument(JsonElement again, JsonElement first)
    {
        if (string.Equals(again.GetRawText(), first.GetRawText(), StringComparison.Ordinal))
        {
            return;
        }

        var changes = new List<string>();
        var before = first.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).ToList();
        var after = again.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).ToList();
        foreach (var (was, now) in before.Zip(after))
        {
            if (!string.Equals(was.GetRawText(), now.GetRawText(), StringComparison.Ordinal))
            {
                changes.Add($"{was.GetProperty("resourceType").GetString()}: {Parting(was.GetRawText(), now.GetRawText())}");
            }

            foreach (var (pdf, pdfAgain) in Pdfs(was).Zip(Pdfs(now)))
            {
                if (!pdf.AsSpan().SequenceEqual(pdfAgain))
                {
                    changes.Add($"its PDF ({pdf.Length} and {pdfAgain.Length} bytes): {Parting(Encoding.Latin1.GetString(pdf), Encoding.Latin1.GetString(pdfAgain))}");
                }
            }
        }

        if (before.Count != after.Count)
        {
            changes.Add($"{before.Count} entries, then {after.Count}");
        }

        again.GetRawText().ShouldBe(first.GetRawText(), $"exported again, the document changed: {string.Join("; ", changes)}");

        static IEnumerable<byte[]> Pdfs(JsonElement resource) =>
            resource.GetProperty("resourceType").GetString() == "Binary" && resource.TryGetProperty("data", out var data)
                ? [data.GetBytesFromBase64()]
                : resource.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                    ? content.EnumerateArray().Select(c => c.GetProperty("attachment"))
                        .Where(a => a.TryGetProperty("data", out _)).Select(a => a.GetProperty("data").GetBytesFromBase64())
                    : [];

        // Where two texts part: the first difference, with what comes before it and each side's next characters.
        static string Parting(string a, string b)
        {
            var at = 0;
            while (at < a.Length && at < b.Length && a[at] == b[at])
            {
                at++;
            }

            static string Shown(string text) => string.Concat(text.Select(c => c is >= ' ' and <= '~' ? c.ToString() : $"\\x{(int)c:x2}"));
            var from = Math.Max(0, at - 80);
            return $"at {at}, after \"{Shown(a[from..at])}\": \"{Shown(a[at..Math.Min(a.Length, at + 160)])}\" then \"{Shown(b[at..Math.Min(b.Length, at + 160)])}\"";
        }
    }

    /// <summary>A master record as its edit screen sends it back, with one field changed.</summary>
    private static JsonObject With(JsonObject record, string field, string? value)
    {
        var changed = record.DeepClone().AsObject();
        changed[field] = value;
        return changed;
    }

    /// <summary>The document's resources of one type, in document order.</summary>
    private static List<JsonElement> Resources(JsonElement bundle, string type) =>
    [
        .. bundle.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).Where(r => r.GetProperty("resourceType").GetString() == type),
    ];

    /// <summary>The files the document's DocumentReferences hold.</summary>
    private static IEnumerable<JsonElement> Attachments(JsonElement bundle) =>
        Resources(bundle, "DocumentReference").SelectMany(d => d.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("attachment")));

    /// <summary>
    /// A document holds the PDF the hospital prints of its record wherever this server has a browser to make PDFs with,
    /// as CI's Windows runner has Edge, and none where it has none.
    /// </summary>
    private void ShouldCarryThePrintedCopy(IEnumerable<JsonElement> files)
    {
        var printed = files.Where(f => f.TryGetProperty("contentType", out var type) && type.GetString() == "application/pdf").ToList();
        if (server.Services.GetRequiredService<IPdfRenderer>().Status.Browser is null)
        {
            printed.ShouldBeEmpty("this server has no browser to make PDFs with");
            return;
        }

        var pdf = printed.ShouldHaveSingleItem().GetProperty("data").GetBytesFromBase64();
        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
    }

    /// <summary>The resources a DiagnosticReport's <c>result</c> references point to.</summary>
    private static List<JsonElement> Results(JsonElement bundle, JsonElement report) =>
    [
        .. report.GetProperty("result").EnumerateArray().Select(r => bundle.GetProperty("entry").EnumerateArray()
            .Single(e => e.GetProperty("fullUrl").GetString() == r.GetProperty("reference").GetString()).GetProperty("resource")),
    ];

    /// <summary>Every literal reference in the JSON, at any depth.</summary>
    private static IEnumerable<string> References(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().SelectMany(p =>
            p is { Name: "reference", Value.ValueKind: JsonValueKind.String } ? [p.Value.GetString()!] : References(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().SelectMany(References),
        _ => [],
    };

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.SetFeatureAsync(FeatureKeys.Abdm, new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }

    private async Task<AuditEvent?> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Action == action)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(Ct);
    }

    internal static RegisterPatientRequest Registration(string abha, string? address) => new(
        Title: "Mrs.", FirstName: "Meera", MiddleName: null, LastName: "Abdm" + Letters(8), Gender: Gender.Female,
        DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
        Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
        GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
        EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
        Category: null, AbhaNumber: abha, AbhaAddress: address, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
        ConfirmedNotDuplicate: true);

    /// <summary>The record as the edit screen sends it back, with a new phone number and ABHA address.</summary>
    private static UpdatePatientRequest Edit(PatientDetail p, string phone, string? abhaAddress) => new(
        p.Title, p.FirstName, p.MiddleName, p.LastName, p.Gender, p.DateOfBirth, AgeYears: null, AgeMonths: null,
        Phone: phone, p.AlternatePhone, p.Email, p.Address, p.BloodGroup, p.MaritalStatus, p.GuardianName, p.GuardianRelation,
        p.EmergencyContactName, p.EmergencyContactPhone, p.EmergencyContactRelation, p.Occupation, p.Religion, p.Nationality,
        p.PreferredLanguage, p.Category, AbhaNumber: null, AbhaAddress: abhaAddress, AadhaarNumber: null, p.PhotoFileId, p.IsVip,
        p.IsDeceased, p.DeceasedAt, p.Notes, p.IsActive, p.Version);

    /// <summary>
    /// An ABHA number of this test alone, made so that it fails a Verhoeff check, as most real ABHA numbers do: they have
    /// no check digit, and registration must take any 14 digits.
    /// </summary>
    internal static string NewAbha()
    {
        var digits = "91" + Digits(11);
        return digits + (char)('0' + ((Verhoeff.CheckDigit(digits) - '0' + 1) % 10));
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    internal static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    /// <summary>A resulted lab test: its accession's report, and the result the pathologist verifies.</summary>
    private sealed record LabWork(Guid ReportId, Guid ResultId);
}
