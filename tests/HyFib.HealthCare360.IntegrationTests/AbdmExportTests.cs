using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Features;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// ABDM health records over the real API (SRS EHR-08): a patient registered with an ABHA number and address, a signed
/// consultation exported as an ABDM document, and who may do it. The ABDM switch is hospital-wide and off by default,
/// so these tests are in the feature-switch collection and turn it off again in a <c>finally</c> block.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class AbdmExportTests(HospitalServer server)
{
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
            }

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

    /// <summary>A consultation without a visit: complaint, assessment and a coded diagnosis; signed or left open.</summary>
    private static async Task<Guid> ConsultAsync(ApiClient doctor, Guid patientId, bool sign)
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

    private async Task SwitchAsync(bool enabled)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        (await admin.PutAsync<FeatureDto>($"/api/v1/admin/features/{FeatureKeys.Abdm}", new SetFeatureRequest(enabled))).Enabled.ShouldBe(enabled);
    }

    private async Task<AuditEvent?> AuditAsync(Guid patientId, string action)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.PatientId == patientId && a.Action == action)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(Ct);
    }

    private static RegisterPatientRequest Registration(string abha, string? address) => new(
        Title: "Mrs.", FirstName: "Meera", MiddleName: null, LastName: "Abdm" + Letters(8), Gender: Gender.Female,
        DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null,
        Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
        GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
        EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
        Category: null, AbhaNumber: abha, AbhaAddress: address, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
        ConfirmedNotDuplicate: true);

    /// <summary>
    /// An ABHA number of this test alone, made so that it fails a Verhoeff check, as most real ABHA numbers do: they have
    /// no check digit, and registration must take any 14 digits.
    /// </summary>
    private static string NewAbha()
    {
        var digits = "91" + Digits(11);
        return digits + (char)('0' + ((Verhoeff.CheckDigit(digits) - '0' + 1) % 10));
    }

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));
}
