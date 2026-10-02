using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Features.Admin;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Diagnoses;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Queue;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Features.Availability;
using HyFib.HealthCare360.Modules.Scheduling.Features.Schedules;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using BillPaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;
using BillPaymentRequest = HyFib.HealthCare360.Modules.Billing.Features.Payments.CollectPaymentRequest;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The out-patient journey that SRS §9 makes a release condition: register → appointment → consult → lab → pharmacy →
/// bill. Every step goes through the real HTTP API, signed in as the member of staff who does it in the hospital, and
/// checks what the step changed: statuses, the report, the stock, the bill and the payments. Two product rules set the
/// order: an out-patient test is paid before its sample is drawn, so the OP bill (registration, consultation, lab test)
/// is settled before the lab collects; and the pharmacy sells the medicines on its own GST invoice, paid at its counter.
/// </summary>
public sealed class OutpatientJourneyTests(HospitalServer server)
{
    private const string Complaint = "Fever and body ache for three days";
    private const string CrpCode = "LAB-CRP";
    private const string CrpValue = "3.2";
    private const string ParacetamolCode = "DRG-PCM500";
    private const int CourseDays = 3;

    /// <summary>India Standard Time, the hospital's calendar (no daylight saving).</summary>
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    [Fact]
    public async Task A_new_patient_is_registered_seen_tested_treated_and_billed()
    {
        // Messages to the patient go to the integrations simulator, so the journey can see what the patient receives.
        await using var email = await EmailToSimulatorAsync();

        // Front office: registers a new patient, books today's slot with the doctor and checks the patient in.
        var patient = await FrontOfficeRegistersBooksAndChecksInAsync();

        // Doctor: sees the patient, writes the notes and a diagnosis, orders a CRP and signs an e-prescription.
        var consultation = await DoctorConsultsAsync(patient);

        // Lab: the sample is waiting to be drawn, but an out-patient test is drawn only once it is paid.
        var labWork = await LabFindsTheUnpaidSampleAsync(patient);

        // Billing: the OP bill of the visit (registration, consultation, lab test) is finalized and paid in full.
        var bill = await BillingCollectsTheOpBillAsync(patient, consultation, labWork);

        // Lab: draws, receives and results the sample; the pathologist verifies it and releases the report.
        var reportId = await LabReportsTheTestAsync(patient, labWork);

        // Doctor: the released report is on the patient's record.
        await DoctorSeesTheReportAsync(patient, consultation, reportId);

        // The patient gets the report: the copy printed at the counter, the PDF on the portal, and by e-mail.
        await ThePatientGetsTheReportAsync(patient, reportId);

        // Pharmacist: dispenses the e-prescription from the queue, FEFO, on the pharmacy's GST invoice, paid at the counter.
        var sale = await PharmacistDispensesAsync(patient, consultation);

        // The patient leaves owing nothing: every service of the visit is on the paid OP bill, and the medicines, which
        // the pharmacy sells on its own GST invoice (SRS MB-06, PH-01/02) rather than on the OP bill, are paid too.
        await NothingIsLeftToPayAsync(patient, bill, sale);
    }

    private async Task<CheckedInPatient> FrontOfficeRegistersBooksAndChecksInAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);

        // A patient of this test alone: the unique surname and mobile number keep the duplicate check quiet.
        var surname = "Journey" + RandomLetters(8);
        var phone = "9" + RandomDigits(9);
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: "Ravi", MiddleName: null, LastName: surname, Gender: Gender.Male,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(365 * 30)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: $"ravi.{surname}@example.org", Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: false));
        var record = await frontOffice.GetAsync<PatientDetail>($"/api/v1/patients/{registered.Id}");
        record.Uhid.ShouldBe(registered.Uhid, "Registration allocates the UHID.");
        record.FullName.ShouldBe($"Ravi {surname}");

        // The consultant the doctor account belongs to, and a free slot with them today.
        var doctorAccount = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctorAccount.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var doctor = await frontOffice.GetAsync<PractitionerDto>($"/api/v1/masters/practitioners/{practitionerId}");
        var slot = await FreeSlotTodayAsync(frontOffice, doctor);

        var appointment = await frontOffice.PostAsync<AppointmentDetail>("/api/v1/scheduling/appointments", new BookAppointmentRequest(
            registered.Id, doctor.Id, slot.StartsAt, AppointmentChannel.Phone, AppointmentVisitType.New, IsTelehealth: false, Complaint, Notes: null));
        appointment.Status.ShouldBe(AppointmentStatus.Booked);
        appointment.StartsAt.ShouldBe(slot.StartsAt);
        appointment.FeeAmount.ShouldBe(doctor.ConsultationFeeNew, "The fee quoted at booking is the doctor's fee for a new visit.");

        // The patient arrives: check-in starts the visit and issues a queue token.
        var checkIn = await frontOffice.PostAsync<CheckInResult>(
            $"/api/v1/scheduling/appointments/{appointment.Id}/check-in", new CheckInAppointmentRequest());
        checkIn.LinkedExistingVisit.ShouldBeFalse("A new patient has no earlier visit today.");
        var visit = await frontOffice.GetAsync<VisitDetail>($"/api/v1/visits/{checkIn.VisitId}");
        visit.Status.ShouldBe(VisitStatus.Open, "A checked-in patient waits in an open visit.");
        visit.Type.ShouldBe(VisitType.OutPatient);
        visit.AppointmentId.ShouldBe(appointment.Id);
        visit.PractitionerId.ShouldBe(doctor.Id);
        var token = visit.Token.ShouldNotBeNull("Check-in issues a queue token.");
        token.Label.ShouldBe(checkIn.TokenLabel);
        token.Status.ShouldBe(TokenStatus.Waiting);
        (await AppointmentAsync(frontOffice, appointment.Id)).Status.ShouldBe(AppointmentStatus.CheckedIn);

        return new CheckedInPatient(registered.Id, registered.Uhid, phone, doctor, appointment.Id, appointment.FeeAmount, appointment.Room, visit.Id, token.Id);
    }

    private async Task<Consultation> DoctorConsultsAsync(CheckedInPatient patient)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);

        // Calls the patient in from the department queue.
        (await doctor.PostAsync<QueueTokenDto>($"/api/v1/queue/{patient.TokenId}/call", new CallTokenRequest(patient.Room)))
            .Status.ShouldBe(TokenStatus.Called);

        // Opens the encounter from the visit: the visit and the appointment are now in consultation.
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.PatientId, patient.VisitId, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null,
            ChiefComplaint: null));
        encounter.Status.ShouldBe(EncounterStatus.InProgress);
        encounter.Type.ShouldBe(EncounterType.OutPatient);
        encounter.PractitionerId.ShouldBe(patient.Doctor.Id);
        encounter.ChiefComplaint.ShouldBe(Complaint, "The encounter starts from the complaint given when booking.");
        (await doctor.GetAsync<VisitDetail>($"/api/v1/visits/{patient.VisitId}")).Status.ShouldBe(VisitStatus.InProgress);
        (await AppointmentAsync(doctor, patient.AppointmentId)).Status.ShouldBe(AppointmentStatus.InConsultation);

        // Writes the notes and a provisional diagnosis.
        encounter = await doctor.PutAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}", new UpdateEncounterRequest(
            ChiefComplaint: Complaint,
            HistoryOfPresentIllness: "Intermittent fever up to 101 F for three days with body ache. No cough, rash or bleeding.",
            Examination: "Temperature 100.4 F, pulse 96/min, BP 122/78 mmHg. Throat mildly congested, chest clear.",
            Assessment: "Acute febrile illness, probably viral.",
            Plan: "CRP today. Paracetamol for the fever, oral fluids. Review with the report.",
            FollowUpDate: null, FollowUpAdvice: "Come back if the fever lasts beyond two more days.",
            ReferralDepartmentId: null, ReferralPractitionerId: null, ReferralExternal: null, ReferralReason: null,
            encounter.Version));
        encounter.Assessment.ShouldBe("Acute febrile illness, probably viral.");
        var diagnosis = await doctor.PostAsync<DiagnosisDto>($"/api/v1/emr/encounters/{encounter.Id}/diagnoses",
            new AddDiagnosisRequest("R50.9", Title: null, DiagnosisType.Provisional, IsPrimary: true, OnsetDate: null, DiagnosisStatus.Active, Notes: null));
        diagnosis.Title.ShouldBe("Fever, unspecified", "The title comes from the ICD-10 catalogue.");

        // Orders a CRP from the seeded lab catalogue.
        var crp = (await doctor.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?category=Laboratory&search={CrpCode}"))
            .Single(s => s.Code == CrpCode);
        var order = await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.PatientId, encounter.Id, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Fever for three days",
            [new OrderLineRequest(crp.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));
        order.VisitId.ShouldBe(patient.VisitId, "An order from the encounter belongs to its visit.");
        order.Status.ShouldBe(ClinicalOrderStatus.Active);
        order.Items.ShouldHaveSingleItem().Status.ShouldBe(OrderItemStatus.Ordered);

        // Prescribes paracetamol, which the pharmacy has in stock, and signs the e-prescription.
        var drug = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Paracetamol")).Single(d => d.Code == ParacetamolCode);
        drug.Available.ShouldNotBeNull("The facility has a pharmacy store.").ShouldBeGreaterThan(0m, "The pharmacy has paracetamol in stock.");
        var prescription = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patient.PatientId, encounter.Id, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null,
            Advice: "Drink plenty of fluids and rest.", RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    drug.Id, drug.Name, drug.GenericName, drug.Strength, drug.DosageForm, Route: "Oral", Dose: "1 tablet", DoseFrequency.TDS,
                    CustomFrequency: null, DosesPerDay: null, CourseDays, Quantity: null, Instructions: "After food", drug.IsControlled,
                    drug.ControlledSchedule),
            ]));
        prescription.Status.ShouldBe(PrescriptionStatus.Draft);
        prescription.Items.ShouldHaveSingleItem().Quantity.ShouldBe(3m * CourseDays, "One tablet three times a day is calculated for the course.");

        var check = await doctor.PostAsync<SafetyCheckResponse>($"/api/v1/clinical/prescriptions/{prescription.Id}/check");
        check.IsBlocked.ShouldBeFalse("Nothing should block paracetamol for a patient with no allergies or other medicines.");
        check.RequiresAcknowledgement.ShouldBeFalse("A standard paracetamol dose raises no warning.");
        var signed = await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{prescription.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: false, ConfirmControlledDrugs: false, prescription.Version));
        signed.Prescription.Status.ShouldBe(PrescriptionStatus.Signed);

        // Signs the note and ends the consultation: the visit and the appointment are complete.
        (await doctor.PostAsync<EncounterDetail>($"/api/v1/emr/encounters/{encounter.Id}/sign", new SignEncounterRequest(encounter.Version)))
            .Status.ShouldBe(EncounterStatus.Signed);
        (await doctor.PostAsync<QueueTokenDto>($"/api/v1/queue/{patient.TokenId}/complete")).Status.ShouldBe(TokenStatus.Completed);
        (await doctor.GetAsync<VisitDetail>($"/api/v1/visits/{patient.VisitId}")).Status.ShouldBe(VisitStatus.Completed);
        (await AppointmentAsync(doctor, patient.AppointmentId)).Status.ShouldBe(AppointmentStatus.Completed);

        return new Consultation(order.Id, crp, signed.Prescription, drug);
    }

    private async Task<LabWork> LabFindsTheUnpaidSampleAsync(CheckedInPatient patient)
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);

        // The doctor's order reached the lab in the same transaction: an accession whose tube waits on the collection list.
        var waiting = await lab.GetAsync<PagedResult<PendingCollectionItem>>($"/api/v1/lab/samples/pending-collection?search={patient.Uhid}");
        var tube = waiting.Items.ShouldHaveSingleItem("The CRP needs one tube.");
        tube.PatientId.ShouldBe(patient.PatientId);
        tube.PatientClass.ShouldBe(PatientClass.OutPatient);
        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{tube.LabOrderId}");
        order.VisitId.ShouldBe(patient.VisitId);
        var test = order.Items.ShouldHaveSingleItem();
        test.TestCode.ShouldBe(CrpCode);
        test.Status.ShouldBe(LabOrderItemStatus.Ordered);
        test.ChargeError.ShouldBeNull();
        var chargeId = test.ChargeId.ShouldNotBeNull("Diagnostics charges the test to Billing when it is ordered.");

        // The payment gate (lab.requirePaymentBeforeCollection, on by default): an unpaid out-patient test is not drawn.
        using var refused = await lab.SendRawAsync(HttpMethod.Post, $"/api/v1/lab/orders/{tube.LabOrderId}/collect", new CollectSamplesRequest(null, null, null));
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ProblemCodeAsync(refused)).ShouldBe("lab.payment_pending");

        return new LabWork(tube.LabOrderId, tube.SampleId, tube.Barcode, test.Id, chargeId);
    }

    private async Task<BillDetail> BillingCollectsTheOpBillAsync(CheckedInPatient patient, Consultation consultation, LabWork labWork)
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // The bill is drafted from the visit's charges, as the modules posted them: the registration fee and the
        // consultation when the patient checked in (Billing), the test when it was ordered (Diagnostics).
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(patient.VisitId, PatientId: null, ChargeIds: null));
        draft.Status.ShouldBe(BillStatus.Draft);
        draft.Type.ShouldBe(BillType.OutPatient);
        draft.Lines.Select(l => l.ServiceCode).ShouldBe(new[] { "REG-NEW", "CONS-NEW", CrpCode }, ignoreOrder: true);

        var registration = draft.Lines.Single(l => l.ServiceCode == "REG-NEW");
        registration.UnitPrice.ShouldBe(await TariffPriceAsync(billing, registration.ServiceItemId, patient.VisitId), "Registration is charged at the tariff.");
        var consultationLine = draft.Lines.Single(l => l.ServiceCode == "CONS-NEW");
        consultationLine.UnitPrice.ShouldBe(patient.ConsultationFee, "The consultation is charged at the doctor's fee quoted at booking.");
        consultationLine.PractitionerId.ShouldBe(patient.Doctor.Id);
        var labTest = draft.Lines.Single(l => l.ServiceCode == CrpCode);
        labTest.ChargeId.ShouldBe(labWork.ChargeId, "The test on the bill is the charge Diagnostics posted.");
        labTest.UnitPrice.ShouldBe(await TariffPriceAsync(billing, consultation.LabTest.Id, patient.VisitId), "The test is charged at the tariff.");

        var total = registration.UnitPrice + consultationLine.UnitPrice + labTest.UnitPrice;
        draft.Lines.ShouldAllBe(l => l.Quantity == 1m && l.NetAmount == l.UnitPrice, "One of each service, with no discount or tax.");
        draft.TaxAmount.ShouldBe(0m, "Healthcare services are exempt from GST.");
        draft.NetAmount.ShouldBe(total);
        draft.PatientAmount.ShouldBe(total, "A self-paying patient pays the whole bill.");

        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.Status.ShouldBe(BillStatus.Finalized);
        bill.BillNumber.ShouldNotBeNullOrWhiteSpace("Finalizing allocates the bill number.");
        bill.DueAmount.ShouldBe(total);

        // The patient pays in cash at the counter, in the cashier's open shift.
        await OpenCounterShiftAsync(billing);
        var receipt = await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new BillPaymentRequest(
            bill.Id, [new PaymentLineInput(BillPaymentMode.Cash, bill.DueAmount, null, null, null, null, null, null)], Notes: null));
        receipt.Purpose.ShouldBe(ReceiptPurpose.BillPayment);
        receipt.BillId.ShouldBe(bill.Id);
        receipt.Amount.ShouldBe(total);
        receipt.ReceiptNumber.ShouldNotBeNullOrWhiteSpace();

        bill = await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");
        bill.Status.ShouldBe(BillStatus.Paid);
        bill.PaidAmount.ShouldBe(total);
        bill.DueAmount.ShouldBe(0m);
        bill.Receipts.ShouldHaveSingleItem().ReceiptNumber.ShouldBe(receipt.ReceiptNumber);
        return bill;
    }

    private async Task<Guid> LabReportsTheTestAsync(CheckedInPatient patient, LabWork labWork)
    {
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);

        // Paid: the technician draws the sample and prints its barcode label.
        var label = (await lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{labWork.OrderId}/collect",
            new CollectSamplesRequest(null, null, "OP sample collection"))).ShouldHaveSingleItem();
        label.SampleId.ShouldBe(labWork.SampleId);
        label.Barcode.ShouldBe(labWork.Barcode);
        label.Uhid.ShouldBe(patient.Uhid);
        label.CollectedAt.ShouldNotBeNull();

        // The lab receives the tube by scanning its barcode.
        var received = (await lab.PostAsync<List<ReceiveOutcome>>("/api/v1/lab/samples/receive", new ReceiveSamplesRequest([label.Barcode])))
            .ShouldHaveSingleItem();
        received.Received.ShouldBeTrue(received.Message);

        // Enters the result, which is flagged against the reference interval.
        var sheet = await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{labWork.SampleId}/results");
        sheet.SampleStatus.ShouldBe(SampleStatus.Received);
        var line = sheet.Lines.ShouldHaveSingleItem();
        line.Status.ShouldBe(LabResultStatus.Pending);
        var entered = (await lab.PostAsync<List<ResultSheetLine>>("/api/v1/lab/results",
            new EnterResultsRequest([new ResultEntry(line.ResultId, CrpValue, null, null, null, null)]))).ShouldHaveSingleItem();
        entered.Status.ShouldBe(LabResultStatus.Entered);
        entered.Flag.ShouldBe(ResultFlag.Normal, "3.2 mg/L is within the CRP reference interval.");

        // Pathologist: verifies the result and releases the report in one step.
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var verified = await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify",
            new VerifyResultsRequest([line.ResultId], QcOverrideReason: null, CriticalCall: null, Release: true));
        verified.Verified.ShouldBe(1);
        verified.ReleasedItemIds.ShouldHaveSingleItem().ShouldBe(labWork.TestItemId);

        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{labWork.OrderId}");
        order.Status.ShouldBe(LabOrderStatus.Completed);
        order.Items.ShouldHaveSingleItem().Status.ShouldBe(LabOrderItemStatus.Released);
        order.ReportStatus.ShouldBe(LabReportStatus.Released);
        return order.ReportId.ShouldNotBeNull("A released test has a report.");
    }

    private async Task DoctorSeesTheReportAsync(CheckedInPatient patient, Consultation consultation, Guid reportId)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var pathologistId = (await pathologist.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();

        // The report carries the value, the flag and the pathologist's signature.
        var report = await doctor.GetAsync<LabReportView>($"/api/v1/lab/reports/{reportId}");
        report.Status.ShouldBe(LabReportStatus.Released);
        report.IsPartial.ShouldBeFalse();
        report.Patient.Uhid.ShouldBe(patient.Uhid);
        var result = report.Sections.SelectMany(s => s.Tests).ShouldHaveSingleItem().Results.ShouldHaveSingleItem();
        result.Value.ShouldBe(CrpValue);
        result.Unit.ShouldBe("mg/L");
        result.Flag.ShouldBe(ResultFlag.Normal);
        result.Status.ShouldBe(LabResultStatus.Verified);
        report.Signatories.ShouldContain(s => s.UserId == pathologistId, "The verifying pathologist signs the report.");
        report.VerificationCode.ShouldNotBeNullOrWhiteSpace("A released report carries its QR verification code.");

        // The EMR: the result waits on the patient's timeline for the doctor's review, and the order line is complete.
        var timeline = await doctor.GetAsync<PagedResult<TimelineEntry>>($"/api/v1/emr/patients/{patient.PatientId}/timeline?types=Result");
        var entry = timeline.Items.ShouldHaveSingleItem();
        entry.Id.ShouldBe(reportId);
        entry.Status.ShouldBe("Awaiting review");
        var order = await doctor.GetAsync<OrderDto>($"/api/v1/clinical/orders/{consultation.OrderId}");
        order.Status.ShouldBe(ClinicalOrderStatus.Completed);
        order.Items.ShouldHaveSingleItem().Status.ShouldBe(OrderItemStatus.Completed);
    }

    private async Task ThePatientGetsTheReportAsync(CheckedInPatient patient, Guid reportId)
    {
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var report = await doctor.GetAsync<LabReportView>($"/api/v1/lab/reports/{reportId}");
        var fileName = $"lab-report-{report.ReportNumber}.pdf";

        // At the counter the server's document is printed: the same one that becomes the PDF and the patient's copy.
        var printable = await doctor.GetAsync<PrintableDocumentDto>($"/api/v1/lab/reports/{reportId}/document");
        printable.Title.ShouldBe($"Lab report {report.ReportNumber}");
        printable.PageSize.ShouldBe("A4");
        printable.Html.ShouldContain(patient.Uhid);
        printable.Html.ShouldContain(CrpValue);
        printable.Html.ShouldContain($"Verification code {report.VerificationCode}");

        var portal = await server.SignInToPortalAsync(patient.Phone);
        if (!server.Services.GetRequiredService<IPdfRenderer>().Status.Available)
        {
            // A machine without Microsoft Edge can't make PDFs: the download says why, and the screen offers to print.
            using var unavailable = await doctor.SendRawAsync(HttpMethod.Get, $"/api/v1/lab/reports/{reportId}/pdf");
            unavailable.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await ProblemCodeAsync(unavailable)).ShouldBe("pdf.unavailable");
            return;
        }

        // Staff save it as a PDF, and the patient downloads the same PDF from the portal.
        (await PdfAsync(doctor, $"/api/v1/lab/reports/{reportId}/pdf")).ShouldBe(fileName);
        (await PdfAsync(portal, $"/api/v1/portal/reports/lab/{reportId}/pdf")).ShouldBe(fileName);

        // The "report ready" e-mail carries the report's PDF.
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var accession = (await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{report.LabOrderId}")).AccessionNumber;
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var sent = await EventuallyAsync(async () => (await admin.GetAsync<PagedResult<SimulatorMessageDto>>(
                $"/api/v1/admin/integrations/simulator/messages?channel=Email&search={Uri.EscapeDataString(accession)}")).Items.FirstOrDefault(),
            "The report-ready e-mail was not sent.");
        sent.Details.ShouldNotBeNull().ShouldContain($"attachments: {fileName}");
    }

    private async Task<PharmacySaleDetail> PharmacistDispensesAsync(CheckedInPatient patient, Consultation consultation)
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var store = (await pharmacist.GetAsync<List<StoreDto>>("/api/v1/inventory/stores?type=Pharmacy")).Single(s => s.Code == "OPPH");

        // The signed prescription waits in the e-prescription queue.
        var queue = await pharmacist.GetAsync<PagedResult<QueueListItem>>($"/api/v1/pharmacy/queue?search={consultation.Prescription.PrescriptionNumber}");
        var waiting = queue.Items.ShouldHaveSingleItem();
        waiting.PatientId.ShouldBe(patient.PatientId);
        waiting.Status.ShouldBe(QueueEntryStatus.Pending);
        waiting.IsInpatient.ShouldBeFalse();
        var entry = await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{waiting.Id}?storeId={store.Id}");
        var line = entry.Lines.ShouldHaveSingleItem();
        line.DrugItemId.ShouldBe(consultation.Drug.Id);
        var quantity = consultation.Prescription.Items.Single().Quantity;
        line.RequestedQuantity.ShouldBe(quantity);

        // FEFO: the batch that expires first, and still lasts the course, is handed over at its MRP (GST included).
        var before = await BatchesAsync(pharmacist, store.Id, consultation.Drug.Id);
        var courseEnds = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IndiaOffset).DateTime).AddDays(CourseDays);
        var fefo = before
            .Where(b => !b.IsExpired && !b.IsBlocked && b.Available >= quantity && b.ExpiryDate >= courseEnds)
            .OrderBy(b => b.ExpiryDate)
            .First();
        before.ShouldContain(b => b.ExpiryDate > fefo.ExpiryDate, "The OP pharmacy also holds a later-expiring batch, so FEFO has a choice.");
        var price = Math.Round(quantity * fefo.Mrp, MidpointRounding.AwayFromZero);

        var created = await pharmacist.PostAsync<PharmacySaleCreated>($"/api/v1/pharmacy/queue/{entry.Id}/dispense", new DispenseRequest(
            store.Id,
            [new DispenseLineRequest(line.Id, consultation.Drug.Id, quantity, BatchId: null, SubstitutionReason: null, PrescriberConsent: false)],
            CounsellingNotes: "One tablet three times a day after food, for three days.",
            DiscountPercent: 0m,
            [new SalePayment(PharmacyPaymentMode.Cash, price, Reference: null)],
            ConfirmControlledDrugs: false));
        created.NetAmount.ShouldBe(price, "Medicines are sold at MRP, rounded to the rupee.");
        created.PaidAmount.ShouldBe(price);
        created.DueAmount.ShouldBe(0m);
        created.Status.ShouldBe(SaleStatus.Paid);

        var sale = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{created.Id}");
        sale.PatientId.ShouldBe(patient.PatientId);
        sale.PrescriptionNumber.ShouldBe(consultation.Prescription.PrescriptionNumber);
        var sold = sale.Items.ShouldHaveSingleItem();
        sold.BatchNumber.ShouldBe(fefo.BatchNumber, "FEFO hands over the batch that expires first.");
        sold.ExpiryDate.ShouldBe(fefo.ExpiryDate);
        sold.Quantity.ShouldBe(quantity);
        sold.UnitMrp.ShouldBe(fefo.Mrp);
        sold.NetAmount.ShouldBe(Math.Round(quantity * fefo.Mrp, 2, MidpointRounding.AwayFromZero));
        sold.GstRate.ShouldBeGreaterThan(0m, "Medicines carry GST.");
        (sold.TaxableAmount + sold.CgstAmount + sold.SgstAmount).ShouldBe(sold.NetAmount, "GST is carved out of the MRP.");

        // The stock went down on the FEFO batch only, and the prescription is fully dispensed.
        var after = await BatchesAsync(pharmacist, store.Id, consultation.Drug.Id);
        after.Single(b => b.BatchId == fefo.BatchId).Available.ShouldBe(fefo.Available - quantity);
        foreach (var other in before.Where(b => b.BatchId != fefo.BatchId))
        {
            after.Single(b => b.BatchId == other.BatchId).Available.ShouldBe(other.Available, $"Batch {other.BatchNumber} is not touched.");
        }

        (await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{entry.Id}")).Status.ShouldBe(QueueEntryStatus.Dispensed);
        return sale;
    }

    private async Task NothingIsLeftToPayAsync(CheckedInPatient patient, BillDetail bill, PharmacySaleDetail sale)
    {
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        var charges = await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?visitId={patient.VisitId}");
        charges.Items.Count.ShouldBe(bill.Lines.Count);
        charges.Items.ShouldAllBe(c => c.Status == ChargeStatus.Billed && c.BillId == bill.Id, "Every service of the visit is on the OP bill.");

        var dues = await billing.GetAsync<PatientDues>($"/api/v1/billing/patients/{patient.PatientId}/dues");
        dues.TotalDue.ShouldBe(0m, "The OP bill is paid.");
        dues.Bills.ShouldBeEmpty();
        sale.DueAmount.ShouldBe(0m, "The pharmacy invoice is paid.");
    }

    /// <summary>
    /// The doctor's first free in-person slot today. When the seeded sessions have none left (evening, Sunday, or all
    /// booked), the hospital administrator adds an extra OPD session for the rest of the day.
    /// </summary>
    private async Task<SlotDto> FreeSlotTodayAsync(ApiClient frontOffice, PractitionerDto doctor)
    {
        if (await FirstFreeSlotAsync(frontOffice, doctor.Id) is { } slot)
        {
            return slot;
        }

        // Only today's appointments can be checked in, so a run in the last minutes before midnight waits for the next day.
        var now = DateTimeOffset.UtcNow.ToOffset(IndiaOffset);
        if (now.TimeOfDay > new TimeSpan(23, 45, 0))
        {
            await Task.Delay(TimeSpan.FromDays(1) - now.TimeOfDay + TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            if (await FirstFreeSlotAsync(frontOffice, doctor.Id) is { } morning)
            {
                return morning;
            }

            now = DateTimeOffset.UtcNow.ToOffset(IndiaOffset);
        }

        var startMinute = (int)Math.Ceiling((now.TimeOfDay.TotalMinutes + 3) / 5) * 5;
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await admin.PostAsync<ScheduleSaved>("/api/v1/scheduling/exceptions", new SaveScheduleExceptionRequest(
            doctor.Id, ScheduleExceptionType.ExtraSession, DateOnly.FromDateTime(now.DateTime), DateOnly.FromDateTime(now.DateTime),
            new TimeOnly(startMinute / 60, startMinute % 60), new TimeOnly(23, 59), "Extra OPD session", AllFacilities: false,
            doctor.DepartmentId, SlotMinutes: 5, CapacityPerSlot: 1, ConsultationMode.InPerson, Room: null, Version: null));
        return await FirstFreeSlotAsync(frontOffice, doctor.Id)
               ?? throw new InvalidOperationException($"{doctor.DisplayName} has no free slot even after the extra session.");
    }

    /// <summary>Today's first free slot that starts late enough to book (the booking refuses a slot already started).</summary>
    private static async Task<SlotDto?> FirstFreeSlotAsync(ApiClient frontOffice, Guid practitionerId)
    {
        var availability = await frontOffice.GetAsync<List<PractitionerAvailability>>(
            $"/api/v1/scheduling/slots?practitionerId={practitionerId}&mode=InPerson&includeFull=false");
        var earliest = DateTimeOffset.UtcNow.AddMinutes(2);
        return availability.SelectMany(a => a.Slots).Where(s => s.Available > 0 && s.StartsAt > earliest).MinBy(s => s.StartsAt);
    }

    private static Task<AppointmentDetail> AppointmentAsync(ApiClient api, Guid appointmentId) =>
        api.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{appointmentId}");

    private static async Task<decimal> TariffPriceAsync(ApiClient billing, Guid serviceItemId, Guid visitId) =>
        (await billing.GetAsync<PriceQuoteDto>($"/api/v1/billing/tariffs/quote?serviceItemId={serviceItemId}&visitId={visitId}")).UnitPrice;

    /// <summary>Money is collected only in an open cashier shift; the billing desk opens one unless it already has.</summary>
    private static async Task OpenCounterShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    private static Task<List<BatchStock>> BatchesAsync(ApiClient pharmacist, Guid storeId, Guid itemId) =>
        pharmacist.GetAsync<List<BatchStock>>($"/api/v1/pharmacy/stock/batches?storeId={storeId}&itemId={itemId}");

    /// <summary>Downloads a PDF and returns the file name the server gave it.</summary>
    private static async Task<string?> PdfAsync(ApiClient api, string path)
    {
        using var response = await api.SendRawAsync(HttpMethod.Get, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {path}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/pdf");
        var pdf = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        Encoding.ASCII.GetString(pdf, 0, 5).ShouldBe("%PDF-");
        var disposition = response.Content.Headers.ContentDisposition.ShouldNotBeNull();
        disposition.DispositionType.ShouldBe("attachment");
        return disposition.FileNameStar ?? disposition.FileName?.Trim('"');
    }

    /// <summary>Polls until the outbox has delivered (it runs every two seconds), for at most a minute.</summary>
    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> read, string failure)
        where T : class
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(1); waited += TimeSpan.FromSeconds(1))
        {
            if (await read() is { } value)
            {
                return value;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }

        throw new ShouldAssertException(failure);
    }

    /// <summary>
    /// Sends e-mail to the integrations simulator for the rest of the test, then puts the setting back: the server is
    /// shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> EmailToSimulatorAsync()
    {
        const string key = "integrations.email";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await admin.PutAsync($"/api/v1/admin/settings/{key}", new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["fromAddress"] = "reports@hospital.example" }, null, false, null));
        return new Restore(() => admin.PutAsync($"/api/v1/admin/settings/{key}",
            new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }

    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, TestContext.Current.CancellationToken);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string RandomLetters(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());

    private static string RandomDigits(int count) => new(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))).ToArray());

    /// <summary>What the front office hands on: the patient, the doctor and the checked-in visit.</summary>
    private sealed record CheckedInPatient(
        Guid PatientId, string Uhid, string Phone, PractitionerDto Doctor, Guid AppointmentId, decimal ConsultationFee, string? Room, Guid VisitId, Guid TokenId);

    /// <summary>What the doctor ordered: the clinical order of the lab test and the signed e-prescription.</summary>
    private sealed record Consultation(Guid OrderId, ServiceItemDto LabTest, PrescriptionDto Prescription, PrescribableDrug Drug);

    /// <summary>The lab's accession for the ordered test: its tube and the charge Diagnostics posted.</summary>
    private sealed record LabWork(Guid OrderId, Guid SampleId, string Barcode, Guid TestItemId, Guid ChargeId);
}
