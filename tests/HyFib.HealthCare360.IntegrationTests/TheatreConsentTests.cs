using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.Analytics.Features.Journeys;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Files;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Informed consent before surgery and anaesthesia (NABH COP), over the real API and PostgreSQL. "Consent signed" on the
/// pre-op checklist, the WHO Sign-in and Time-out and the incision read the patient's consents as the front office recorded
/// them, not a tick: to the operation (a high-risk procedure) and, under any anaesthesia but local or topical, to the
/// anaesthesia, given since the booking, after the patient's latest refusal or withdrawal, and not relied on by another
/// operation's incision. In an emergency the operating surgeon goes ahead without them, never against the patient's refusal
/// or withdrawal, with the clinical reason and a second doctor who agrees at the surgeon's screen with their own user name
/// and password: audited at Critical, and the booking names both doctors. The waiver ends with another surgeon or another
/// slot. After the Sign-in only the case team changes the anaesthesia or the priority, with a reason, and a consent to
/// anaesthesia needed then stays needed.
/// </summary>
public sealed class TheatreConsentTests(HospitalServer server)
{
    private const string Required = "surgery.consent_required";
    private const string Emergency = "Ruptured spleen after a road accident, in shock; unconscious, no relative reachable";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RecordChecklistRequest ConsentTicked(SurgeryDetail detail) =>
        new([new ChecklistInput(PreOpChecklist.ConsentItem, ChecklistItemState.Done, null)], detail.Version);

    [Fact]
    public async Task The_incision_waits_for_the_patients_consent_recorded_at_the_front_office()
    {
        var cases = new TheatreCases(server);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparoscopic cholecystectomy");

        // Nothing is recorded under the patient's consents: the screen says what is missing, and the nurse's tick is refused.
        var detail = await nurse.GetAsync<SurgeryDetail>(c.Path);
        var consent = detail.Consent.ShouldNotBeNull();
        consent.Satisfied.ShouldBeFalse();
        consent.Checks.Select(x => (x.Kind, x.State)).ShouldBe(
            [(SurgicalConsentKind.Procedure, SurgicalConsentState.Missing), (SurgicalConsentKind.Anaesthesia, SurgicalConsentState.Missing)]);
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Put, $"{c.Path}/pre-op-checklist", ConsentTicked(detail)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, Required));

        // A planned operation does not go ahead on the emergency doctrine.
        var concurs = await server.WitnessAsync(doctor, Users.SecondDoctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        (await TheatreCases.RefusalAsync(doctor, HttpMethod.Post, $"{c.Path}/consent-waiver", new WaiveSurgicalConsentRequest(Emergency, concurs.ConfirmationId)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.consent_waiver_not_emergency"));

        // The consent to the operation alone is not enough under spinal anaesthesia.
        var procedure = await cases.ConsentAsync(c, ConsentType.HighRiskProcedure);
        detail = await nurse.GetAsync<SurgeryDetail>(c.Path);
        detail.Consent.ShouldNotBeNull().Checks.Select(x => x.State).ShouldBe([SurgicalConsentState.Recorded, SurgicalConsentState.Missing]);
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Put, $"{c.Path}/pre-op-checklist", ConsentTicked(detail)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, Required));

        // With the consent to anaesthesia recorded too, the checklist, the Sign-in and the Time-out go ahead.
        var anaesthesia = await cases.ConsentAsync(c, ConsentType.Anaesthesia);
        (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull().Satisfied.ShouldBeTrue();
        await cases.PreOpDoneAsync(c);
        await cases.SignedInAndTimedOutAsync(c);

        // The front office records the patient withdrawing his consent to the operation: the incision is refused.
        await frontOffice.PostAsync($"/api/v1/patients/consents/{procedure}/revoke", new RevokeConsentRequest("Wants to speak to his son before the operation"));
        (await TheatreCases.RefusalAsync(doctor, HttpMethod.Post, $"{c.Path}/start", null)).ShouldBe((HttpStatusCode.UnprocessableEntity, Required));
        var withdrawn = await nurse.GetAsync<SurgeryDetail>(c.Path);
        (withdrawn.Status, withdrawn.StartedAt).ShouldBe((SurgeryStatus.InPreOp, (DateTimeOffset?)null));
        withdrawn.Consent.ShouldNotBeNull().Checks.Single(x => x.Kind == SurgicalConsentKind.Procedure).State.ShouldBe(SurgicalConsentState.Withdrawn);

        // Consented again at the front office, the operation starts, and the booking names the consents it relied on.
        var again = await cases.ConsentAsync(c, ConsentType.HighRiskProcedure);
        await doctor.PostAsync($"{c.Path}/start");
        var started = await nurse.GetAsync<SurgeryDetail>(c.Path);
        started.Status.ShouldBe(SurgeryStatus.InProgress);
        var relied = started.Consent.ShouldNotBeNull();
        (relied.ProcedureConsentId, relied.AnaesthesiaConsentId).ShouldBe(((Guid?)again, (Guid?)anaesthesia));
    }

    [Fact]
    public async Task In_an_emergency_the_surgeon_goes_ahead_without_consent_with_a_second_doctor_who_agrees_and_it_is_audited_at_critical()
    {
        var cases = new TheatreCases(server);
        var surgeon = await server.SignInAsync(Users.Doctor);
        var anaesthetist = await server.SignInAsync(Users.SecondDoctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparotomy for a ruptured spleen", priority: SurgeryPriority.Emergency);
        var waiver = $"{c.Path}/consent-waiver";

        // The doctor who agrees is a doctor who may operate: a nurse cannot agree, nor waive.
        (await WitnessRefusalAsync(surgeon, Users.Nurse, c.PatientId)).ShouldBe("witness.not_allowed");
        var concurs = await server.WitnessAsync(surgeon, Users.SecondDoctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        using (var byNurse = await nurse.SendRawAsync(HttpMethod.Post, waiver, new WaiveSurgicalConsentRequest(Emergency, concurs.ConfirmationId)))
        {
            byNurse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        // The reason is the surgeon's clinical judgement, at least ten characters.
        (await TheatreCases.InvalidFieldsAsync(surgeon, HttpMethod.Post, waiver, new WaiveSurgicalConsentRequest("Emergency", concurs.ConfirmationId)))
            .ShouldContain("reason");

        // The anaesthetist is not the operating surgeon: he may agree to it, not waive it.
        var forTheAnaesthetist = await server.WitnessAsync(anaesthetist, Users.ThirdDoctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        (await TheatreCases.RefusalAsync(anaesthetist, HttpMethod.Post, waiver, new WaiveSurgicalConsentRequest(Emergency, forTheAnaesthetist.ConfirmationId)))
            .ShouldBe((HttpStatusCode.Forbidden, "surgery.consent_waiver_surgeon"));

        // The operating surgeon waives it, the anaesthetist agreeing at his screen: the booking names both, with the reason.
        await surgeon.PostAsync(waiver, new WaiveSurgicalConsentRequest(Emergency, concurs.ConfirmationId));
        var me = await surgeon.GetAsync<AuthUser>("/api/v1/auth/me");
        var consent = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        (consent.Satisfied, consent.Recorded).ShouldBe((true, false));
        var waived = consent.Waiver.ShouldNotBeNull();
        (waived.SurgeonName, waived.ConcurringName, waived.ConcurringUserId, waived.Reason).ShouldBe((me.DisplayName, concurs.Name, (Guid?)concurs.UserId, Emergency));

        // Audited at Critical, with the reason and both doctors.
        var (summary, severity) = await cases.AuditAsync(c.PatientId, "surgery.consent_waived");
        severity.ShouldBe(AuditSeverity.Critical);
        summary.ShouldContain(Emergency);
        summary.ShouldContain(concurs.Name);

        // Waived once: a second waiver is refused.
        var again = await server.WitnessAsync(surgeon, Users.ThirdDoctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        (await TheatreCases.RefusalAsync(surgeon, HttpMethod.Post, waiver, new WaiveSurgicalConsentRequest(Emergency, again.ConfirmationId)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.consent_waived"));

        // The day goes on without the consent: "consent signed" over the waiver, the Sign-in, the Time-out and the incision.
        await cases.PreOpDoneAsync(c);
        await cases.SignedInAndTimedOutAsync(c);
        await surgeon.PostAsync($"{c.Path}/start");
        var started = await nurse.GetAsync<SurgeryDetail>(c.Path);
        started.Status.ShouldBe(SurgeryStatus.InProgress);
        (started.Consent.ShouldNotBeNull().ProcedureConsentId, started.Consent.AnaesthesiaConsentId).ShouldBe(((Guid?)null, (Guid?)null));
    }

    [Fact]
    public async Task A_withdrawal_is_the_patients_latest_word_though_the_consent_was_recorded_twice()
    {
        var cases = new TheatreCases(server);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparoscopic appendicectomy");

        // The consent to the operation is recorded twice (again on the ward round), with the consent to anaesthesia.
        var first = await cases.ConsentAsync(c, ConsentType.HighRiskProcedure);
        await cases.ConsentAsync(c, ConsentType.HighRiskProcedure);
        await cases.ConsentAsync(c, ConsentType.Anaesthesia);
        await cases.PreOpDoneAsync(c);
        await cases.SignedInAndTimedOutAsync(c);

        // The screen names who recorded each consent and whether the signed form is attached.
        var recorder = await frontOffice.GetAsync<AuthUser>("/api/v1/auth/me");
        var recorded = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        recorded.Checks.ShouldAllBe(x => x.State == SurgicalConsentState.Recorded);
        recorded.Checks.ShouldAllBe(x => x.Consent!.CapturedByName == recorder.DisplayName && !x.Consent.SignedFormAttached);

        // The patient withdraws; the front office withdraws one of the two records. The other was given before the
        // withdrawal and does not undo it: the incision is refused.
        await frontOffice.PostAsync($"/api/v1/patients/consents/{first}/revoke", new RevokeConsentRequest("No longer wants the operation"));
        (await TheatreCases.RefusalAsync(doctor, HttpMethod.Post, $"{c.Path}/start", null)).ShouldBe((HttpStatusCode.UnprocessableEntity, Required));
        var withdrawn = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        var procedure = withdrawn.Checks.Single(x => x.Kind == SurgicalConsentKind.Procedure);
        (procedure.State, procedure.Consent?.Id, withdrawn.Satisfied).ShouldBe((SurgicalConsentState.Withdrawn, (Guid?)first, false));

        // He agrees again: the consent recorded after the withdrawal opens the incision, which relies on it.
        var again = await cases.ConsentAsync(c, ConsentType.HighRiskProcedure);
        await doctor.PostAsync($"{c.Path}/start");
        (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull().ProcedureConsentId.ShouldBe((Guid?)again);
    }

    [Fact]
    public async Task A_consent_another_operations_incision_relied_on_does_not_open_this_ones_gates()
    {
        var cases = new TheatreCases(server);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);

        // Two operations are booked for the patient before any consent; the consents recorded next are both "since the booking".
        var first = await cases.InPreOpAsync(Laterality.NotApplicable, "Exploratory laparotomy");
        var second = await cases.AnotherOperationAsync(first, "Relook laparotomy");
        var (procedure, anaesthesia) = await cases.ConsentedAsync(first);

        // The first operation goes ahead on them.
        await cases.PreOpDoneAsync(first);
        await cases.SignedInAndTimedOutAsync(first);
        await doctor.PostAsync($"{first.Path}/start");
        var relied = await nurse.GetAsync<SurgeryDetail>(first.Path);
        (relied.Consent!.ProcedureConsentId, relied.Consent.AnaesthesiaConsentId).ShouldBe(((Guid?)procedure, (Guid?)anaesthesia));

        // A consent is for one operation: the second one's gates stay shut until the patient consents to it.
        var detail = await nurse.GetAsync<SurgeryDetail>(second.Path);
        var consent = detail.Consent.ShouldNotBeNull();
        consent.Satisfied.ShouldBeFalse();
        consent.Checks.ShouldAllBe(x => x.State == SurgicalConsentState.OtherOperation && x.Consent!.ReliedOnBy == relied.BookingNumber);
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Put, $"{second.Path}/pre-op-checklist", ConsentTicked(detail)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, Required));

        // Had the first been another branch's operation, its consents would not count here either, and its booking number,
        // which this branch's lists do not show, is not given.
        await using (await AnotherBranchAsync(first.BookingId))
        {
            var elsewhere = (await nurse.GetAsync<SurgeryDetail>(second.Path)).Consent.ShouldNotBeNull();
            elsewhere.Satisfied.ShouldBeFalse();
            elsewhere.Checks.ShouldAllBe(x => x.State == SurgicalConsentState.OtherOperation && x.Consent!.ReliedOnBy == SurgicalConsent.AnotherBranch);
        }

        await cases.ConsentedAsync(second);
        await nurse.PutAsync($"{second.Path}/pre-op-checklist", ConsentTicked(await nurse.GetAsync<SurgeryDetail>(second.Path)));
        (await nurse.GetAsync<SurgeryDetail>(second.Path)).PreOpChecklist.Single(i => i.Key == PreOpChecklist.ConsentItem).State.ShouldBe(ChecklistItemState.Done);
    }

    [Fact]
    public async Task The_emergency_doctrine_does_not_override_a_refusal_the_patient_recorded()
    {
        var cases = new TheatreCases(server);
        var surgeon = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparotomy for perforation", priority: SurgeryPriority.Emergency);

        // The patient refuses the operation; the front office records it.
        await cases.ConsentAsync(c, ConsentType.HighRiskProcedure, granted: false);
        var refused = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        (refused.Refused, refused.SaidNo, refused.Checks.Single(x => x.Kind == SurgicalConsentKind.Procedure).State)
            .ShouldBe((true, true, SurgicalConsentState.Refused));

        // The surgeon and a second doctor cannot go ahead against it.
        var concurs = await server.WitnessAsync(surgeon, Users.SecondDoctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        (await TheatreCases.RefusalAsync(surgeon, HttpMethod.Post, $"{c.Path}/consent-waiver", new WaiveSurgicalConsentRequest(Emergency, concurs.ConfirmationId)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, SurgicalConsent.RefusedCode));
        (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull().Waiver.ShouldBeNull();

        // He changes his mind: the consents recorded after the refusal are his latest word, and the day goes on.
        await cases.ConsentedAsync(c);
        var consented = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        (consented.Satisfied, consented.Refused, consented.SaidNo).ShouldBe((true, false, false));
    }

    [Fact]
    public async Task The_emergency_doctrine_does_not_override_a_consent_the_patient_withdrew()
    {
        var cases = new TheatreCases(server);
        var surgeon = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparotomy for a perforated ulcer", priority: SurgeryPriority.Emergency);
        var waiver = $"{c.Path}/consent-waiver";

        // He consented to the operation, then withdrew; the front office records it.
        var given = await cases.ConsentAsync(c, ConsentType.HighRiskProcedure);
        await frontOffice.PostAsync($"/api/v1/patients/consents/{given}/revoke", new RevokeConsentRequest("Wants to speak to his son before the operation"));
        var withdrawn = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        (withdrawn.SaidNo, withdrawn.Refused, withdrawn.Checks.Single(x => x.Kind == SurgicalConsentKind.Procedure).State)
            .ShouldBe((true, false, SurgicalConsentState.Withdrawn));

        // A withdrawal is his "no", as a refusal is: the surgeon and a second doctor cannot go ahead against it.
        var concurs = await server.WitnessAsync(surgeon, Users.SecondDoctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        (await TheatreCases.RefusalAsync(surgeon, HttpMethod.Post, waiver, new WaiveSurgicalConsentRequest(Emergency, concurs.ConfirmationId)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, SurgicalConsent.WithdrawnCode));
        (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull().Waiver.ShouldBeNull();

        // He agrees again, and the front office records it; then he collapses before the anaesthetist can take his consent
        // to the anaesthesia, which the surgeon and a second doctor waive.
        var again = await cases.ConsentAsync(c, ConsentType.HighRiskProcedure);
        concurs = await server.WitnessAsync(surgeon, Users.SecondDoctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        await surgeon.PostAsync(waiver, new WaiveSurgicalConsentRequest("Collapsed in pre-op, BP falling; cannot consent to the anaesthesia", concurs.ConfirmationId));
        (await PreOpStageAsync(nurse, c)).Detail.ShouldBe("0 of 10 checks done, consent waived in an emergency");

        // The incision relies on his consent to the operation and on the waiver for the anaesthesia: the patient journey
        // still says the consent was waived once the incision has recorded the consent it relied on.
        await cases.PreOpDoneAsync(c);
        await cases.SignedInAndTimedOutAsync(c);
        await surgeon.PostAsync($"{c.Path}/start");
        var relied = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        (relied.ProcedureConsentId, relied.AnaesthesiaConsentId).ShouldBe(((Guid?)again, (Guid?)null));
        (await PreOpStageAsync(nurse, c)).Detail.ShouldBe("Consent waived in an emergency");
    }

    [Fact]
    public async Task A_withdrawal_or_a_refusal_after_the_waiver_stops_the_operation_whichever_is_recorded_first()
    {
        var cases = new TheatreCases(server);
        var surgeon = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);

        // Waived while he could not consent. He comes round and consents to both, and the day goes on on his consents; then
        // he withdraws his consent to the operation: the Time-out is refused, though the waiver is still on the booking.
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparotomy for a ruptured spleen", priority: SurgeryPriority.Emergency);
        await WaivedAsync(surgeon, c);
        var (procedure, _) = await cases.ConsentedAsync(c);
        (await PreOpStageAsync(nurse, c)).Detail.ShouldNotBeNull().ShouldNotContain("waived", Case.Insensitive, "it goes ahead on his consents now");
        await cases.PreOpDoneAsync(c);
        await nurse.PostAsync($"{c.Path}/safety-checklist/sign-in", TheatreCases.AllDone(await nurse.GetAsync<SurgeryDetail>(c.Path), SafetyPhase.SignIn));
        await frontOffice.PostAsync($"/api/v1/patients/consents/{procedure}/revoke", new RevokeConsentRequest("No longer wants the operation"));
        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Post, $"{c.Path}/safety-checklist/time-out",
                TheatreCases.AllDone(await nurse.GetAsync<SurgeryDetail>(c.Path), SafetyPhase.TimeOut)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, Required));
        var withdrawn = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        (withdrawn.Satisfied, withdrawn.SaidNo, withdrawn.Waiver is null).ShouldBe((false, true, false));
        withdrawn.Checks.Single(x => x.Kind == SurgicalConsentKind.Procedure).State.ShouldBe(SurgicalConsentState.Withdrawn);

        // Waived, and the day done on the waiver; a consent to the operation is recorded, then he says no: the front office
        // records a refusal and withdraws the earlier consent. The incision is refused, as it is in the other order.
        var other = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparotomy for a bleeding liver", priority: SurgeryPriority.Emergency);
        await WaivedAsync(surgeon, other);
        await cases.PreOpDoneAsync(other);
        await cases.SignedInAndTimedOutAsync(other);
        (await PreOpStageAsync(nurse, other)).Detail.ShouldBe("Consent waived in an emergency");
        var earlier = await cases.ConsentAsync(other, ConsentType.HighRiskProcedure);
        await cases.ConsentAsync(other, ConsentType.HighRiskProcedure, granted: false);
        await frontOffice.PostAsync($"/api/v1/patients/consents/{earlier}/revoke", new RevokeConsentRequest("Refuses the operation"));
        (await TheatreCases.RefusalAsync(surgeon, HttpMethod.Post, $"{other.Path}/start", null)).ShouldBe((HttpStatusCode.UnprocessableEntity, Required));
        var said = await nurse.GetAsync<SurgeryDetail>(other.Path);
        (said.Status, said.Consent.ShouldNotBeNull().Satisfied, said.Consent.SaidNo).ShouldBe((SurgeryStatus.InPreOp, false, true));
        said.Consent.Checks.Single(x => x.Kind == SurgicalConsentKind.Procedure).State.ShouldBe(SurgicalConsentState.Withdrawn, "the later of the two");
        (await PreOpStageAsync(nurse, other)).Detail.ShouldBeNull("the waiver no longer stands");
    }

    [Fact]
    public async Task The_waiver_ends_when_another_surgeon_takes_the_case_or_it_moves_to_another_start()
    {
        var cases = new TheatreCases(server);
        var surgeon = await server.SignInAsync(Users.Doctor);
        var other = await server.SignInAsync(Users.ThirdDoctor);
        var c = await cases.BookedAsync("Laparotomy for a ruptured spleen", SurgeryPriority.Emergency);
        await WaivedAsync(surgeon, c);
        var waived = await surgeon.GetAsync<SurgeryDetail>(c.Path);
        waived.Consent.ShouldNotBeNull().Waiver.ShouldNotBeNull();

        // Another operating surgeon takes the case: the waiver was the first surgeon's judgement.
        var otherId = (await other.GetAsync<AuthUser>("/api/v1/auth/me")).PractitionerId.ShouldNotBeNull();
        await surgeon.PutAsync(c.Path, TheatreCases.Plan(waived) with { SurgeonId = otherId });
        var handedOver = await surgeon.GetAsync<SurgeryDetail>(c.Path);
        (handedOver.SurgeonId, handedOver.Consent.ShouldNotBeNull().Waiver).ShouldBe(((Guid?)otherId, null));
        handedOver.Consent.Satisfied.ShouldBeFalse();

        // The new surgeon waives it again, the first agreeing at his screen; then the case is moved to another start.
        var concurs = await server.WitnessAsync(other, Users.Doctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        await other.PostAsync($"{c.Path}/consent-waiver", new WaiveSurgicalConsentRequest(Emergency, concurs.ConfirmationId));
        var again = await surgeon.GetAsync<SurgeryDetail>(c.Path);
        again.Consent.ShouldNotBeNull().Waiver.ShouldNotBeNull().SurgeonName.ShouldBe((await other.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName);
        await MovedAsync(surgeon, again);
        var moved = await surgeon.GetAsync<SurgeryDetail>(c.Path);
        (moved.Status, moved.ScheduledStart == again.ScheduledStart, moved.Consent.ShouldNotBeNull().Waiver).ShouldBe((SurgeryStatus.Scheduled, false, null));
        moved.Consent.Satisfied.ShouldBeFalse("the operation waited: the consent is taken for the new slot, or waived again");
    }

    [Fact]
    public async Task The_signed_form_counts_as_attached_only_when_it_is_in_the_document_store_for_the_patient()
    {
        var cases = new TheatreCases(server);
        var nurse = await server.SignInAsync(Users.Nurse);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparoscopic cholecystectomy");
        var (someoneElse, _) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);

        // An id that names no stored file, and a form scanned for another patient, are not this patient's signed form.
        await cases.ConsentAsync(c, ConsentType.HighRiskProcedure, documentFileId: Guid.CreateVersion7());
        await cases.ConsentAsync(c, ConsentType.Anaesthesia, documentFileId: await UploadFormAsync(frontOffice, someoneElse));
        var unsigned = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull();
        unsigned.Checks.ShouldAllBe(x => x.State == SurgicalConsentState.Recorded && !x.Consent!.SignedFormAttached);

        // Nor is a file filed to no patient: anyone can upload one.
        await cases.ConsentAsync(c, ConsentType.Anaesthesia, documentFileId: await UploadFormAsync(frontOffice, null));
        var unfiled = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull().Checks.Single(x => x.Kind == SurgicalConsentKind.Anaesthesia);
        (unfiled.State, unfiled.Consent!.SignedFormAttached).ShouldBe((SurgicalConsentState.Recorded, false));

        // The form scanned for this patient is.
        var form = await UploadFormAsync(frontOffice, c.PatientId);
        var signed = await cases.ConsentAsync(c, ConsentType.HighRiskProcedure, documentFileId: form);
        var procedure = (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull().Checks.Single(x => x.Kind == SurgicalConsentKind.Procedure);
        (procedure.Consent!.Id, procedure.Consent.SignedFormAttached).ShouldBe((signed, true));
    }

    [Fact]
    public async Task After_the_sign_in_the_case_team_changes_the_anaesthesia_with_a_reason_and_the_consent_to_anaesthesia_stays_needed()
    {
        var cases = new TheatreCases(server);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var outsider = await cases.OutsideDoctorAsync();
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparoscopic cholecystectomy");
        var (_, anaesthesia) = await cases.ConsentedAsync(c);
        await cases.PreOpDoneAsync(c);
        await cases.SignedInAndTimedOutAsync(c);

        // Signed in under spinal anaesthesia: a doctor outside the case cannot turn it to local, nor make the case an emergency.
        var signedIn = await nurse.GetAsync<SurgeryDetail>(c.Path);
        signedIn.Consent.ShouldNotBeNull().AnaesthesiaConsentRequiredAtSignIn.ShouldBeTrue();
        var local = TheatreCases.Plan(signedIn) with { AnaesthesiaType = AnaesthesiaType.Local };
        (await TheatreCases.RefusalAsync(outsider, HttpMethod.Put, c.Path, local with { ChangeReason = "Spinal failed" }))
            .ShouldBe((HttpStatusCode.Forbidden, "surgery.plan_change_team"));
        (await TheatreCases.RefusalAsync(outsider, HttpMethod.Put, c.Path, TheatreCases.Plan(signedIn) with { Priority = SurgeryPriority.Emergency, ChangeReason = "Bleeding" }))
            .ShouldBe((HttpStatusCode.Forbidden, "surgery.plan_change_team"));

        // The operating surgeon may, with a reason: audited at Warning, and the Sign-in and Time-out stand.
        (await TheatreCases.RefusalAsync(doctor, HttpMethod.Put, c.Path, local)).ShouldBe((HttpStatusCode.BadRequest, "validation"));
        await doctor.PutAsync(c.Path, local with { ChangeReason = "Spinal failed twice; the surgeon infiltrates locally" });
        var changed = await nurse.GetAsync<SurgeryDetail>(c.Path);
        changed.AnaesthesiaType.ShouldBe(AnaesthesiaType.Local);
        changed.SafetyChecklist.Where(p => p.Phase != SafetyPhase.SignOut).ShouldAllBe(p => p.IsComplete);
        var (summary, severity) = await cases.AuditAsync(c.PatientId, "surgery.plan_changed_after_sign_in");
        severity.ShouldBe(AuditSeverity.Warning);
        summary.ShouldContain("anaesthesia Spinal → Local");

        // The consent to anaesthesia, needed at the Sign-in, stays needed: withdrawn, the incision waits for it.
        await frontOffice.PostAsync($"/api/v1/patients/consents/{anaesthesia}/revoke", new RevokeConsentRequest("Wants to speak to her husband first"));
        (await TheatreCases.RefusalAsync(doctor, HttpMethod.Post, $"{c.Path}/start", null)).ShouldBe((HttpStatusCode.UnprocessableEntity, Required));
        (await nurse.GetAsync<SurgeryDetail>(c.Path)).Consent.ShouldNotBeNull().Checks.Single(x => x.Kind == SurgicalConsentKind.Anaesthesia).State
            .ShouldBe(SurgicalConsentState.Withdrawn);
    }

    [Fact]
    public async Task A_checklist_answer_that_is_not_done_not_applicable_or_pending_is_refused()
    {
        var cases = new TheatreCases(server);
        var nurse = await server.SignInAsync(Users.Nurse);
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Laparoscopic cholecystectomy");
        await cases.ConsentedAsync(c);
        var detail = await nurse.GetAsync<SurgeryDetail>(c.Path);

        // A number the JSON enum converter lets through would otherwise count as an answer.
        var crafted = new RecordChecklistRequest([new ChecklistInput(PreOpChecklist.ConsentItem, (ChecklistItemState)7, null)], detail.Version);
        (await TheatreCases.InvalidFieldsAsync(nurse, HttpMethod.Put, $"{c.Path}/pre-op-checklist", crafted)).ShouldContain("items[0].state");
        (await nurse.GetAsync<SurgeryDetail>(c.Path)).PreOpChecklist.Single(i => i.Key == PreOpChecklist.ConsentItem).State.ShouldBe(ChecklistItemState.Pending);
    }

    /// <summary>The operating surgeon (the doctor) waives consent for the emergency case, the second doctor agreeing at his screen.</summary>
    private async Task WaivedAsync(ApiClient surgeon, TheatreCases.Case c)
    {
        var concurs = await server.WitnessAsync(surgeon, Users.SecondDoctor, SurgeryService.ConsentWaiverWitness.Key, c.PatientId);
        await surgeon.PostAsync($"{c.Path}/consent-waiver", new WaiveSurgicalConsentRequest(Emergency, concurs.ConfirmationId));
    }

    /// <summary>The operation's pre-op stage as the patient journey shows it.</summary>
    private static async Task<JourneyStage> PreOpStageAsync(ApiClient api, TheatreCases.Case c) =>
        (await api.GetAsync<PatientJourneys>($"/api/v1/patients/{c.PatientId}/journey")).Surgeries.Single(s => s.Id == c.BookingId)
        .Stages.Single(s => s.Key == SurgeryJourney.PreOp.Key);

    /// <summary>
    /// The scheduled case moved to another start in the same theatre, on a random day years ahead; on the rare clash with
    /// another case another day is tried.
    /// </summary>
    private static async Task MovedAsync(ApiClient api, SurgeryDetail booking)
    {
        var path = $"{TheatreCases.Bookings}/{booking.Id}/schedule";
        for (var attempt = 0; ; attempt++)
        {
            var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(400 + Random.Shared.Next(1500)).AddMinutes(30 * Random.Shared.Next(40)), TimeSpan.Zero);
            using var response = await api.SendRawAsync(HttpMethod.Post, path,
                new ScheduleSurgeryRequest(booking.TheatreId.ShouldNotBeNull(), start, start.AddMinutes(90), booking.Version));
            var answer = await response.Content.ReadAsStringAsync(Ct);
            if (response.StatusCode == HttpStatusCode.Conflict && attempt < 5 && answer.Contains("\"surgery.slot_conflict\"", StringComparison.Ordinal))
            {
                continue;
            }

            response.IsSuccessStatusCode.ShouldBeTrue($"POST {path} answered {(int)response.StatusCode}: {answer}");
            return;
        }
    }

    /// <summary>A signed consent form, scanned and uploaded to the document store for <paramref name="patientId"/> (or for no patient).</summary>
    private static async Task<Guid> UploadFormAsync(ApiClient client, Guid? patientId)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("%PDF-1.4\n1 0 obj<<>>endobj\n%%EOF"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(file, "file", "surgical-consent.pdf");
        form.Add(new StringContent("consent"), "category");
        if (patientId is { } id)
        {
            form.Add(new StringContent(id.ToString()), "patientId");
        }

        using var response = await client.Http.PostAsync("/api/v1/files", form, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<StoredFileDto>(ApiClient.Json, Ct))!.Id;
    }

    /// <summary>
    /// The booking moved to a branch of its own until disposed of; then it is moved back and the branch closed, because the
    /// server is shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> AnotherBranchAsync(Guid bookingId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var home = await db.Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).Where(b => b.Id == bookingId)
            .Select(b => b.FacilityId).SingleAsync(Ct);
        var branch = new Facility
        {
            Code = "B" + string.Concat(Enumerable.Range(0, 5).Select(_ => (char)('A' + Random.Shared.Next(26)))),
            Name = "Nana Hospitals, Madurai",
            Address = new HyFib.HealthCare360.SharedKernel.Address { City = "Madurai", State = "Tamil Nadu", Country = "India" },
        };
        db.Add(branch);
        await db.SaveChangesAsync(Ct);
        await MoveAsync(server, bookingId, branch.Id);
        return new BranchCase(server, bookingId, home, branch.Id);
    }

    private static async Task MoveAsync(HospitalServer server, Guid bookingId, Guid facilityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SurgeryBooking>().IgnoreQueryFilters([HospitalDbContext.FacilityFilter])
            .Where(b => b.Id == bookingId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.FacilityId, facilityId), CancellationToken.None)).ShouldBe(1);
    }

    private sealed class BranchCase(HospitalServer server, Guid bookingId, Guid home, Guid branchId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await MoveAsync(server, bookingId, home);
            await using var scope = server.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var branch = await db.Set<Facility>().FirstAsync(f => f.Id == branchId, CancellationToken.None);
            (branch.IsDeleted, branch.DeletedAt) = (true, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>Someone confirms at <paramref name="at"/>'s screen as the doctor agreeing to operate without consent, and is refused: the code.</summary>
    private async Task<string?> WitnessRefusalAsync(ApiClient at, string witness, Guid patientId)
    {
        await server.SignInAsync(witness);
        using var response = await server.FromOwnAddress(at).SendRawAsync(HttpMethod.Post, "/api/v1/auth/witness",
            new ConfirmWitnessRequest(witness, server.Password, null, SurgeryService.ConsentWaiverWitness.Key, patientId));
        return (await TheatreCases.RefusalOfAsync(response)).Code;
    }
}
