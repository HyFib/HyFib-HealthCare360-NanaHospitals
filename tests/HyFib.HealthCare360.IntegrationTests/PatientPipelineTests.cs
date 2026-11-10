using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.Analytics.Features.Journeys;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.SharedKernel;
using BillPaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;
using CreditNoteKind = HyFib.HealthCare360.Modules.Billing.Domain.CreditNoteKind;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The pipeline of where a patient is in the hospital, as staff see it on the patient's page and on the board of patients
/// in the hospital. A patient is registered, checked in to the OPD, seen by the doctor who orders a CRP, pays at the
/// counter, gives the sample and is admitted from the OPD; at each step both endpoints show the stage the records prove,
/// and nothing is set by hand.
/// </summary>
public sealed class PatientPipelineTests(HospitalServer server)
{
    private const string CrpCode = "LAB-CRP";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_patient_is_followed_from_check_in_through_the_consultation_the_lab_and_admission()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);

        // Front office: registers the patient and checks him in to see the doctor, with a queue token.
        var patient = await RegisterAsync(frontOffice, "Ravi", "Pipeline" + Letters(8), isVip: false);
        var visit = await CheckInAsync(frontOffice, patient.Id);
        var token = visit.Token.ShouldNotBeNull();

        var journeys = await JourneysAsync(frontOffice, patient.Id);
        var op = journeys.Outpatient.ShouldNotBeNull("the visit just checked in is the patient's current out-patient visit");
        (op.Id, op.Number, op.Kind, op.Status).ShouldBe((visit.Id, visit.VisitNumber, JourneyKind.Outpatient, JourneyStatus.Active));
        op.Stages.Select(s => s.Key).ShouldBe(OutpatientJourney.Stages.Select(s => s.Key));
        var current = op.Current.ShouldNotBeNull();
        (current.Key, current.Detail, current.At).ShouldBe(("checked-in", $"Token {token.Label}", visit.VisitedAt));
        current.MinutesInStage.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(0);
        Stage(op, "called").State.ShouldBe(StageState.Pending);
        journeys.Inpatient.ShouldBeNull("he is not admitted");
        journeys.LabOrders.ShouldBeEmpty();
        journeys.Surgeries.ShouldBeEmpty();
        journeys.SurgeriesWithheld.ShouldBeFalse();

        // The board of patients in the hospital lists him among today's out-patients of the department, at the same stage.
        var row = await OnBoardAsync(frontOffice, patient.Id, $"kind=Outpatient&departmentId={visit.DepartmentId}");
        (row.Kind, row.Uhid, row.PatientName, row.IsRestricted, row.Place).ShouldBe((JourneyKind.Outpatient, patient.Uhid, patient.FullName, false, $"Token {token.Label}"));
        (row.Journey.Id, row.Journey.Current?.Key).ShouldBe((visit.Id, "checked-in"));
        (await BoardAsync(frontOffice, "kind=Inpatient")).Items.ShouldNotContain(r => r.PatientId == patient.Id, "an out-patient is not on the in-patient board");

        // Doctor: calls the token, sees him, orders a CRP and signs the consultation; the visit stays open.
        await doctor.PostAsync<QueueTokenDto>($"/api/v1/queue/{token.Id}/call", new CallTokenRequest("Room 3"));
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, visit.Id, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: "Fever and cough for four days"));
        var crp = (await doctor.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?category=Laboratory&search={CrpCode}"))
            .Single(s => s.Code == CrpCode);
        await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, encounter.Id, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Fever for four days",
            [new OrderLineRequest(crp.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));
        await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}/sign", new SignEncounterRequest(encounter.Version));

        journeys = await JourneysAsync(doctor, patient.Id);
        op = journeys.Outpatient.ShouldNotBeNull();
        op.Current.ShouldNotBeNull().Key.ShouldBe("consultation-signed");
        Stage(op, "called").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Done), s => s.Detail.ShouldBe("Room 3"));
        Stage(op, "in-consultation").State.ShouldBe(StageState.Done);
        Stage(op, "investigations-done").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("0 of 1 resulted"));
        Stage(op, "dispensed").State.ShouldBe(StageState.NotApplicable, "nothing was prescribed");
        Stage(op, "billed").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldNotBeNull().ShouldEndWith("not billed yet"));

        // The CRP's accession is beside the visit: ordered, and an out-patient pays before the sample is drawn.
        var lab = journeys.LabOrders.ShouldHaveSingleItem();
        var tube = await PendingTubeAsync(patient.Uhid);
        (lab.Id, lab.Number, lab.Kind).ShouldBe((tube.LabOrderId, tube.AccessionNumber, JourneyKind.Lab));
        lab.Current.ShouldNotBeNull().Key.ShouldBe("ordered");
        Stage(lab, "payment-cleared").State.ShouldBe(StageState.Pending);

        // Billing: the OP bill of the visit (registration, consultation, the CRP) is finalized and paid in cash.
        await BillingCollectsAsync(visit.Id);

        journeys = await JourneysAsync(frontOffice, patient.Id);
        op = journeys.Outpatient.ShouldNotBeNull();
        Stage(op, "billed").State.ShouldBe(StageState.Done);
        op.Current.ShouldNotBeNull().Key.ShouldBe("paid", "paying is the last thing that happened");
        Stage(op, "investigations-done").State.ShouldBe(StageState.Pending, "the CRP has no report yet");
        journeys.LabOrders.ShouldHaveSingleItem().Current.ShouldNotBeNull().Key.ShouldBe("payment-cleared");

        // Lab: paid, so the sample is drawn.
        var labTechnician = await server.SignInAsync(HospitalServer.Users.Lab);
        await labTechnician.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{tube.LabOrderId}/collect", new CollectSamplesRequest(null, null, "OP sample collection"));
        lab = (await JourneysAsync(doctor, patient.Id)).LabOrders.ShouldHaveSingleItem();
        lab.Current.ShouldNotBeNull().Key.ShouldBe("collected");
        Stage(lab, "received").State.ShouldBe(StageState.Pending);

        // Front office: the doctor advises admission, and he is admitted from the OPD to a semi-private bed.
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "SPW");
        var admission = await AdmitAsync(frontOffice, doctor, patient.Id, visit.Id, ward.Id);
        try
        {
            journeys = await JourneysAsync(nurse, patient.Id);
            var ip = journeys.Inpatient.ShouldNotBeNull("the stay has begun");
            (ip.Id, ip.Number, ip.Kind, ip.Status).ShouldBe((admission.Id, admission.AdmissionNumber, JourneyKind.Inpatient, JourneyStatus.Active));
            ip.Stages.Select(s => s.Key).ShouldBe(InpatientJourney.Stages.Select(s => s.Key));
            Stage(ip, "admitted").ShouldSatisfyAllConditions(
                s => s.State.ShouldBe(StageState.Done),
                s => s.Detail.ShouldBe($"From OPD visit {visit.VisitNumber}", "the admission links the OPD visit he came from"));
            var bedded = ip.Current.ShouldNotBeNull();
            bedded.Key.ShouldBe("bedded", "admitted and put in the bed at the same moment");
            bedded.Detail.ShouldBe($"{ward.Name} · {admission.BedLabel}");
            Stage(ip, "rounds-and-nursing").State.ShouldBe(StageState.Pending);
            Stage(ip, "investigations-complete").State.ShouldBe(StageState.NotApplicable, "nothing is ordered for the stay yet");
            journeys.LabOrders.ShouldHaveSingleItem().Id.ShouldBe(tube.LabOrderId, "the OPD's CRP is still followed beside the stay");

            // The board's in-patients of the ward show the stay, with the ward and bed to find him in.
            var stayRow = await OnBoardAsync(nurse, patient.Id, $"kind=Inpatient&wardId={ward.Id}");
            (stayRow.Kind, stayRow.WardId, stayRow.Journey.Id, stayRow.Journey.Current?.Key).ShouldBe((JourneyKind.Inpatient, ward.Id, admission.Id, "bedded"));
            stayRow.Place.ShouldBe(bedded.Detail);
            stayRow.ArrivedAt.ShouldBe(ip.StartedAt);

            // Each view of the patient's journeys is recorded as a read of his record.
            var auditor = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            (await auditor.GetAsync<PagedResult<AuditEventDto>>($"/api/v1/admin/audit?patientId={patient.Id}&action=patient.accessed&pageSize=50")).Items
                .Count(e => e.Summary == "Viewed where the patient is in their visit, stay, operations and lab orders").ShouldBeGreaterThanOrEqualTo(5);
        }
        finally
        {
            await CancelAdmissionAsync(frontOffice, nurse, admission.Id);
            await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/complete");
        }
    }

    [Fact]
    public async Task An_out_patient_bill_part_paid_and_written_off_is_paid_when_the_write_off_was_issued()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);

        // Front office checks the patient in; billing charges two dressings and finalizes the visit's OP bill.
        var patient = await RegisterAsync(frontOffice, "Arun", "Settled" + Letters(8), isVip: false);
        var visit = await CheckInAsync(frontOffice, patient.Id);
        var dressing = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=PROC-DRESS")).Single(s => s.Code == "PROC-DRESS");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(patient.Id, visit.Id, null, dressing.Id, 2m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, PatientId: null, ChargeIds: null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));

        // The patient pays all but ₹100 at the counter.
        await OpenShiftAsync(billing);
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(BillPaymentMode.Cash, bill.DueAmount - 100m, null, null, null, null, null, null)], Notes: null));
        Stage((await JourneysAsync(frontOffice, patient.Id)).Outpatient.ShouldNotBeNull(), "paid").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Pending),
            s => s.Detail.ShouldBe("₹100.00 due"));

        // The rest is written off: billing asks, the accountant approves, and the write-off is issued then.
        using var asked = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/credit-notes",
            new IssueCreditNoteRequest(CreditNoteKind.WriteOff, 100m, "Balance waived by the medical superintendent"));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(Ct));
        await MakerChecker.ApproveAsync(accountant, (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull());
        var writeOff = (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).CreditNotes.ShouldHaveSingleItem();

        // Paid when the write-off brought the due to nothing, not at the payment before it.
        Stage((await JourneysAsync(frontOffice, patient.Id)).Outpatient.ShouldNotBeNull(), "paid").ShouldSatisfyAllConditions(
            s => s.State.ShouldBe(StageState.Done),
            s => s.At.ShouldBe(writeOff.IssuedAt));
    }

    [Fact]
    public async Task A_restricted_patient_is_on_the_board_by_initials_and_the_journey_asks_for_access()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var lastName = "Hidden" + Letters(8);
        var patient = await RegisterAsync(frontOffice, "Rajiv", lastName, isVip: true);
        var visit = await CheckInAsync(frontOffice, patient.Id);
        try
        {
            // The front office may not open restricted records: the board names him by initials, as patient search does.
            var masked = await OnBoardAsync(frontOffice, patient.Id, $"kind=Outpatient&departmentId={visit.DepartmentId}");
            masked.PatientName.ShouldBe($"{patient.FullName[0]}. {lastName[0]}. (restricted)");
            masked.IsRestricted.ShouldBeTrue();
            masked.Uhid.ShouldBe(patient.Uhid, "the UHID stays, so the patient is still seen to");
            masked.Journey.Current.ShouldNotBeNull().Key.ShouldBe("checked-in");

            using (var refused = await frontOffice.SendRawAsync(HttpMethod.Get, $"/api/v1/patients/{patient.Id}/journey"))
            {
                refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
                (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("patient.restricted");
            }

            // The hospital administrator may open restricted records, so sees his name and his journey.
            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            var named = await OnBoardAsync(admin, patient.Id, $"kind=Outpatient&departmentId={visit.DepartmentId}");
            (named.PatientName, named.IsRestricted).ShouldBe((patient.FullName, false));
            (await JourneysAsync(admin, patient.Id)).Outpatient.ShouldNotBeNull().Id.ShouldBe(visit.Id);
        }
        finally
        {
            await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/complete");
        }
    }

    [Fact]
    public async Task A_casualty_patient_is_followed_from_arrival_through_triage_and_the_doctor_to_admission()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var caseRow = (await nurse.GetAsync<ErBoard>("/api/v1/emergency/board")).Rows.Single(r => r.VisitId == visit.Id);

        // Just arrived: on the board under ER, waiting for triage, the minutes counting from the arrival.
        var row = await OnBoardAsync(frontOffice, patientId, "kind=Emergency");
        (row.Kind, row.Place, row.WardId).ShouldBe((JourneyKind.Emergency, "Casualty · not triaged", (Guid?)null));
        (row.Journey.Id, row.Journey.Number, row.Journey.Status).ShouldBe((caseRow.CaseId, visit.VisitNumber, JourneyStatus.Active));
        row.Journey.Stages.Select(s => s.Key).ShouldBe(EmergencyJourney.Stages.Select(s => s.Key));
        var current = row.Journey.Current.ShouldNotBeNull();
        (current.Key, current.MinutesInStage.HasValue).ShouldBe(("arrived", true));
        (await BoardAsync(frontOffice, "kind=Outpatient")).Items.ShouldNotContain(r => r.PatientId == patientId, "a casualty patient is not an out-patient");
        (await BoardAsync(frontOffice, "kind=Inpatient")).Items.ShouldNotContain(r => r.PatientId == patientId);
        (await BoardAsync(frontOffice, $"kind=Emergency&departmentId={visit.DepartmentId}")).Items.ShouldContain(r => r.PatientId == patientId);
        (await BoardAsync(frontOffice, $"kind=Emergency&departmentId={Guid.CreateVersion7()}")).Items.ShouldNotContain(r => r.PatientId == patientId);

        // Triaged at level 2: the board and the patient's page show the level.
        var triaged = await nurse.PostAsync<EmergencyCaseDetail>($"/api/v1/emergency/cases/{caseRow.CaseId}/triage", EmergencyDepartmentTests.Triage(caseRow.Version));
        row = await OnBoardAsync(nurse, patientId, "kind=Emergency");
        row.Place.ShouldBe("Casualty · ESI 2");
        row.Journey.Current.ShouldNotBeNull().ShouldSatisfyAllConditions(s => s.Key.ShouldBe("triaged"), s => s.Detail.ShouldBe("ESI 2"));
        var er = (await JourneysAsync(nurse, patientId)).Emergency.ShouldNotBeNull();
        er.Title.ShouldBe("Casualty · Fall from two-wheeler, forearm laceration, head knock");

        // Seen by the doctor, who orders a CRP on the emergency visit: the test runs alongside.
        var seen = await doctor.PostAsync<EmergencyCaseDetail>($"/api/v1/emergency/cases/{caseRow.CaseId}/seen", new CaseVersionRequest(triaged.Version));
        var crp = (await doctor.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?category=Laboratory&search={CrpCode}")).Single(s => s.Code == CrpCode);
        await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(patientId, EncounterId: null, VisitId: visit.Id, AdmissionId: null,
            OrderPriority.Stat, "Head injury, fever", [new OrderLineRequest(crp.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));
        var journeys = await JourneysAsync(doctor, patientId);
        er = journeys.Emergency.ShouldNotBeNull();
        er.Current.ShouldNotBeNull().Key.ShouldBe("seen-by-doctor");
        Stage(er, "seen-by-doctor").Detail.ShouldBe(seen.DoctorName);
        Stage(er, "investigations-done").ShouldSatisfyAllConditions(s => s.State.ShouldBe(StageState.Pending), s => s.Detail.ShouldBe("0 of 1 resulted"));
        journeys.LabOrders.ShouldHaveSingleItem("the emergency visit's lab order is followed beside it");
        journeys.Outpatient.ShouldBeNull("an emergency visit is not an out-patient visit");

        // The doctor admits the patient: the casualty journey ends saying so, and the stay goes on from the casualty visit.
        await doctor.PostAsync<EmergencyCaseDetail>($"/api/v1/emergency/cases/{caseRow.CaseId}/disposition",
            new DispositionRequest(EmergencyDisposition.Admitted, "For observation, head injury", null, seen.Version));
        er = (await JourneysAsync(doctor, patientId)).Emergency.ShouldNotBeNull();
        (er.Status, er.Current).ShouldBe((JourneyStatus.Completed, (JourneyStage?)null));
        Stage(er, "disposition").Detail.ShouldBe("Admitted, bed not yet given");
        (await BoardAsync(frontOffice, "kind=Emergency")).Items.ShouldNotContain(r => r.PatientId == patientId, "the patient has left casualty");

        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "SPW");
        var admission = await AdmitAsync(frontOffice, doctor, patientId, visit.Id, ward.Id, AdmissionSource.Emergency);
        try
        {
            journeys = await JourneysAsync(nurse, patientId);
            Stage(journeys.Emergency.ShouldNotBeNull(), "disposition").Detail.ShouldBe($"Admitted · {admission.AdmissionNumber}");
            Stage(journeys.Inpatient.ShouldNotBeNull(), "admitted").Detail.ShouldBe($"From casualty visit {visit.VisitNumber}");
            (await OnBoardAsync(nurse, patientId, $"kind=Inpatient&wardId={ward.Id}")).Kind.ShouldBe(JourneyKind.Inpatient);
        }
        finally
        {
            await CancelAdmissionAsync(frontOffice, nurse, admission.Id);
        }
    }

    [Fact]
    public async Task A_restricted_patient_in_casualty_is_on_the_board_by_initials()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, isVip: true);
        try
        {
            var masked = await OnBoardAsync(frontOffice, patientId, "kind=Emergency");
            masked.IsRestricted.ShouldBeTrue();
            masked.PatientName.ShouldEndWith("(restricted)");
            using var refused = await frontOffice.SendRawAsync(HttpMethod.Get, $"/api/v1/patients/{patientId}/journey");
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
            (await OnBoardAsync(admin, patientId, "kind=Emergency")).IsRestricted.ShouldBeFalse();
            (await JourneysAsync(admin, patientId)).Emergency.ShouldNotBeNull().Number.ShouldBe(visit.VisitNumber);
        }
        finally
        {
            // A case whose visit is cancelled leaves the board.
            await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/cancel", new CancelVisitRequest("Registered for a test of the casualty board"));
            (await BoardAsync(frontOffice, "kind=Emergency")).Items.ShouldNotContain(r => r.PatientId == patientId);
        }
    }

    [Fact]
    public async Task Staff_who_never_see_patients_cannot_open_the_board_or_a_journey()
    {
        var cssd = await server.SignInAsync(HospitalServer.Users.CssdTechnician);

        using var board = await cssd.SendRawAsync(HttpMethod.Get, "/api/v1/journeys/in-hospital");
        board.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var journey = await cssd.SendRawAsync(HttpMethod.Get, $"/api/v1/patients/{Guid.CreateVersion7()}/journey");
        journey.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        using var unknown = await frontOffice.SendRawAsync(HttpMethod.Get, "/api/v1/journeys/in-hospital?kind=Lab");
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest, "the board shows out-patients, casualty or in-patients");
    }

    private static Task<PatientJourneys> JourneysAsync(ApiClient api, Guid patientId) =>
        api.GetAsync<PatientJourneys>($"/api/v1/patients/{patientId}/journey");

    private static Task<PagedResult<InHospitalPatient>> BoardAsync(ApiClient api, string filters) =>
        api.GetAsync<PagedResult<InHospitalPatient>>($"/api/v1/journeys/in-hospital?{filters}&pageSize=200");

    /// <summary>The patient's row on the board; the newest arrivals come first, so a patient just checked in is on the first page.</summary>
    private static async Task<InHospitalPatient> OnBoardAsync(ApiClient api, Guid patientId, string filters) =>
        (await BoardAsync(api, filters)).Items.Where(r => r.PatientId == patientId).ShouldHaveSingleItem();

    private static JourneyStage Stage(JourneyDto journey, string key) => journey.Stages.Single(s => s.Key == key);

    private sealed record Registered(Guid Id, string Uhid, string FullName);

    private static async Task<Registered> RegisterAsync(ApiClient frontOffice, string firstName, string lastName, bool isVip)
    {
        var first = TestPatients.FirstName(firstName);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: first, MiddleName: null, LastName: lastName, Gender: Gender.Male,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null,
            Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null,
            AadhaarNumber: null, PhotoFileId: null, IsVip: isVip, Notes: null, ConfirmedNotDuplicate: false));
        return new Registered(created.Id, created.Uhid, $"{first} {lastName}");
    }

    /// <summary>
    /// Front office: checks the patient in to see the doctor, in the doctor's department, with a queue token. The visit is
    /// read back as stored, to the microsecond the database keeps, which the journey's times are.
    /// </summary>
    private async Task<VisitDetail> CheckInAsync(ApiClient frontOffice, Guid patientId)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        var created = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Fever",
            false, null, null, TokenPriority.Normal, IssueToken: true, NotifyPatient: false));
        return await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{created.Id}");
    }

    /// <summary>The lab's tube for the patient's CRP, waiting on the collection list.</summary>
    private async Task<PendingCollectionItem> PendingTubeAsync(string uhid)
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        return (await lab.GetAsync<PagedResult<PendingCollectionItem>>($"/api/v1/lab/samples/pending-collection?search={uhid}")).Items.ShouldHaveSingleItem();
    }

    /// <summary>Billing: drafts the visit's OP bill, finalizes it and takes the whole amount in cash in an open shift.</summary>
    private async Task BillingCollectsAsync(Guid visitId)
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visitId, PatientId: null, ChargeIds: null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        await OpenShiftAsync(billing);
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(BillPaymentMode.Cash, bill.DueAmount, null, null, null, null, null, null)], Notes: null));
    }

    /// <summary>Billing's counter shift, opened if it is not: money is taken only in an open shift.</summary>
    private static async Task OpenShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    /// <summary>
    /// Front office: admits the patient from his OPD visit, under the doctor, to a bed of the ward added for it
    /// (<see cref="TestBeds"/>).
    /// </summary>
    private async Task<AdmissionCreated> AdmitAsync(
        ApiClient frontOffice, ApiClient doctor, Guid patientId, Guid visitId, Guid wardId, AdmissionSource source = AdmissionSource.OutPatient)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, wardId);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: source == AdmissionSource.Emergency ? AdmissionType.Emergency : AdmissionType.Elective, Source: source,
            SourceVisitId: visitId,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>
    /// The front office cancels the admission made for the test, and the nurse cleans the bed it released: the run's tests
    /// read the same wards' boards and housekeeping lists.
    /// </summary>
    private static async Task CancelAdmissionAsync(ApiClient frontOffice, ApiClient nurse, Guid admissionId)
    {
        var stay = await frontOffice.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admissionId}");
        await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{admissionId}/cancel", new CancelAdmissionRequest("Admitted for a test of the patient pipeline"));
        var task = (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={stay.WardId}"))
            .Single(t => t.BedId == stay.BedId && t.AdmissionId == stay.Id);
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned"));
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
