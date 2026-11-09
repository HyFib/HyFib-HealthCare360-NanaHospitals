using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Features.LabConnections;
using HyFib.HealthCare360.Modules.Analytics.Domain.Journeys;
using HyFib.HealthCare360.Modules.Analytics.Features.Journeys;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Domain;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Quality;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabMasters;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabReports;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabResults;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Diagnostics.Features.Portal;
using HyFib.HealthCare360.Modules.Diagnostics.Features.QualityControl;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;
using BillPaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;
using BillPaymentRequest = HyFib.HealthCare360.Modules.Billing.Features.Payments.CollectPaymentRequest;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The lab journey end to end, with the lab asking for a second pathologist: a doctor orders a fasting sugar → the OP bill
/// carries the test and is paid → the sample is drawn and received → the chemistry analyser sends a critical result (the
/// training analyser, which builds the analyser's own message) → a pathologist verifies it, the call to the doctor
/// documented → the report is released → the doctor acknowledges the critical result (another doctor may not without
/// saying they cover the patient) → the patient opens the report on the portal. Each hand-off is checked where the next
/// module sees it: the bill line, the journey's stages, the critical call register and the doctor's results list.
/// </summary>
public sealed class LabJourneyTests(HospitalServer server)
{
    private const string Connections = "/api/v1/integrations/lab/connections";
    private const string CriticalSugar = "512";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_doctors_order_is_paid_drawn_measured_verified_by_a_pathologist_released_acknowledged_and_read_on_the_portal()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var otherDoctor = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var pathologist = await server.SignInAsync(HospitalServer.Users.Pathologist);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // A new patient sees the doctor, who orders a fasting sugar from the encounter.
        var phone = "9" + Digits(9);
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Mohan"), MiddleName: null, LastName: "Labjourney" + Letters(6), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1962, 1, 1).AddDays(Random.Shared.Next(3650)), AgeYears: null, AgeMonths: null, Phone: phone, AlternatePhone: null,
            Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null,
            EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        var visit = await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patient.Id, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Excessive thirst",
            false, null, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patient.Id, visit.Id, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: null));
        var fbsService = (await doctor.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?category=Laboratory&search=LAB-FBS")).Single(s => s.Code == "LAB-FBS");
        var clinicalOrder = await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patient.Id, encounter.Id, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Polyuria and thirst",
            [new OrderLineRequest(fbsService.Id, Notes: null)], OrderSetId: null, ConfirmDuplicates: false));

        // Hand-off to the lab and to billing: the accession waits to be drawn, and its charge is the line on the visit's bill.
        var tube = (await lab.GetAsync<PagedResult<PendingCollectionItem>>($"/api/v1/lab/samples/pending-collection?search={patient.Uhid}")).Items.ShouldHaveSingleItem();
        var order = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{tube.LabOrderId}");
        var chargeId = order.Items.ShouldHaveSingleItem().ChargeId.ShouldNotBeNull("Diagnostics charged the test when it was ordered");
        (await ReachedAsync(doctor, patient.Id, tube.LabOrderId, LabJourney.PaymentCleared.Key)).ShouldBeFalse("an out-patient test is paid before it is drawn");
        using (var unpaid = await lab.SendRawAsync(HttpMethod.Post, $"/api/v1/lab/orders/{tube.LabOrderId}/collect", new CollectSamplesRequest(null, null, null)))
        {
            unpaid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        }

        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, PatientId: null, ChargeIds: null));
        draft.Lines.Single(l => l.ServiceCode == "LAB-FBS").ChargeId.ShouldBe(chargeId);
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        await OpenCounterShiftAsync(billing);
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new BillPaymentRequest(
            bill.Id, [new PaymentLineInput(BillPaymentMode.Cash, bill.DueAmount, null, null, null, null, null, null)], Notes: null));
        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).Status.ShouldBe(BillStatus.Paid);
        (await ReachedAsync(doctor, patient.Id, tube.LabOrderId, LabJourney.PaymentCleared.Key)).ShouldBeTrue();

        // Paid: drawn and received.
        await lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{tube.LabOrderId}/collect", new CollectSamplesRequest(null, null, "OP collection"));
        (await lab.PostAsync<List<ReceiveOutcome>>("/api/v1/lab/samples/receive", new ReceiveSamplesRequest([tube.Barcode]))).ShouldHaveSingleItem().Received.ShouldBeTrue();
        (await ReachedAsync(doctor, patient.Id, tube.LabOrderId, LabJourney.Received.Key)).ShouldBeTrue();

        await using (await SeparateVerifierAsync(admin))
        await using (await TrainingAnalyserAsync(admin, lab))
        {
            // The chemistry analyser sends the result (the training analyser builds its message from the value entered).
            var channel = (await admin.GetAsync<TrainingSampleDto>($"{Connections}/EM360/training/samples/{tube.Barcode}")).Channels.ShouldHaveSingleItem();
            (await admin.PostAsync<TrainingResultsOutcome>($"{Connections}/EM360/training/results",
                new TrainingResultsRequest(tube.Barcode, [new TrainingResult(channel, CriticalSugar, "mg/dL")]))).Accepted.ShouldBeTrue();
            var line = (await lab.GetAsync<ResultSheet>($"/api/v1/lab/samples/{tube.SampleId}/results")).Lines.ShouldHaveSingleItem();
            (line.Status, line.IsCritical, line.InstrumentCode, line.AutoValidated).ShouldBe((LabResultStatus.Entered, true, "EM360", false),
                "a critical value is never verified automatically");
            (await ReachedAsync(doctor, patient.Id, tube.LabOrderId, LabJourney.Resulted.Key)).ShouldBeTrue();

            // The technician may not verify; the pathologist verifies only with the call to the doctor documented.
            using (var technician = await lab.SendRawAsync(HttpMethod.Post, "/api/v1/lab/results/verify", new VerifyResultsRequest([line.ResultId], null, null, Release: true)))
            {
                technician.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var noCall = await pathologist.SendRawAsync(HttpMethod.Post, "/api/v1/lab/results/verify", new VerifyResultsRequest([line.ResultId], null, null, Release: true)))
            {
                (await CodeAsync(noCall)).ShouldBe("lab.critical_call_required");
            }

            var call = new CriticalCallDetails("Dr. Demo Doctor", "Ordering doctor", practitionerId, CommunicationMethod.Phone, DateTimeOffset.UtcNow.AddMinutes(-2),
                ReadBackConfirmed: true, "Patient asked to come back today");
            var verified = await pathologist.PostAsync<VerificationOutcome>("/api/v1/lab/results/verify", new VerifyResultsRequest([line.ResultId], null, call, Release: true));
            (verified.Verified, verified.CriticalCallsRecorded).ShouldBe((1, 1));
            verified.ReleasedItemIds.ShouldHaveSingleItem();
        }

        // Hand-off to the critical call register: who was told, and that it was read back.
        var registered = (await pathologist.GetAsync<PagedResult<CriticalCallDto>>("/api/v1/lab/critical-calls?kind=Laboratory&pageSize=200")).Items
            .Where(c => c.PatientId == patient.Id).ShouldHaveSingleItem();
        (registered.InformedPersonName, registered.ReadBackConfirmed).ShouldBe(("Dr. Demo Doctor", true));

        // Released: the report is on the doctor's results list, critical, and the order line is complete.
        var reportId = (await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{tube.LabOrderId}")).ReportId.ShouldNotBeNull();
        (await ReachedAsync(doctor, patient.Id, tube.LabOrderId, LabJourney.Released.Key)).ShouldBeTrue();
        (await doctor.GetAsync<OrderDto>($"/api/v1/clinical/orders/{clinicalOrder.Id}")).Items.ShouldHaveSingleItem().Status.ShouldBe(OrderItemStatus.Completed);
        var review = (await doctor.GetAsync<EmrWorklist>("/api/v1/emr/worklist")).ResultsToReview.Single(r => r.ReportId == reportId);
        review.HasCriticalFindings.ShouldBeTrue();

        // With the second pathologist on, a critical result is acknowledged by its ordering doctor, or by another doctor who
        // records that they cover the patient.
        await using (await SeparateVerifierAsync(admin))
        {
            using (var other = await otherDoctor.SendRawAsync(HttpMethod.Post, $"/api/v1/emr/results/{review.Id}/acknowledge", new AcknowledgeResultRequest(null)))
            {
                other.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await other.Content.ReadAsStringAsync(Ct));
            }

            (await ReachedAsync(doctor, patient.Id, tube.LabOrderId, LabJourney.Reviewed.Key)).ShouldBeFalse("the refused acknowledgement reviewed nothing");
            using var ordering = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/emr/results/{review.Id}/acknowledge", new AcknowledgeResultRequest("Seen; insulin started"));
            ordering.StatusCode.ShouldBe(HttpStatusCode.NoContent, await ordering.Content.ReadAsStringAsync(Ct));
        }

        var journey = (await doctor.GetAsync<PatientJourneys>($"/api/v1/patients/{patient.Id}/journey")).LabOrders.Single(j => j.Id == tube.LabOrderId);
        journey.Status.ShouldBe(JourneyStatus.Completed);
        journey.Stages.Select(s => s.Key).ShouldBe(LabJourney.Stages.Select(s => s.Key));
        journey.Stages.ShouldAllBe(s => s.State == StageState.Done);

        // The patient reads it on the portal, with the critical value as verified.
        var portal = await server.SignInToPortalAsync(phone);
        (await portal.GetAsync<List<PortalReportItem>>($"/api/v1/portal/reports?patientId={patient.Id}")).ShouldContain(r => r.Id == reportId);
        var onPortal = await portal.GetAsync<LabReportView>($"/api/v1/portal/reports/lab/{reportId}");
        var shown = onPortal.Sections.SelectMany(s => s.Tests).SelectMany(t => t.Results).Single();
        (shown.Value, shown.Status).ShouldBe((CriticalSugar, LabResultStatus.Verified));
    }

    /// <summary>Whether the accession has reached the stage (done, or the one it is at now) on the patient's record.</summary>
    private static async Task<bool> ReachedAsync(ApiClient api, Guid patientId, Guid labOrderId, string key) =>
        (await StageAsync(api, patientId, labOrderId, key)).State is StageState.Done or StageState.Current;

    /// <summary>The lab journey's stage for this accession on the patient's record.</summary>
    private static async Task<JourneyStage> StageAsync(ApiClient api, Guid patientId, Guid labOrderId, string key) =>
        (await api.GetAsync<PatientJourneys>($"/api/v1/patients/{patientId}/journey")).LabOrders.Single(j => j.Id == labOrderId).Stages.Single(s => s.Key == key);

    /// <summary>
    /// The chemistry analyser on the training transport, its glucose QC run and accepted today; disposing removes the
    /// connection again, so no other test meets it.
    /// </summary>
    private async Task<IAsyncDisposable> TrainingAnalyserAsync(ApiClient admin, ApiClient lab)
    {
        var fbs = (await lab.GetAsync<PagedResult<LabTestListItem>>("/api/v1/lab/tests?search=LAB-FBS")).Items.Single(t => t.Code == "LAB-FBS");
        foreach (var control in (await lab.GetAsync<List<QcLotDto>>($"/api/v1/lab/qc/lots?labTestId={fbs.Id}")).Where(l => l.InstrumentCode == "EM360" && !l.IsExpired))
        {
            (await lab.PostAsync<QcRunDto>("/api/v1/lab/qc/runs", new RecordQcRunRequest(control.Id, control.TargetMean, null))).Outcome.ShouldBe(QcOutcome.Accepted);
        }

        await server.SaveSettingAsync(admin, "integrations.lab", new SaveSettingRequest(JsonNode.Parse("""{"enabled":true}"""), null, false, null));
        var version = (await admin.GetAsync<LabConnectionsDto>(Connections)).ListVersion;
        await admin.PutAsync($"{Connections}/EM360", new SaveLabConnectionRequest("Simulator", true, null, null, null, null, null, null, null, version));
        return new Restore(async () =>
        {
            var current = (await admin.GetAsync<LabConnectionsDto>(Connections)).ListVersion;
            using var removed = await admin.SendRawAsync(HttpMethod.Delete, $"{Connections}/EM360?version={current}");
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        });
    }

    private async Task<IAsyncDisposable> SeparateVerifierAsync(ApiClient admin)
    {
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await admin.SaveSettingAsync(LabResultService.SeparateVerifierSetting, new SaveSettingRequest(JsonValue.Create(true), facilityId, false, null));
        return new Restore(async () =>
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(LabResultService.SeparateVerifierSetting, facilityId, Ct);
        });
    }

    private static async Task OpenCounterShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString();

    private static string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('0' + Random.Shared.Next(10))));

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
