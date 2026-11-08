using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A stay closed as expired (maker-checker, slice 8), over the real API and PostgreSQL: only with the patient's death
/// certificate (MCCD) a second doctor has confirmed; the ward is told why it waits until then. Billing clearance is still
/// skipped (a body is not held for dues), and what was due is recorded. A medico-legal death whose certificate waits for
/// the post-mortem is closed only when a second doctor approves.
/// </summary>
public sealed class ExpiredDischargeTests(HospitalServer server)
{
    private static readonly DateTimeOffset DiedAt = DateTimeOffset.UtcNow.AddHours(-2);

    [Fact]
    public async Task A_stay_is_closed_as_expired_only_with_a_death_certificate_a_second_doctor_confirmed()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        var expired = new CompleteDischargeRequest(DischargeType.Expired, null, "Declared dead at 06:10 by the duty doctor", NotifyPatient: false);

        // No certificate: the ward is told to certify the cause of death first, and the stay goes on.
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), expired)).ShouldBe((HttpStatusCode.UnprocessableEntity, "discharge.death_certificate_required"));
        (await nurse.GetAsync<DischargeDeathCertificateDto>($"{Admissions}/{stay.Id}/discharge/death-certificate")).Settled.ShouldBeFalse();

        // Certified, and waiting for the second doctor: the discharge waits, and the ward sees why.
        var death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(patient.Id, DiedAt) with { AdmissionId = stay.Id });
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), expired)).ShouldBe((HttpStatusCode.UnprocessableEntity, "discharge.death_certificate_unconfirmed"));
        var waiting = await nurse.GetAsync<DischargeDeathCertificateDto>($"{Admissions}/{stay.Id}/discharge/death-certificate");
        (waiting.SerialNumber, waiting.Settled, waiting.ForThisStay, waiting.ConfirmationWaitingSince is not null).ShouldBe((death.SerialNumber, false, true, true));
        waiting.Status.ShouldContain("not yet confirmed by a second doctor");
        (await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}")).Status.ShouldBe(AdmissionStatus.Admitted);

        // Confirmed by the second doctor: closed at once, without billing clearance, with the certificate and the dues recorded.
        await DeathCertificateConfirmationTests.ConfirmAsync(server, death);
        (await nurse.GetAsync<DischargeDeathCertificateDto>($"{Admissions}/{stay.Id}/discharge/death-certificate")).Settled.ShouldBeTrue();
        (await nurse.PostAsync<DischargeResult>(Complete(stay.Id), expired)).Status.ShouldBe(AdmissionStatus.Expired);
        var closed = await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
        var declared = closed.Declaration.ShouldNotBeNull();
        (declared.Type, declared.DeathCertificateNumber, declared.CertificatePendingPostMortem, declared.DuesOutstanding is not null)
            .ShouldBe((DischargeType.Expired, death.SerialNumber, false, true));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).IsDeceased.ShouldBeTrue();
        (await AuditAsync(server, patient.Id, "discharge.expired")).ShouldContain(death.SerialNumber);
        await CleanBedAsync(nurse, stay.Id);
    }

    [Fact]
    public async Task A_medico_legal_death_pending_post_mortem_is_closed_only_when_a_second_doctor_approves()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var second = await server.SignInAsync(Users.SecondDoctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var pending = new CompleteDischargeRequest(DischargeType.Expired, null, "Brought in after a fall; police informed; body sent for post-mortem",
            NotifyPatient: false, CertificatePendingPostMortem: true);

        // Not a medico-legal case: the certificate is made first, whatever is said.
        var (_, ordinary) = await AdmitAsync(server, frontOffice, doctor);
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(ordinary.Id), pending)).ShouldBe((HttpStatusCode.UnprocessableEntity, "discharge.postmortem_not_mlc"));
        await CancelAsync(frontOffice, nurse, ordinary.Id);

        // A medico-legal stay: the doctor asks, and the patient stays admitted (not deceased) until a second doctor agrees.
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor, mlc: true);
        var asked = await AcceptedAsync(doctor, HttpMethod.Post, Complete(stay.Id), pending);
        (asked.Kind, asked.SubjectId).ShouldBe((DischargeService.PendingPostMortemKind, stay.Id));
        asked.Details.ShouldContain(d => d.Label == "Death certificate");
        asked.Details.ShouldContain(d => d.Label == "Dues");
        var waiting = await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
        (waiting.Status, waiting.PendingDischarge?.ApprovalId).ShouldBe((AdmissionStatus.Admitted, asked.Id));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).IsDeceased.ShouldBeFalse();
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), pending)).ShouldBe((HttpStatusCode.Conflict, "approval.already_requested"));

        // Not the doctor who asked, not a nurse; the second doctor approves.
        (await RefusalAsync(doctor, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await RefusalAsync(nurse, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        await MakerChecker.ApproveAsync(second, await second.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}"));

        var closed = await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
        closed.Status.ShouldBe(AdmissionStatus.Expired);
        var declared = closed.Declaration.ShouldNotBeNull();
        (declared.CertificatePendingPostMortem, declared.DeathCertificateNumber, declared.ApprovedBy is not null).ShouldBe((true, null, true));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).IsDeceased.ShouldBeTrue("the mortuary receives a body only of a patient recorded deceased");
        var audit = await AuditAsync(server, patient.Id, "discharge.expired_pending_postmortem");
        audit.ShouldContain("asked for by");
        audit.ShouldContain("approved by");
        await CleanBedAsync(nurse, stay.Id);
    }
}
