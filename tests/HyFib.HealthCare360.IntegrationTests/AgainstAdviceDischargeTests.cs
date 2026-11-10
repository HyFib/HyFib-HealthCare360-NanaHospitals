using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Platform.Files;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A patient leaving against medical advice (maker-checker, slice 8), over the real API and PostgreSQL: on the ward (LAMA,
/// DAMA) and from the emergency department (LAMA), the record says who signed the form, or that nobody would; the doctor
/// who explained the risks, from their own sign-in or their own confirmation at the nurse's screen; and a witness, another
/// member of staff confirming with their own user name and password (ADR-014), never the doctor. A scanned copy of the
/// signed form can go with it.
/// </summary>
public sealed class AgainstAdviceDischargeTests(HospitalServer server)
{
    private const string Er = "/api/v1/emergency";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_nurse_records_dama_only_with_the_doctors_confirmation_and_a_witness_who_is_not_the_doctor()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        await InitiateAndClearAsync(server, nurse, stay.Id);
        var dama = new CompleteDischargeRequest(DischargeType.Dama, null, "Family wants to take him to their home town; risks of leaving explained", NotifyPatient: false);

        // Free text alone no longer closes the stay.
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), dama)).ShouldBe((HttpStatusCode.BadRequest, null));

        // A witness, but no doctor: the nurse is not the doctor who explained the risks.
        var witness = await server.WitnessAsync(nurse, Users.SecondNurse, DischargeService.AgainstAdviceWitness.Key, patient.Id);
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), dama with { AgainstAdvice = Refused(null, witness.ConfirmationId) }))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "against_advice.doctor_required"));

        // The doctor confirms at the nurse's screen, and also witnesses: the witness is someone else.
        var byDoctor = await server.WitnessAsync(nurse, Users.Doctor, AgainstAdviceDeclarations.DoctorPurpose.Key, patient.Id);
        var doctorAsWitness = await server.WitnessAsync(nurse, Users.Doctor, DischargeService.AgainstAdviceWitness.Key, patient.Id);
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), dama with { AgainstAdvice = Refused(byDoctor.ConfirmationId, doctorAsWitness.ConfirmationId) }))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "against_advice.witness_is_doctor"));
        (await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}")).Status.ShouldBe(AdmissionStatus.DischargeInitiated);

        // The family refuses to sign: the doctor and the second nurse confirm it, and the stay closes.
        var closed = await nurse.PostAsync<DischargeResult>(Complete(stay.Id), dama with { AgainstAdvice = Refused(byDoctor.ConfirmationId, witness.ConfirmationId) });
        closed.Status.ShouldBe(AdmissionStatus.Dama);
        var declared = (await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}")).Declaration.ShouldNotBeNull();
        (declared.Type, declared.SignedBy, declared.RisksExplainedBy, declared.WitnessName, declared.DuesOutstanding)
            .ShouldBe((DischargeType.Dama, "Refused to sign", byDoctor.Name, witness.Name, null));
        (await AuditAsync(server, patient.Id, "discharge.against_advice")).ShouldContain($"witnessed by {witness.Name}");
        await CleanBedAsync(nurse, stay.Id);
    }

    [Fact]
    public async Task The_doctor_records_lama_signed_by_a_relative_with_the_scanned_form_and_a_witness_once()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        await InitiateAndClearAsync(server, nurse, stay.Id);
        var form = await UploadAsync(doctor, patient.Id);
        var witness = await server.WitnessAsync(doctor, Users.Nurse, DischargeService.AgainstAdviceWitness.Key, patient.Id);
        var relative = new AgainstAdviceRequest(AgainstAdviceSignatory.Relative, "Ravi Kumar", null, null, witness.ConfirmationId, form);
        var lama = new CompleteDischargeRequest(DischargeType.Lama, null, "Left against advice after the risks of stopping antibiotics were explained",
            NotifyPatient: false, AgainstAdvice: relative);

        // A relative who signs is named with the relationship.
        (await RefusalAsync(doctor, HttpMethod.Post, Complete(stay.Id), lama)).ShouldBe((HttpStatusCode.BadRequest, null));

        // The doctor is the one signed in: no confirmation of their own is needed.
        var closed = await doctor.PostAsync<DischargeResult>(Complete(stay.Id), lama with { AgainstAdvice = relative with { Relationship = "son" } });
        closed.Status.ShouldBe(AdmissionStatus.Lama);
        var declared = (await doctor.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}")).Declaration.ShouldNotBeNull();
        (declared.SignedBy, declared.WitnessName, declared.SignedFormFileId).ShouldBe(("Signed by Ravi Kumar (son)", witness.Name, form));

        // The witness's confirmation was spent on this stay: it witnesses nothing else.
        var (other, otherStay) = await AdmitAsync(server, frontOffice, doctor);
        await InitiateAndClearAsync(server, nurse, otherStay.Id);
        var reused = lama with { AgainstAdvice = new AgainstAdviceRequest(AgainstAdviceSignatory.Patient, null, null, null, witness.ConfirmationId, null) };
        (await RefusalAsync(doctor, HttpMethod.Post, Complete(otherStay.Id), reused)).ShouldBe((HttpStatusCode.UnprocessableEntity, "witness.confirmation_unusable"));
        var fresh = await server.WitnessAsync(doctor, Users.Nurse, DischargeService.AgainstAdviceWitness.Key, other.Id);
        (await doctor.PostAsync<DischargeResult>(Complete(otherStay.Id),
            reused with { AgainstAdvice = reused.AgainstAdvice! with { WitnessConfirmationId = fresh.ConfirmationId } })).Status.ShouldBe(AdmissionStatus.Lama);
        await CleanBedAsync(nurse, stay.Id);
        await CleanBedAsync(nurse, otherStay.Id);
    }

    [Fact]
    public async Task Lama_from_the_emergency_department_is_witnessed()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var row = (await nurse.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id);
        var triaged = await nurse.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{row.CaseId}/triage", EmergencyDepartmentTests.Triage(row.Version));
        var lama = new DispositionRequest(EmergencyDisposition.Lama, "Risks of leaving with a head injury explained; wants to go home", null, triaged.Version);

        // A note of who signed is not enough: a witness confirms.
        (await RefusalAsync(doctor, HttpMethod.Post, $"{Er}/cases/{row.CaseId}/disposition", lama)).ShouldBe((HttpStatusCode.BadRequest, null));

        // A witness from outside the department's clinical staff (the front office) is not one.
        await server.SignInAsync(Users.FrontOffice);
        using (var notTheirs = await server.FromOwnAddress(doctor).SendRawAsync(HttpMethod.Post, "/api/v1/auth/witness",
                   new Platform.Identity.ConfirmWitnessRequest(Users.FrontOffice, server.Password, null, AgainstAdviceDeclarations.ErWitness.Key, patientId)))
        {
            notTheirs.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await notTheirs.Content.ReadAsStringAsync(Ct));
        }

        var witness = await server.WitnessAsync(doctor, Users.Nurse, AgainstAdviceDeclarations.ErWitness.Key, patientId);
        var left = await doctor.PostAsync<EmergencyCaseDetail>($"{Er}/cases/{row.CaseId}/disposition",
            lama with { AgainstAdvice = new AgainstAdviceRequest(AgainstAdviceSignatory.Patient, null, null, null, witness.ConfirmationId, null) });
        left.Disposition.ShouldBe(EmergencyDisposition.Lama);
        var declared = left.AgainstAdvice.ShouldNotBeNull();
        (declared.SignedBy, declared.WitnessName, declared.DoctorName).ShouldBe((AgainstAdviceSignatory.Patient, witness.Name, left.DisposedByName));
    }

    private static AgainstAdviceRequest Refused(Guid? doctorConfirmation, Guid witnessConfirmation) =>
        new(AgainstAdviceSignatory.RefusedToSign, null, null, doctorConfirmation, witnessConfirmation, null);

    /// <summary>The signed form, scanned and uploaded to the document store for the patient.</summary>
    private static async Task<Guid> UploadAsync(ApiClient client, Guid patientId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("%PDF-1.4\n1 0 obj<<>>endobj\n%%EOF"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "lama-form.pdf");
        form.Add(new StringContent("consent"), "category");
        form.Add(new StringContent(patientId.ToString()), "patientId");
        using var response = await client.Http.PostAsync("/api/v1/files", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<StoredFileDto>(ApiClient.Json, Ct))!.Id;
    }
}
