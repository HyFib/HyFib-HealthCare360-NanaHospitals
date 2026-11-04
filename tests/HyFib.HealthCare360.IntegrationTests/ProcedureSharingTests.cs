using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.MedicalRecords;
using HyFib.HealthCare360.Modules.Clinical.Features.Procedures;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;
using Migrations = HyFib.HealthCare360.Server.Persistence.Migrations;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Gap #26 through the whole server: the procedures doctors record reach another provider through the FHIR API, by name
/// (the ABDM guide codes procedures in SNOMED CT only, and HC360's are ICD-10-PCS), with who did them, where and how they
/// went; a record made for the wrong patient is retracted; and only under a record-sharing consent that covers procedures.
/// A procedure that reveals what a sensitive diagnosis would (a termination of pregnancy, electroconvulsive therapy) closes
/// the record first, so it never leaves under a consent.
/// </summary>
public sealed class ProcedureSharingTests(HospitalServer server)
{
    [Fact]
    public async Task A_provider_reads_the_procedures_done_here_by_name_under_a_consent_that_covers_them()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var procedures = $"/api/v1/emr/patients/{patient}/procedures";
        var performedAt = DateTimeOffset.UtcNow.AddHours(-3);

        // An appendicectomy coded from the catalogue, a drainage by a visiting surgeon, and one recorded for the wrong patient.
        var appendicectomy = await doctor.PostAsync<ProcedureDto>(procedures, new RecordProcedureRequest(
            null, performedAt, "0DTJ4ZZ", null, null, null, ProcedureSide.NotApplicable, "Abdomen", "Uneventful", null));
        var drainage = await doctor.PostAsync<ProcedureDto>(procedures, new RecordProcedureRequest(
            null, performedAt.AddHours(1), null, "Incision and drainage of abscess", null, "Dr. Rao (visiting)", ProcedureSide.Right, "Axilla",
            "Pus drained", null));
        var wrong = await doctor.PostAsync<ProcedureDto>(procedures, new RecordProcedureRequest(
            null, performedAt, null, "Wound suturing", null, null, ProcedureSide.NotApplicable, null, null, null));
        await doctor.PostAsync<ProcedureDto>($"/api/v1/emr/procedures/{wrong.Id}/entered-in-error", new EnteredInErrorRequest("Recorded for the wrong patient"));

        // Another patient's procedure, done the same day: never in this patient's search.
        var other = await RegisterAsync(frontOffice);
        await doctor.PostAsync<ProcedureDto>($"/api/v1/emr/patients/{other}/procedures", new RecordProcedureRequest(
            null, performedAt, null, "Wound suturing", null, null, ProcedureSide.NotApplicable, null, null, null));

        // The patient shares their procedures, and nothing else, with the partner for treatment.
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], [SharingPurpose.Treatment], [SharedRecordKind.Procedures], null, null, null)));

        var search = await FhirAsync(partner, $"Procedure?patient={patient}", "TREAT");
        Matches(search.Body).ShouldBe([appendicectomy.Id.ToString("D"), drainage.Id.ToString("D")], ignoreOrder: true);
        var coded = Resource(search.Body, appendicectomy.Id);
        coded.GetProperty("status").GetString().ShouldBe("completed");
        coded.GetProperty("code").GetProperty("text").GetString().ShouldBe(appendicectomy.Display);
        coded.GetProperty("code").TryGetProperty("coding", out _).ShouldBeFalse("the guide allows SNOMED CT only, and the catalogue's code is ICD-10-PCS");
        coded.GetProperty("performer")[0].GetProperty("actor").GetProperty("display").GetString().ShouldBe(appendicectomy.PerformerName,
            "the recording doctor did it");
        coded.GetProperty("bodySite")[0].GetProperty("text").GetString().ShouldBe("Abdomen");
        coded.GetProperty("outcome").GetProperty("text").GetString().ShouldBe("Uneventful");
        var visiting = Resource(search.Body, drainage.Id);
        visiting.GetProperty("performer")[0].GetProperty("actor").GetProperty("display").GetString().ShouldBe("Dr. Rao (visiting)");
        visiting.GetProperty("bodySite")[0].GetProperty("text").GetString().ShouldBe("Axilla, right");

        // The record in error is left out of searches, but read by id it says it was entered in error.
        var retracted = await FhirAsync(partner, $"Procedure/{wrong.Id}", "TREAT");
        retracted.Status.ShouldBe(200);
        retracted.Body.GetProperty("status").GetString().ShouldBe("entered-in-error");
        Matches((await FhirAsync(partner, $"Procedure?patient={patient}&status=entered-in-error", "TREAT")).Body).ShouldBe([wrong.Id.ToString("D")]);

        // The consent covers procedures only.
        Matches((await FhirAsync(partner, $"AllergyIntolerance?patient={patient}", "TREAT")).Body).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_consent_that_names_other_kinds_of_records_does_not_share_procedures()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var procedure = await doctor.PostAsync<ProcedureDto>($"/api/v1/emr/patients/{patient}/procedures", new RecordProcedureRequest(
            null, DateTimeOffset.UtcNow.AddHours(-1), null, "Lumbar puncture", null, null, ProcedureSide.NotApplicable, "Lower back", "Clear fluid",
            null));
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], null, [SharedRecordKind.Allergies, SharedRecordKind.ClinicalNotes], null, null, null)));

        var search = await FhirAsync(partner, $"Procedure?patient={patient}", null);
        Matches(search.Body).ShouldBeEmpty();
        Withheld(search.Body).ShouldBeTrue();
        (await FhirAsync(partner, $"Procedure/{procedure.Id}", null)).Refusal.ShouldBe((403, "consent-required"));
    }

    [Fact]
    public async Task A_procedure_that_ends_a_pregnancy_closes_the_record_so_no_provider_reads_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents", new CaptureConsentRequest(
            ConsentType.DataSharing, null, true, null, null, null, null, null,
            new RecordSharingScope([partnerId], [SharingPurpose.Treatment], [SharedRecordKind.Procedures], null, null, null)));
        var appendicectomy = await RecordAsync(doctor, patient, "0DTJ4ZZ");
        Matches((await FhirAsync(partner, $"Procedure?patient={patient}", "TREAT")).Body).ShouldBe([appendicectomy.Id.ToString("D")]);
        (await CategoriesAsync(admin, patient)).ShouldBeEmpty();

        // A suction evacuation from the procedure catalogue (ICD-10-PCS 10A07Z6, the root operation Abortion) puts the record
        // in the MTP category in its own save, as a diagnosis of a medical abortion (O04) does: the MTP Act (s.5A) lets
        // nobody reveal the woman's name or particulars.
        var termination = await RecordAsync(doctor, patient, "10A07Z6");
        (await CategoriesAsync(admin, patient)).ShouldBe([SensitiveCategory.Mtp]);

        // From then on the consent shares nothing of the record: neither the termination nor what the provider read before.
        var search = await FhirAsync(partner, $"Procedure?patient={patient}", "TREAT");
        Matches(search.Body).ShouldBeEmpty();
        Withheld(search.Body).ShouldBeTrue();
        (await FhirAsync(partner, $"Procedure/{termination.Id}", "TREAT")).Refusal.ShouldBe((403, "restricted-record"));
        (await FhirAsync(partner, $"Procedure/{appendicectomy.Id}", "TREAT")).Refusal.ShouldBe((403, "restricted-record"));
    }

    [Fact]
    public async Task Electroconvulsive_therapy_closes_the_record_and_the_upgrade_closes_those_recorded_before_it()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var mrd = await server.SignInAsync(HospitalServer.Users.MrdOfficer);

        // Medical records adds ECT to the procedure catalogue, which does not ship with it.
        using (var added = await mrd.SendRawAsync(HttpMethod.Post, "/api/v1/mrd/procedure-codes", new SaveProcedureCodeRequest(
                   "GZB0ZZZ", "Electroconvulsive therapy, unilateral, single seizure", "ICD-10-PCS", "Psychiatry", "ECT", true)))
        {
            added.EnsureSuccessStatusCode();
        }

        // The psychiatrist records it on the chart: the record goes into the mental health category, as an F20–F69 diagnosis
        // puts it (Mental Healthcare Act 2017, s.23).
        var treated = await RegisterAsync(frontOffice);
        await RecordAsync(doctor, treated, "GZB0ZZZ");
        (await CategoriesAsync(admin, treated)).ShouldBe([SensitiveCategory.MentalHealth]);

        // A procedure recorded under the wrong code and corrected to ECT does the same.
        var corrected = await RegisterAsync(frontOffice);
        var misfiled = await RecordAsync(doctor, corrected, "0DTJ4ZZ");
        (await CategoriesAsync(admin, corrected)).ShouldBeEmpty();
        await doctor.PutAsync<ProcedureDto>($"/api/v1/emr/procedures/{misfiled.Id}", new CorrectProcedureRequest(
            misfiled.PerformedAt, "GZB0ZZZ", null, null, null, ProcedureSide.NotApplicable, null, null, null, "Wrong procedure picked", misfiled.Version));
        (await CategoriesAsync(admin, corrected)).ShouldBe([SensitiveCategory.MentalHealth]);

        // Records kept before the upgrade, when nothing put them in a category: a termination, a course of ECT, a termination
        // marked as entered in error, and an appendicectomy. Then the upgrade's own statement.
        var (terminated, shocked, voided, operated) =
            (await RegisterAsync(frontOffice), await RegisterAsync(frontOffice), await RegisterAsync(frontOffice), await RegisterAsync(frontOffice));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var facilityId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("facilityId").GetGuid();
            ProcedureRecord Before(Guid patientId, string code)
            {
                var record = ProcedureRecord.Record(patientId, new ProcedureDetails(DateTimeOffset.UtcNow.AddYears(-1), code, CodeReleases.Icd10Pcs,
                    "Recorded before the upgrade", null, "Dr. Menon", ProcedureSide.NotApplicable, null, null, null), null);
                record.FacilityId = facilityId;
                db.Add(record);
                return record;
            }

            Before(terminated, "10A07ZX");
            Before(shocked, "gzb4zzz");
            Before(voided, "10A07Z6").MarkEnteredInError("Recorded for the wrong patient", null, DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
            Before(operated, "0DTJ4ZZ");
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            (await CategoriesAsync(admin, terminated)).ShouldBeEmpty();
            await db.Database.ExecuteSqlRawAsync(Migrations.SensitiveProcedures.MarkRecordsOfSensitiveProcedures, TestContext.Current.CancellationToken);
        }

        (await CategoriesAsync(admin, terminated)).ShouldBe([SensitiveCategory.Mtp]);
        (await CategoriesAsync(admin, shocked)).ShouldBe([SensitiveCategory.MentalHealth]);
        (await CategoriesAsync(admin, voided)).ShouldBeEmpty("a procedure entered in error is known to be wrong: the upgrade does not close the record for it");
        (await CategoriesAsync(admin, operated)).ShouldBeEmpty();
        (await CategoriesAsync(admin, treated)).ShouldBe([SensitiveCategory.MentalHealth], "the upgrade only adds a category");
    }

    /// <summary>A procedure from the catalogue, done an hour ago, recorded on the chart by the doctor who did it.</summary>
    private static Task<ProcedureDto> RecordAsync(ApiClient doctor, Guid patientId, string code) =>
        doctor.PostAsync<ProcedureDto>($"/api/v1/emr/patients/{patientId}/procedures", new RecordProcedureRequest(
            null, DateTimeOffset.UtcNow.AddHours(-1), code, null, null, null, ProcedureSide.NotApplicable, null, null, null));

    private static async Task<IReadOnlyList<SensitiveCategory>> CategoriesAsync(ApiClient admin, Guid patientId) =>
        (await admin.GetAsync<SensitiveCategoriesDto>($"/api/v1/patients/{patientId}/sensitive-categories")).Categories;

    private static JsonElement Resource(JsonElement bundle, Guid id) =>
        bundle.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource")).Single(r => r.GetProperty("id").GetString() == id.ToString("D"));
}
