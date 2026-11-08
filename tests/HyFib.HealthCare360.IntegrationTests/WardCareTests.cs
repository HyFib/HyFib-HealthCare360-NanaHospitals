using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Clinical.Features.Vitals;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.CarePlan;
using HyFib.HealthCare360.Modules.Inpatient.Features.DowntimePrint;
using HyFib.HealthCare360.Modules.Inpatient.Features.EarlyWarning;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Features.Nursing;
using HyFib.HealthCare360.Modules.Inpatient.Features.Rounds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Transfers;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The ward's safety work around a stay: NEWS2 escalation to the ward's doctor and nurses in charge, transfers between
/// wards as a request the receiving ward accepts, the sheet the wards print before a downtime, and the basic nursing care
/// plan shown at handover.
/// </summary>
public sealed class WardCareTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------- NEWS2

    [Fact]
    public async Task Vital_signs_scoring_news2_five_or_more_escalate_and_a_second_clinician_records_the_response()
    {
        var (frontOffice, doctor, nurse) = await StaffAsync();
        var admission = await AdmitAsync(frontOffice, doctor, "GWF");

        // Normal observations: NEWS2 0, nothing raised.
        var calm = await RecordAsync(nurse, admission.PatientId, rr: 16, spo2: 97, oxygen: false, sbp: 122, pulse: 78, Consciousness.Alert, 37.0m);
        calm.News2.ShouldNotBeNull().Total.ShouldBe(0);
        calm.News2.Response.ShouldBe(News2Response.Routine);
        calm.News2.IsComplete.ShouldBeTrue();
        (await EscalationsAsync(nurse, admission.Id)).ShouldBeEmpty();

        // Respiration 22 (2), SpO₂ 95 (1), on oxygen (2): 5, an urgent review.
        var urgent = await RecordAsync(nurse, admission.PatientId, rr: 22, spo2: 95, oxygen: true, sbp: 118, pulse: 88, Consciousness.Alert, 37.4m);
        urgent.News2.ShouldNotBeNull().Total.ShouldBe(5);
        urgent.News2.Response.ShouldBe(News2Response.Urgent);
        var first = (await EscalationsAsync(nurse, admission.Id)).ShouldHaveSingleItem();
        (first.Level, first.Status, first.Score, first.VitalSignsId).ShouldBe((EscalationLevel.Urgent, EscalationStatus.Open, 5, urgent.Id));

        // The stay's consultant and the nurses in charge (the hospital administrator holds nursing.in_charge) are told,
        // by UHID and bed, never by name.
        var doctorUser = await UserIdAsync(HospitalServer.Users.Doctor);
        var inCharge = await UserIdAsync(HospitalServer.Users.HospitalAdmin);
        var told = await NotificationsAsync($"/inpatient/admissions/{admission.Id}");
        told.ShouldContain(n => n.UserId == doctorUser && n.Title == "NEWS2 urgent review");
        told.ShouldContain(n => n.UserId == inCharge && n.Title == "NEWS2 urgent review");
        told.ShouldAllBe(n => n.Body!.Contains(admission.Uhid) && !n.Body.Contains(admission.PatientName));

        // Another urgent score while the first is open raises nothing more.
        await RecordAsync(nurse, admission.PatientId, rr: 23, spo2: 94, oxygen: true, sbp: 115, pulse: 92, Consciousness.Alert, 37.6m);
        (await EscalationsAsync(nurse, admission.Id)).Count.ShouldBe(1);

        // A single parameter scoring 3 is urgent whatever the total: a systolic of 88 alone.
        var single = News2.Score(new News2Reading(16, 97, SpO2Scale.Scale1, false, 88, 80, Consciousness.Alert, 37m), null, server.Today).ShouldNotBeNull();
        (single.Total, single.Response).ShouldBe((3, News2Response.Urgent));

        // A rise to 7 or more is an emergency, raised although the urgent one is open.
        var worse = await RecordAsync(nurse, admission.PatientId, rr: 26, spo2: 91, oxygen: true, sbp: 95, pulse: 115, Consciousness.Voice, 38.5m);
        worse.News2.ShouldNotBeNull().Total.ShouldBe(16);
        worse.News2.Response.ShouldBe(News2Response.Emergency);
        var open = await EscalationsAsync(nurse, admission.Id);
        open.Select(e => e.Level).Order().ToArray().ShouldBe([EscalationLevel.Urgent, EscalationLevel.Emergency]);
        (await NotificationsAsync($"/inpatient/admissions/{admission.Id}")).ShouldContain(n => n.UserId == inCharge && n.Title == "NEWS2 emergency: respond now");

        // The bed board and the doctor's round show the latest score and the open escalation.
        var bed = await BedAsync(nurse, admission);
        (bed.Occupant!.News2Score, bed.Occupant.News2Response).ShouldBe((16, News2Response.Emergency));
        bed.Alerts.ShouldContain(BedAlert.News2Emergency);
        bed.Alerts.ShouldContain(BedAlert.EscalationOpen);
        var onRound = (await doctor.GetAsync<WardRound>("/api/v1/inpatient/rounds")).Patients.Single(p => p.AdmissionId == admission.Id);
        (onRound.News2Score, onRound.News2Response, onRound.HasOpenEscalation).ShouldBe((16, News2Response.Emergency, true));

        // Whoever took the observations does not record the response; the doctor does.
        var emergency = open.Single(e => e.Level == EscalationLevel.Emergency);
        var respond = $"/api/v1/nursing/escalations/{emergency.Id}/respond";
        (await RefusalAsync(nurse, respond, new RespondToEscalationRequest(EscalationOutcome.RapidResponseCalled, "Called the RRT", emergency.Version)))
            .ShouldBe("escalation.self");
        var answered = await doctor.PostAsync<EscalationDto>(respond,
            new RespondToEscalationRequest(EscalationOutcome.RapidResponseCalled, "Seen at the bedside; RRT called, fluids and antibiotics started", emergency.Version));
        answered.Status.ShouldBe(EscalationStatus.Responded);
        answered.RespondedByDoctor.ShouldBeTrue();
        answered.MinutesToRespond.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(0);
        answered.RespondedByName.ShouldNotBeNullOrWhiteSpace();
        (await RefusalAsync(doctor, respond, new RespondToEscalationRequest(EscalationOutcome.TreatmentChanged, "Again", answered.Version)))
            .ShouldBe("escalation.responded");
        (await AuditAsync(admission.PatientId)).ShouldContain(a => a.Action == "inpatient.news2_responded");

        // The ward's open list keeps the urgent one only.
        var wardOpen = await nurse.GetAsync<List<EscalationDto>>($"/api/v1/nursing/escalations?wardId={admission.WardId}");
        wardOpen.Where(e => e.AdmissionId == admission.Id).Select(e => e.Level).ShouldBe([EscalationLevel.Urgent]);
    }

    [Fact]
    public async Task News2_is_not_scored_for_a_child_nor_escalated_for_a_patient_not_in_a_bed()
    {
        var (frontOffice, _, nurse) = await StaffAsync();
        var outpatient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var reading = await RecordAsync(nurse, outpatient.Id, rr: 30, spo2: 88, oxygen: true, sbp: 85, pulse: 140, Consciousness.Pain, 39.5m);
        reading.News2.ShouldNotBeNull().Response.ShouldBe(News2Response.Emergency, "scored, but there is no stay to escalate on");
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<EarlyWarningEscalation>().IgnoreQueryFilters()
            .AnyAsync(e => e.PatientId == outpatient.Id, Ct)).ShouldBeFalse();

        News2.Score(new News2Reading(30, 88, SpO2Scale.Scale1, true, 85, 140, Consciousness.Pain, 39.5m), server.Today.AddYears(-10), server.Today)
            .ShouldBeNull("NEWS2 is for adults of 16 and over");
    }

    // ---------------------------------------------------------------------- transfers

    [Fact]
    public async Task A_move_to_another_ward_is_requested_with_the_bed_held_and_made_when_the_receiving_ward_accepts()
    {
        var (frontOffice, doctor, nurse) = await StaffAsync();
        var inCharge = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var admission = await AdmitAsync(frontOffice, doctor, "GWF");
        var target = await TestBeds.AddAsync(server, "SPW");

        // Not as a direct transfer.
        using (var direct = await nurse.SendRawAsync(HttpMethod.Post, $"/api/v1/inpatient/admissions/{admission.Id}/transfer",
                   new TransferPatientRequest(target.BedId, "Room change", null, null, null)))
        {
            direct.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CodeAsync(direct)).ShouldBe("transfer.request_required");
        }

        // The sending ward asks, with the reason and a summary; the bed is held for the patient.
        var asked = await nurse.PostAsync<TransferRequestDto>($"/api/v1/inpatient/admissions/{admission.Id}/transfer-requests",
            new RequestTransferRequest(target.BedId, "Family asked for a semi-private room", "Cellulitis left leg day 2, afebrile, IV ceftriaxone 12-hourly; cannula right hand.", null, null, false));
        (asked.Status, asked.IsUrgent, asked.ToBedId).ShouldBe((TransferRequestStatus.Pending, false, target.BedId));
        asked.HoldUntil.ShouldNotBeNull();
        var held = await frontOffice.GetAsync<BedDto>($"/api/v1/beds/{target.BedId}");
        (held.Status, held.ReservedForPatientId).ShouldBe((BedStatus.Reserved, admission.PatientId));
        (await BedAsync(nurse, admission)).Alerts.ShouldContain(BedAlert.TransferPending);
        var inChargeId = await UserIdAsync(HospitalServer.Users.HospitalAdmin);
        var nurseId = await UserIdAsync(HospitalServer.Users.Nurse);
        (await NotificationsAsync("/inpatient/transfers")).ShouldContain(n => n.UserId == inChargeId && n.Title.StartsWith("Transfer requested to"));

        // One request at a time; never accepted by whoever asked; nor by someone who is neither a nurse in charge nor a doctor.
        using (var again = await nurse.SendRawAsync(HttpMethod.Post, $"/api/v1/inpatient/admissions/{admission.Id}/transfer-requests",
                   new RequestTransferRequest((await TestBeds.AddAsync(server, "SPW")).BedId, "Again", "Again", null, null, false)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var accept = $"/api/v1/inpatient/transfer-requests/{asked.Id}/accept";
        (await RefusalAsync(nurse, accept, new AcceptTransferRequest(null, null, asked.Version))).ShouldBe("transfer.self_accept");
        using (var frontDesk = await frontOffice.SendRawAsync(HttpMethod.Post, accept, new AcceptTransferRequest(null, null, asked.Version)))
        {
            frontDesk.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeAsync(frontDesk)).ShouldBe("transfer.not_receiving_ward");
        }

        // The receiving ward's nurse in charge accepts: the patient moves as any transfer moves them.
        var accepted = await inCharge.PostAsync<TransferRequestDto>(accept, new AcceptTransferRequest("Bed ready, received", null, asked.Version));
        (accepted.Status, accepted.DecidedByName).ShouldBe((TransferRequestStatus.Completed, (await inCharge.GetAsync<AuthUser>("/api/v1/auth/me")).DisplayName));
        var stay = await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}");
        stay.BedId.ShouldBe(target.BedId);
        var history = await nurse.GetAsync<List<BedAssignmentDto>>($"/api/v1/inpatient/admissions/{admission.Id}/bed-history");
        history.Last().ShouldSatisfyAllConditions(h => h.BedId.ShouldBe(target.BedId), h => h.Reason.ShouldBe(BedAssignmentReason.Transfer));
        (await frontOffice.GetAsync<BedDto>($"/api/v1/beds/{admission.BedId}")).Status.ShouldBe(BedStatus.Cleaning);
        (await NotificationsAsync("/inpatient/transfers")).ShouldContain(n => n.UserId == nurseId && n.Title == "Transfer accepted");
    }

    [Fact]
    public async Task A_declined_or_cancelled_request_releases_the_held_bed()
    {
        var (frontOffice, doctor, nurse) = await StaffAsync();
        var admission = await AdmitAsync(frontOffice, doctor, "GWF");

        var first = await TestBeds.AddAsync(server, "SPW");
        var declined = await RequestAsync(nurse, admission.Id, first.BedId);
        var decision = await doctor.PostAsync<TransferRequestDto>($"/api/v1/inpatient/transfer-requests/{declined.Id}/decline",
            new DecideTransferRequest("No isolation room free today", declined.Version));
        (decision.Status, decision.DecisionNote).ShouldBe((TransferRequestStatus.Declined, "No isolation room free today"));
        (await frontOffice.GetAsync<BedDto>($"/api/v1/beds/{first.BedId}")).Status.ShouldBe(BedStatus.Vacant);
        var nurseId = await UserIdAsync(HospitalServer.Users.Nurse);
        (await NotificationsAsync("/inpatient/transfers")).ShouldContain(n => n.UserId == nurseId && n.Title == "Transfer declined");

        var second = await TestBeds.AddAsync(server, "SPW");
        var cancelled = await RequestAsync(nurse, admission.Id, second.BedId);
        (await nurse.PostAsync<TransferRequestDto>($"/api/v1/inpatient/transfer-requests/{cancelled.Id}/cancel",
            new DecideTransferRequest("Family changed their mind", cancelled.Version))).Status.ShouldBe(TransferRequestStatus.Cancelled);
        (await frontOffice.GetAsync<BedDto>($"/api/v1/beds/{second.BedId}")).Status.ShouldBe(BedStatus.Vacant);
        (await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}")).BedId.ShouldBe(admission.BedId, "the patient never moved");
    }

    [Fact]
    public async Task An_urgent_step_up_to_icu_moves_at_once_and_the_acceptance_is_recorded_afterwards_or_flagged_after_the_hour()
    {
        var (frontOffice, doctor, nurse) = await StaffAsync();
        var admission = await AdmitAsync(frontOffice, doctor, "GWF");

        // Urgent only to or from critical care.
        (await RefusalAsync(nurse, $"/api/v1/inpatient/admissions/{admission.Id}/transfer-requests",
                new RequestTransferRequest((await TestBeds.AddAsync(server, "SPW")).BedId, "Quick move", "Stable", null, null, true)))
            .ShouldBe("transfer.urgent_not_critical_care");

        var icu = await TestBeds.AddAsync(server, "ICU");
        var urgent = await nurse.PostAsync<TransferRequestDto>($"/api/v1/inpatient/admissions/{admission.Id}/transfer-requests",
            new RequestTransferRequest(icu.BedId, "NEWS2 9: septic shock", "Hypotensive despite 2 L fluids; noradrenaline needed.", null, null, true));
        (urgent.Status, urgent.IsUrgent).ShouldBe((TransferRequestStatus.MovedAwaitingAcceptance, true));
        urgent.AcceptanceDueAt.ShouldBe(urgent.TransferredAt!.Value.AddHours(1));
        (await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}")).BedId.ShouldBe(icu.BedId, "moved at once");

        // The patient has moved: the receiving ward records its acceptance, it does not decline.
        (await RefusalAsync(doctor, $"/api/v1/inpatient/transfer-requests/{urgent.Id}/decline", new DecideTransferRequest("No", urgent.Version)))
            .ShouldBe("transfer.not_pending");

        // Not accepted within the hour: flagged, and the nurses in charge are told once.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            var row = await db.Set<TransferRequest>().IgnoreQueryFilters().SingleAsync(t => t.Id == urgent.Id, Ct);
            row.TransferredAt = row.TransferredAt!.Value.AddHours(-2);
            await db.SaveChangesAsync(Ct);
        }

        (await NotifyOverdueAsync()).ShouldBeGreaterThanOrEqualTo(1);
        (await NotifyOverdueAsync()).ShouldBe(0, "told once");
        var listed = (await nurse.GetAsync<List<TransferRequestDto>>($"/api/v1/inpatient/admissions/{admission.Id}/transfer-requests")).Single();
        listed.IsAcceptanceOverdue.ShouldBeTrue();

        var accepted = await doctor.PostAsync<TransferRequestDto>($"/api/v1/inpatient/transfer-requests/{urgent.Id}/accept",
            new AcceptTransferRequest("Received in ICU", null, listed.Version));
        (accepted.Status, accepted.WasAcceptedLate, accepted.IsAcceptanceOverdue).ShouldBe((TransferRequestStatus.Completed, true, false));
        (await AuditAsync(admission.PatientId)).ShouldContain(a => a.Action == "inpatient.transfer_acceptance_overdue");
    }

    // ---------------------------------------------------------------------- downtime print

    [Fact]
    public async Task The_downtime_sheet_lists_each_patient_with_allergies_medicines_due_tasks_and_news2()
    {
        var (frontOffice, doctor, nurse) = await StaffAsync();
        var admission = await AdmitAsync(frontOffice, doctor, "GWF");
        await nurse.PostAsync<JsonElement>($"/api/v1/emr/patients/{admission.PatientId}/allergies",
            new RecordAllergyRequest("Penicillin", AllergyType.Drug, "Urticaria", AllergySeverity.Severe, null, null));
        var order = await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders", new AddMedicationOrderRequest(
            DrugItemId: null, DrugName: "Paracetamol 1 g IV", GenericName: "Paracetamol", Strength: "1 g", Dose: "1 g", Route: "IV", Frequency: "Q4H",
            DurationDays: 3, StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: null));
        await RecordAsync(nurse, admission.PatientId, rr: 22, spo2: 95, oxygen: true, sbp: 118, pulse: 88, Consciousness.Alert, 37.4m);

        var pack = await nurse.GetAsync<DowntimeWardPack>($"/api/v1/inpatient/downtime-print?wardId={admission.WardId}");
        (pack.WindowEndsAt - pack.GeneratedAt).ShouldBe(TimeSpan.FromHours(DowntimePrintService.WindowHours));
        var ward = pack.Wards.ShouldHaveSingleItem();
        ward.Counts.Occupied.ShouldBeGreaterThanOrEqualTo(1);
        var sheet = ward.Patients.Single(p => p.AdmissionId == admission.Id);
        sheet.AllergyStatus.ShouldBe(AllergyRecordingStatus.Recorded);
        sheet.Allergies.ShouldHaveSingleItem().ShouldSatisfyAllConditions(a => a.Allergen.ShouldBe("Penicillin"), a => a.Severity.ShouldBe(AllergySeverity.Severe));
        var paracetamol = sheet.Medications.Single(m => m.OrderId == order.Id);
        paracetamol.DueTimes.ShouldNotBeEmpty();
        paracetamol.DueTimes.ShouldAllBe(t => t <= pack.WindowEndsAt);
        sheet.News2.ShouldNotBeNull().Score.ShouldBe(5);
        sheet.PendingTasks.ShouldContain(t => t.StartsWith("NEWS2 5") && t.Contains("waiting for a response"));

        // The whole hospital's pack has every ward in service, and the print is audited.
        var hospital = await nurse.GetAsync<DowntimeWardPack>("/api/v1/inpatient/downtime-print");
        hospital.Wards.Count.ShouldBeGreaterThan(1);
        hospital.Wards.SelectMany(w => w.Patients).ShouldContain(p => p.AdmissionId == admission.Id);
        var nurseUser = await UserIdAsync(HospitalServer.Users.Nurse);
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .AnyAsync(e => e.Action == "inpatient.downtime_print" && e.UserId == nurseUser, Ct)).ShouldBeTrue();

        // Not for staff outside the ward team.
        using var billing = await (await server.SignInAsync(HospitalServer.Users.Billing)).SendRawAsync(HttpMethod.Get, "/api/v1/inpatient/downtime-print");
        billing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---------------------------------------------------------------------- care plan

    [Fact]
    public async Task A_care_plan_problem_is_evaluated_once_a_shift_and_shown_on_the_handover_sheet()
    {
        var (frontOffice, doctor, nurse) = await StaffAsync();
        var admission = await AdmitAsync(frontOffice, doctor, "GWF");
        var path = $"/api/v1/nursing/admissions/{admission.Id}/care-plan";

        var falls = await nurse.PostAsync<CarePlanProblemDto>(path, new AddCarePlanProblemRequest("FALL_RISK", null, null, null));
        (falls.Title, falls.Status, falls.EvaluatedThisShift).ShouldBe(("Risk of falls", CarePlanStatus.Active, false));
        falls.Interventions.ShouldNotBeEmpty("the list's interventions are the starting point");
        using (var twice = await nurse.SendRawAsync(HttpMethod.Post, path, new AddCarePlanProblemRequest("FALL_RISK", null, null, null)))
        {
            twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var unnamed = await nurse.SendRawAsync(HttpMethod.Post, path, new AddCarePlanProblemRequest("OTHER", null, null, null)))
        {
            unnamed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var other = await nurse.PostAsync<CarePlanProblemDto>(path, new AddCarePlanProblemRequest("OTHER", "Language barrier", "Patient understands the plan",
            [new CarePlanIntervention("Use the interpreter line", "Each shift")]));
        other.ProblemCode.ShouldBe("OTHER");

        var evaluated = await nurse.PostAsync<CarePlanProblemDto>($"/api/v1/nursing/care-plan/{falls.Id}/evaluate",
            new EvaluateCarePlanProblemRequest(CarePlanProgress.Unchanged, "Walks with a frame; rails up"));
        evaluated.EvaluatedThisShift.ShouldBeTrue();
        evaluated.LastEvaluation.ShouldNotBeNull().Progress.ShouldBe(CarePlanProgress.Unchanged);
        using (var again = await nurse.SendRawAsync(HttpMethod.Post, $"/api/v1/nursing/care-plan/{falls.Id}/evaluate",
                   new EvaluateCarePlanProblemRequest(CarePlanProgress.Improving, null)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await CodeAsync(again)).ShouldBe("care_plan.evaluated");
        }

        // The handover sheet carries the active problems for the receiving shift.
        await nurse.PostAsync<HandoverDto>($"/api/v1/nursing/admissions/{admission.Id}/handovers",
            new AddHandoverRequest("Stable", null, null, null, "Recheck Morse score"));
        var sheet = await nurse.GetAsync<List<WardHandoverItem>>($"/api/v1/nursing/wards/{admission.WardId}/handovers");
        var item = sheet.Single(h => h.Handover.AdmissionId == admission.Id);
        item.CarePlan.ShouldNotBeNull().Select(p => p.Title).Order().ShouldBe(["Language barrier", "Risk of falls"]);

        var resolved = await nurse.PostAsync<CarePlanProblemDto>($"/api/v1/nursing/care-plan/{other.Id}/resolve",
            new ResolveCarePlanProblemRequest("Family interprets", other.Version));
        resolved.Status.ShouldBe(CarePlanStatus.Resolved);
        (await nurse.GetAsync<List<WardHandoverItem>>($"/api/v1/nursing/wards/{admission.WardId}/handovers"))
            .Single(h => h.Handover.AdmissionId == admission.Id).CarePlan!.Select(p => p.Title).ShouldBe(["Risk of falls"]);
    }

    // ---------------------------------------------------------------------- steps

    private async Task<(ApiClient FrontOffice, ApiClient Doctor, ApiClient Nurse)> StaffAsync() => (
        await server.SignInAsync(HospitalServer.Users.FrontOffice),
        await server.SignInAsync(HospitalServer.Users.Doctor),
        await server.SignInAsync(HospitalServer.Users.Nurse));

    /// <summary>A new adult patient admitted under the doctor to a bed added to the ward for the test.</summary>
    internal async Task<Stay> AdmitAsync(ApiClient frontOffice, ApiClient doctor, string wardCode)
    {
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, wardCode);
        var created = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Sepsis, source to be found", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        var detail = await frontOffice.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{created.Id}");
        return new Stay(created.Id, patient.Id, patient.Uhid, detail.PatientName, detail.WardId!.Value, bed.BedId);
    }

    internal static Task<VitalsDto> RecordAsync(
        ApiClient nurse, Guid patientId, int rr, int spo2, bool oxygen, int sbp, int pulse, Consciousness consciousness, decimal temperature) =>
        nurse.PostAsync<VitalsDto>($"/api/v1/emr/patients/{patientId}/vitals", new RecordVitalsRequest(
            EncounterId: null, RecordedAt: null, Temperature: temperature, TemperatureUnit: TemperatureUnit.Celsius, Pulse: pulse, RespiratoryRate: rr,
            SystolicBp: sbp, DiastolicBp: sbp - 40, SpO2: spo2, OnSupplementalOxygen: oxygen, HeightCm: null, WeightKg: null, PainScore: null,
            BloodGlucose: null, GlucoseTiming: GlucoseTiming.Random, Gcs: null, Notes: null, Consciousness: consciousness, SpO2Scale: SpO2Scale.Scale1));

    private static Task<List<EscalationDto>> EscalationsAsync(ApiClient api, Guid admissionId) =>
        api.GetAsync<List<EscalationDto>>($"/api/v1/nursing/admissions/{admissionId}/escalations");

    private static Task<TransferRequestDto> RequestAsync(ApiClient nurse, Guid admissionId, Guid bedId) =>
        nurse.PostAsync<TransferRequestDto>($"/api/v1/inpatient/admissions/{admissionId}/transfer-requests",
            new RequestTransferRequest(bedId, "Needs a quieter room", "Stable, day 1 of IV antibiotics.", null, null, false));

    private static async Task<BedBoardItem> BedAsync(ApiClient api, Stay stay)
    {
        var current = await api.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{stay.Id}");
        var board = await api.GetAsync<List<BedBoardWard>>($"/api/v1/beds/board?wardId={current.WardId}");
        return board.ShouldHaveSingleItem().Beds.Single(b => b.BedId == current.BedId);
    }

    private async Task<int> NotifyOverdueAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TransferService>().NotifyOverdueAcceptancesAsync(Ct);
    }

    private async Task<Guid> UserIdAsync(string userName)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<User>().IgnoreQueryFilters()
            .Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync(Ct);
    }

    private async Task<List<UserNotification>> NotificationsAsync(string link)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .Where(n => n.Link == link).ToListAsync(Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.PatientId == patientId).ToListAsync(Ct);
    }

    private static async Task<string?> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        return await CodeAsync(response);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).TryGetProperty("code", out var code) ? code.GetString() : null;

    /// <summary>A stay made for a test: its admission, patient (UHID and name), ward and first bed.</summary>
    internal sealed record Stay(Guid Id, Guid PatientId, string Uhid, string PatientName, Guid WardId, Guid BedId);
}
