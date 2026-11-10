using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Notes;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.ScopedRecordSharingTests;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Gap #27 through the whole server: the notes clinicians sign reach another provider through the FHIR API as
/// DocumentReferences, drafts never; an addendum appends to its note; a counselling note goes only under a consent that
/// names counselling notes, labelled restricted; and a note cannot name another patient's stay.
/// </summary>
public sealed class ClinicalNoteSharingTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Progress = "Afebrile since morning. Chest clear.\nஉணவு: மென்மையான உணவு.";

    [Fact]
    public async Task A_provider_reads_the_signed_notes_a_consent_for_clinical_notes_covers_and_never_a_draft()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var encounter = await AbdmExportTests.ConsultAsync(doctor, patient, sign: false);
        var notes = $"/api/v1/emr/patients/{patient}/notes";

        // A progress note signed, a procedure note left as a draft, a counselling note signed, and an addendum to the
        // progress note, signed.
        var progress = await SignAsync(doctor, await doctor.PostAsync<ClinicalNoteDto>(notes,
            new WriteNoteRequest(encounter, null, ClinicalNoteType.ProgressNote, "Day 2 review", Progress)));
        var draft = await doctor.PostAsync<ClinicalNoteDto>(notes,
            new WriteNoteRequest(encounter, null, ClinicalNoteType.Procedure, "Ascitic tap", "Planned for tomorrow under ultrasound guidance."));
        var counselling = await SignAsync(doctor, await doctor.PostAsync<ClinicalNoteDto>(notes,
            new WriteNoteRequest(encounter, null, ClinicalNoteType.Counselling, "Pre-test counselling", "Counselled before the HIV test; she agreed to it.")));
        var addendum = await SignAsync(doctor, await doctor.PostAsync<ClinicalNoteDto>($"/api/v1/emr/notes/{progress.Id}/addendum",
            new AddendumRequest("Wife reports a rash on both forearms since last night.")));

        // The patient shares their clinical notes, and nothing else, with the partner for treatment.
        await ShareAsync(frontOffice, patient, new RecordSharingScope([partnerId], [SharingPurpose.Treatment], [SharedRecordKind.ClinicalNotes], null, null, null));

        var search = await FhirAsync(partner, $"DocumentReference?patient={patient}", "TREAT");
        Matches(search.Body).ShouldBe([Id(progress), Id(addendum)], ignoreOrder: true);
        Withheld(search.Body).ShouldBeTrue("the counselling note is left out, and said to be");

        var doctorName = (await doctor.GetAsync<PagedResult<ClinicalNoteDto>>(notes)).Items.Single(n => n.Id == progress.Id).AuthorName;
        doctorName.ShouldNotBeNullOrWhiteSpace();
        var note = Resource(search.Body, Id(progress));
        Code(note.GetProperty("type")).ShouldBe(("http://loinc.org", "11506-3"));
        note.GetProperty("docStatus").GetString().ShouldBe("final");
        note.GetProperty("description").GetString().ShouldBe("Day 2 review");
        note.GetProperty("author")[0].GetProperty("display").GetString().ShouldBe(doctorName);
        note.GetProperty("author")[0].GetProperty("type").GetString().ShouldBe("Practitioner");
        note.GetProperty("authenticator").GetProperty("display").GetString().ShouldBe(doctorName, "the doctor signed it");
        note.GetProperty("authenticator").GetProperty("type").GetString().ShouldBe("Practitioner", "the signer's practitioner, looked up from their account");
        note.GetProperty("custodian").GetProperty("display").GetString().ShouldNotBeNullOrWhiteSpace("the facility where it was written");
        note.GetProperty("context").GetProperty("encounter")[0].GetProperty("reference").GetString().ShouldBe($"Encounter/enc-{encounter:D}");
        var attachment = note.GetProperty("content")[0].GetProperty("attachment");
        attachment.GetProperty("contentType").GetString().ShouldBe("text/plain; charset=utf-8");
        Encoding.UTF8.GetString(attachment.GetProperty("data").GetBytesFromBase64()).ShouldBe(Progress, "the note as it was signed, Tamil and all");
        note.TryGetProperty("securityLabel", out _).ShouldBeFalse();

        var appended = Resource(search.Body, Id(addendum)).GetProperty("relatesTo")[0];
        appended.GetProperty("code").GetString().ShouldBe("appends");
        appended.GetProperty("target").GetProperty("reference").GetString().ShouldBe($"DocumentReference/{Id(progress)}");

        // A draft is no note at all, to anyone; the counselling note is outside the consent.
        (await FhirAsync(partner, $"DocumentReference/{Id(draft)}", "TREAT")).Status.ShouldBe(404);
        (await FhirAsync(partner, $"DocumentReference/{Id(counselling)}", "TREAT")).Refusal.ShouldBe((403, "consent-required"));
        (await FhirAsync(partner, $"DocumentReference/{Id(progress)}", "TREAT")).Status.ShouldBe(200);
        Matches((await FhirAsync(partner, $"DocumentReference?patient={patient}&type=http://loinc.org|28570-0", "TREAT")).Body)
            .ShouldBeEmpty("the procedure note is still a draft");
    }

    [Fact]
    public async Task A_counselling_note_goes_only_under_a_consent_that_names_counselling_notes_labelled_restricted()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, partnerId, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var notes = $"/api/v1/emr/patients/{patient}/notes";
        var progress = await SignAsync(doctor, await doctor.PostAsync<ClinicalNoteDto>(notes,
            new WriteNoteRequest(null, null, ClinicalNoteType.ProgressNote, null, "Review after the antibiotics: better.")));
        var counselling = await SignAsync(doctor, await doctor.PostAsync<ClinicalNoteDto>(notes,
            new WriteNoteRequest(null, null, ClinicalNoteType.Counselling, null, "Talked through the result with her and her husband.")));

        // A consent to share the whole record shares every note but the counselling note.
        await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents",
            new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null,
                WitnessConfirmationId: await server.RecordSharingWitnessAsync(frontOffice, patient)));
        var search = await FhirAsync(partner, $"DocumentReference?patient={patient}", null);
        Matches(search.Body).ShouldBe([Id(progress)]);
        Withheld(search.Body).ShouldBeTrue();
        (await FhirAsync(partner, $"DocumentReference/{Id(counselling)}", null)).Refusal.ShouldBe((403, "consent-required"));

        // Once the patient names counselling notes for the partner, it goes, labelled restricted.
        await ShareAsync(frontOffice, patient, new RecordSharingScope([partnerId], null, [SharedRecordKind.CounsellingNotes], null, null, null));
        var read = await FhirAsync(partner, $"DocumentReference/{Id(counselling)}", null);
        read.Status.ShouldBe(200);
        Code(read.Body.GetProperty("type")).ShouldBe(("http://loinc.org", "47042-7"));
        Code(read.Body.GetProperty("securityLabel")[0]).ShouldBe(("http://terminology.hl7.org/CodeSystem/v3-Confidentiality", "R"));
        var label = read.Body.GetProperty("meta").GetProperty("security")[0];
        (label.GetProperty("system").GetString(), label.GetProperty("code").GetString())
            .ShouldBe(("http://terminology.hl7.org/CodeSystem/v3-Confidentiality", "R"));
        Matches((await FhirAsync(partner, $"DocumentReference?patient={patient}", null)).Body).ShouldBe([Id(progress), Id(counselling)], ignoreOrder: true);
    }

    [Fact]
    public async Task A_note_names_only_the_patients_own_stay_and_encounter()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (partner, _, _) = await PartnerAsync(server);
        var patient = await RegisterAsync(frontOffice);
        var someoneElse = await RegisterAsync(frontOffice);
        var stay = await AdmitAsync(someoneElse);
        var theirConsultation = await AbdmExportTests.ConsultAsync(doctor, someoneElse, sign: false);
        try
        {
            using (var refused = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/emr/patients/{patient}/notes",
                       new WriteNoteRequest(null, stay.Id, ClinicalNoteType.ProgressNote, null, "Comfortable overnight.")))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
                var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
                problem.GetProperty("errors").GetProperty("admissionId")[0].GetString().ShouldBe("This admission does not belong to the patient.");
            }

            using (var unknown = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/emr/patients/{patient}/notes",
                       new WriteNoteRequest(null, Guid.CreateVersion7(), ClinicalNoteType.ProgressNote, null, "Comfortable overnight.")))
            {
                unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "no such stay");
            }

            var own = await doctor.PostAsync<ClinicalNoteDto>($"/api/v1/emr/patients/{someoneElse}/notes",
                new WriteNoteRequest(null, stay.Id, ClinicalNoteType.ProgressNote, null, "Comfortable overnight."));
            own.AdmissionId.ShouldBe(stay.Id);

            // A note stored before these checks, filed against someone else's consultation and stay, is shared without
            // either: it never points another provider at another patient's visit.
            var misfiled = await MisfiledNoteAsync(patient, theirConsultation, stay.Id);
            await frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents",
                new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null,
                    WitnessConfirmationId: await server.RecordSharingWitnessAsync(frontOffice, patient)));
            var read = await FhirAsync(partner, $"DocumentReference/note-{misfiled:D}", null);
            read.Status.ShouldBe(200);
            read.Body.TryGetProperty("context", out _).ShouldBeFalse("neither the consultation nor the stay is the patient's");
        }
        finally
        {
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            await admin.PostAsync($"/api/v1/inpatient/admissions/{stay.Id}/cancel", new CancelAdmissionRequest("Admitted for a test of clinical notes"));
        }
    }

    private static string Id(ClinicalNoteDto note) => $"note-{note.Id:D}";

    /// <summary>A signed note of the patient that names another patient's consultation and stay, as an older HC360 could store.</summary>
    private async Task<Guid> MisfiledNoteAsync(Guid patientId, Guid encounterId, Guid admissionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var note = ClinicalNote.Draft(patientId, ClinicalNoteType.ProgressNote, "Ward round", "Seen on the round; comfortable.");
        note.FacilityId = await db.Set<Patient>().Where(p => p.Id == patientId).Select(p => p.RegisteredFacilityId).SingleAsync(Ct);
        note.EncounterId = encounterId;
        note.AdmissionId = admissionId;
        note.Sign(null, DateTimeOffset.UtcNow).IsSuccess.ShouldBeTrue();
        db.Add(note);
        await db.SaveChangesAsync(Ct);
        return note.Id;
    }

    private static async Task<ClinicalNoteDto> SignAsync(ApiClient author, ClinicalNoteDto note) =>
        await author.PostAsync<ClinicalNoteDto>($"/api/v1/emr/notes/{note.Id}/sign", new SignNoteRequest(note.Version));

    private static Task<Created> ShareAsync(ApiClient frontOffice, Guid patient, RecordSharingScope scope) =>
        frontOffice.PostAsync<Created>($"/api/v1/patients/{patient}/consents",
            new CaptureConsentRequest(ConsentType.DataSharing, null, true, null, null, null, null, null, scope));

    /// <summary>The front office admits the patient under the doctor to a bed of the female general ward of their own.</summary>
    private async Task<AdmissionCreated> AdmitAsync(Guid patientId)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWF");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Emergency, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Fever with a rash", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null, NotifyPatient: false));
    }

    /// <summary>A match of a search bundle by id (the bundle's outcome entry, saying records were withheld, has none).</summary>
    private static JsonElement Resource(JsonElement bundle, string id) =>
        bundle.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("resource"))
            .Single(r => r.TryGetProperty("id", out var value) && value.GetString() == id);

    private static (string? System, string? Code) Code(JsonElement concept)
    {
        var coding = concept.GetProperty("coding")[0];
        return (coding.GetProperty("system").GetString(), coding.GetProperty("code").GetString());
    }
}
