using System.Net;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Platform.Approvals;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A stay closed as absconded (maker-checker, slice 8), over the real API and PostgreSQL: the ward reports when the
/// patient was last seen, who searched and who was told, and the nurse in charge, never the one who asked, approves
/// closing it. A medico-legal stay needs the police told first. Until it is approved the patient stays admitted; billing
/// clearance is still skipped, and what was due is recorded.
/// </summary>
public sealed class AbscondedDischargeTests(HospitalServer server)
{
    private static AbscondedReport Report(bool police = false) => new(
        DateTimeOffset.UtcNow.AddMinutes(-45), "Ward staff and security searched the floor, the toilets and the grounds", police,
        police ? "Adyar PS" : null, police ? "GD 114/26" : null, true, "The patient's daughter, by phone");

    [Fact]
    public async Task Absconded_waits_for_the_nurse_in_charge_with_the_report_and_closes_with_the_dues_recorded()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var inCharge = await server.SignInAsync(Users.HospitalAdmin);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        var absconded = new CompleteDischargeRequest(DischargeType.Absconded, null, "Not in the ward at the 6 pm round", NotifyPatient: false, Absconded: Report());

        // The report is required: who searched, when last seen, who was told.
        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), absconded with { Absconded = null })).ShouldBe((HttpStatusCode.BadRequest, null));

        // Asked: the patient stays admitted, and the stay shows it waiting.
        var asked = await AcceptedAsync(nurse, HttpMethod.Post, Complete(stay.Id), absconded);
        asked.Kind.ShouldBe(DischargeService.AbscondedKind);
        asked.Details.ShouldContain(d => d.Label == "Searched by" && d.Value.StartsWith("Ward staff", StringComparison.Ordinal));
        asked.Details.ShouldContain(d => d.Label == "Status" && d.Value == "admitted → absconded");
        var waiting = await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
        (waiting.Status, waiting.PendingDischarge?.ApprovalId, waiting.PendingDischarge?.Type).ShouldBe((AdmissionStatus.Admitted, asked.Id, DischargeType.Absconded));

        // The nurse who asked, or the doctor, does not decide it; the nurse in charge does.
        (await RefusalAsync(nurse, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        (await RefusalAsync(doctor, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        await MakerChecker.ApproveAsync(inCharge, await inCharge.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}"));

        var closed = await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
        (closed.Status, closed.PendingDischarge).ShouldBe((AdmissionStatus.Absconded, null));
        var declared = closed.Declaration.ShouldNotBeNull();
        (declared.SearchedBy, declared.RelativesInformed, declared.PoliceInformed, declared.DuesOutstanding is not null, declared.ApprovedBy is not null)
            .ShouldBe((Report().SearchedBy, true, false, true, true));
        var audit = await AuditAsync(server, patient.Id, "discharge.absconded");
        audit.ShouldContain("asked for by");
        audit.ShouldContain("approved by");
        await CleanBedAsync(nurse, stay.Id);
    }

    [Fact]
    public async Task A_medico_legal_stay_needs_the_police_told_and_whoever_asks_never_approves()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var inCharge = await server.SignInAsync(Users.HospitalAdmin);
        var (_, stay) = await AdmitAsync(server, frontOffice, doctor, mlc: true);
        var absconded = new CompleteDischargeRequest(DischargeType.Absconded, null, "Left the ward while the police guard was away", NotifyPatient: false, Absconded: Report());

        (await RefusalAsync(nurse, HttpMethod.Post, Complete(stay.Id), absconded)).ShouldBe((HttpStatusCode.UnprocessableEntity, "discharge.police_intimation_required"));

        // The nurse in charge asks herself: someone else who is in charge decides it, never her.
        var asked = await AcceptedAsync(inCharge, HttpMethod.Post, Complete(stay.Id), absconded with { Absconded = Report(police: true) });
        asked.Details.ShouldContain(d => d.Label == "Police told" && d.Value.Contains("GD 114/26"));
        (await RefusalAsync(inCharge, HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));

        // The patient is found and brought back: the request is withdrawn and the stay goes on.
        await inCharge.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/withdraw", new WithdrawApprovalRequest(asked.Version));
        var back = await nurse.GetAsync<AdmissionDetail>($"{Admissions}/{stay.Id}");
        (back.Status, back.PendingDischarge).ShouldBe((AdmissionStatus.Admitted, null));
        await CancelAsync(frontOffice, nurse, stay.Id);
    }
}
