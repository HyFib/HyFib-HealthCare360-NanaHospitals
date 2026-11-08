using System.Net;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.VitalRegistration;
using HyFib.HealthCare360.Modules.Clinical.Services;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.SharedKernel;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A death certificate a second doctor confirmed, marked entered in error (maker-checker, slice 8), over the real API and
/// PostgreSQL: making it took two doctors, so undoing it does too. The certifier asks; another doctor who confirms
/// certificates agrees, never the one who asked; until then the certificate and the patient's deceased status stand. A
/// certificate reported to the Registrar is not voided here at all.
/// </summary>
public sealed class DeathCertificateVoidTests(HospitalServer server)
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    [Fact]
    public async Task Voiding_a_confirmed_certificate_waits_for_a_second_doctor_and_the_deceased_status_stands_until_then()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var second = await server.SignInAsync(Users.SecondDoctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var mrd = await server.SignInAsync(Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1948, 6, 1));
        var certificate = BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-3));
        var death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", certificate);
        var confirmed = await DeathCertificateConfirmationTests.ConfirmAsync(server, death);
        var path = $"/api/v1/mrd/deaths/{death.Id}/entered-in-error";

        // The certifier asks: 202, and nothing changes yet.
        var asked = await AcceptedAsync(doctor, HttpMethod.Post, path, new VitalEntryErrorRequest("Certified on the wrong patient: bed 12, not bed 21", confirmed.Version));
        (asked.Kind, asked.SubjectId, asked.Summary).ShouldBe(
            (DeathCertificateVoidApproval.Key, death.Id, $"Death certificate {death.SerialNumber}: confirmed → entered in error"));
        asked.Details.ShouldContain(d => d.Label == "Confirmed by");
        asked.Details.ShouldContain(d => d.Label == "What it reaches");
        var waiting = await doctor.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}");
        (waiting.Status, waiting.VoidWaitingSince is not null).ShouldBe((VitalReportStatus.Recorded, true));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).IsDeceased.ShouldBeTrue();

        // Nothing is corrected or reported under the waiting request.
        (await RefusalAsync(doctor, HttpMethod.Put, $"/api/v1/mrd/deaths/{death.Id}", certificate with { Version = waiting.Version }))
            .ShouldBe((HttpStatusCode.Conflict, "death.void_waiting"));
        (await RefusalAsync(mrd, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/report", new ReportToRegistrarRequest(Today, "D-2026-TN-00811", waiting.Version)))
            .ShouldBe((HttpStatusCode.Conflict, "death.void_waiting"));

        // Not the doctor who asked, not a nurse; the second doctor agrees.
        (await RefusalAsync(doctor, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await RefusalAsync(nurse, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        var approved = await MakerChecker.ApproveAsync(second, await second.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}"));
        approved.Outcome.ShouldBe($"Death certificate {death.SerialNumber} entered in error; the patient is no longer recorded as deceased");

        var voided = await doctor.GetAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}");
        (voided.Status, voided.ErrorReason, voided.VoidWaitingSince).ShouldBe((VitalReportStatus.EnteredInError, "Certified on the wrong patient: bed 12, not bed 21", null));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).IsDeceased.ShouldBeFalse();
        var audit = await AuditAsync(server, patient.Id, "death.voided");
        audit.ShouldContain("asked for by");
        audit.ShouldContain("approved by");
    }

    [Fact]
    public async Task A_certificate_reported_to_the_registrar_is_not_voided_here_and_an_unconfirmed_one_is_at_once()
    {
        var doctor = await server.SignInAsync(Users.Doctor);
        var mrd = await server.SignInAsync(Users.MrdOfficer);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);

        // Reported: a correction goes through the Registrar.
        var reportedPatient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1946, 2, 1));
        var death = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(reportedPatient.Id, DateTimeOffset.UtcNow.AddHours(-5)));
        var confirmed = await DeathCertificateConfirmationTests.ConfirmAsync(server, death);
        var reported = await mrd.PostAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{death.Id}/report", new ReportToRegistrarRequest(Today, "D-2026-TN-00812", confirmed.Version));
        (await RefusalAsync(doctor, HttpMethod.Post, $"/api/v1/mrd/deaths/{death.Id}/entered-in-error", new VitalEntryErrorRequest("Wrong patient", reported.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "vital.reported"));
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{reportedPatient.Id}")).IsDeceased.ShouldBeTrue();

        // Not yet confirmed (its confirmation withdrawn): the certifier marks it at once, as before.
        var patient = await BirthsAndDeathsTests.RegisterAsync(frontOffice, Gender.Male, new DateOnly(1946, 2, 1));
        var unconfirmed = await doctor.PostAsync<DeathRecordDto>("/api/v1/mrd/deaths", BirthsAndDeathsTests.Certificate(patient.Id, DateTimeOffset.UtcNow.AddHours(-5)));
        var request = await DeathCertificateConfirmationTests.WaitingRequestAsync(doctor, unconfirmed.Id);
        await doctor.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{request.Id}/withdraw", new WithdrawApprovalRequest(request.Version));
        (await doctor.PostAsync<DeathRecordDto>($"/api/v1/mrd/deaths/{unconfirmed.Id}/entered-in-error", new VitalEntryErrorRequest("Wrong patient", unconfirmed.Version)))
            .Status.ShouldBe(VitalReportStatus.EnteredInError);
        (await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{patient.Id}")).IsDeceased.ShouldBeFalse();
    }
}
