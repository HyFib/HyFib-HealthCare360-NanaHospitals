using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Analytics.Features.Journeys;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtChargeStatus = HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common.ChargeStatus;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Operations and the stay, over the real API. A surgery booked at the OPD joins the stay when the patient is admitted, and
/// one booked while the patient is in a bed is the stay's from whatever visit it came, so the theatre's and the implant's
/// charges reach the IP bill and its discharge clearance. A surgery booked before the stay and still waiting when the stay
/// ends goes back to waiting on its own. A patient the theatre has (in pre-op, on the table, in recovery until discharged
/// from it) is not discharged, and the ward sees him back only once he leaves recovery.
/// </summary>
public sealed class TheatreStayTests(HospitalServer server)
{
    private const string Bookings = "/api/v1/ot/bookings";
    private const string Hernia = "PROC-HERNIA";
    private const string HerniaRepair = "Inguinal hernia repair with mesh";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_surgery_booked_at_the_opd_joins_the_stay_and_its_theatre_and_implant_charges_reach_the_ip_bill_and_the_discharge_clearance()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patientId = await RegisterPatientAsync(frontOffice);

        // The surgeon books the hernia repair at the OPD, before the patient is admitted for it: it belongs to no stay yet.
        var booking = await BookAsync(doctor, patientId, visitId: null);
        (await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{booking.Id}")).AdmissionId.ShouldBeNull();

        // Admitted the day before, the repair is the stay's.
        var admission = await AdmitAsync(frontOffice, doctor, patientId);
        (await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{booking.Id}")).AdmissionId.ShouldBe(admission.Id);

        // In the minor theatre the nurse records the mesh, brought on consignment, and the surgeon completes the operation.
        await OnTheTableAsync(doctor, booking.Id);
        var mesh = await nurse.PostAsync<SurgeryMaterialDto>($"{Bookings}/{booking.Id}/materials", new AddSurgeryMaterialRequest(
            MaterialKind.Implant, null, "Polypropylene mesh", "Ethicon", "PM2611", null, null, 1m, 4_500m, "Prolene", "Polypropylene, 15 x 15 cm", 5_200m));
        mesh.ChargeStatus.ShouldBe(OtChargeStatus.Posted, mesh.ChargeError);
        await doctor.PostAsync($"{Bookings}/{booking.Id}/complete");
        await BackFromRecoveryAsync(nurse, booking.Id);

        // The implant, the procedure, the theatre's time and the surgeon's fee are on the stay's account...
        string[] theatre = ["OT-IMPLANT", Hernia, "OT-MINOR-HR", "OT-SURGEON-FEE"];
        var charges = (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admission.Id}&pageSize=200")).Items
            .Where(c => theatre.Contains(c.ServiceCode)).ToList();
        charges.Select(c => c.ServiceCode).ShouldBe(theatre, ignoreOrder: true);

        // ...so the final bill drafted when the discharge begins carries them, and the clearance waits for them.
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(null, "Wound healthy; fit to go home"));
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        var final = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        var billed = final.Lines.Select(l => l.ServiceCode).ToList();
        theatre.ShouldAllBe(code => billed.Contains(code));
        var clearance = await billing.GetAsync<ClearanceDto>($"/api/v1/billing/ip-accounts/{account.Id}/clearance");
        clearance.Cleared.ShouldBeFalse();
        clearance.UnbilledAmount.ShouldBeGreaterThanOrEqualTo(charges.Sum(c => c.NetAmount));

        // Management lets the family pay at the follow-up visit, and he goes home.
        await DischargeWithDuesAsync(nurse, billing, admission.Id);
        await CleanBedAsync(nurse, admission.Id);
    }

    [Fact]
    public async Task Surgeries_booked_during_a_stay_go_with_it_while_one_booked_before_it_is_given_back_when_it_ends()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patientId = await RegisterPatientAsync(frontOffice);
        var opVisit = await CheckInAsync(frontOffice, doctor, patientId);

        // A repair booked at the OPD for later, and scheduled; then the patient is admitted for something else.
        var planned = await BookAsync(doctor, patientId, visitId: null);
        var slot = await ScheduleAsync(doctor, planned.Id);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);
        (await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{planned.Id}")).AdmissionId.ShouldBe(admission.Id);

        // While he is in a bed, a surgery ordered on the stay's visit and one booked at the OT desk from his OPD visit are the
        // stay's too, whatever visit they came from.
        var hernia = (await doctor.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?search={Hernia}")).Single(s => s.Code == Hernia);
        var order = await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patientId, EncounterId: null, admission.VisitId, AdmissionId: null, OrderPriority.Urgent, "Obstructed hernia",
            [new OrderLineRequest(hernia.Id, null)], OrderSetId: null, ConfirmDuplicates: false));
        var ordered = await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{await BookingOfAsync(order.Items.Single().Id)}");
        (ordered.AdmissionId, ordered.VisitId).ShouldBe((admission.Id, admission.VisitId));
        var fromOpd = await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{(await BookAsync(doctor, patientId, opVisit.Id)).Id}");
        (fromOpd.AdmissionId, fromOpd.VisitId).ShouldBe((admission.Id, opVisit.Id));

        // The admission was a mistake and is cancelled: the surgeries booked under it go with it, and the planned repair
        // waits for its day again, slot and all, as before he was admitted.
        await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted to the wrong ward"));
        foreach (var id in new[] { ordered.Id, fromOpd.Id })
        {
            (await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{id}")).Status.ShouldBe(SurgeryStatus.Cancelled);
        }

        var givenBack = await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{planned.Id}");
        (givenBack.Status, givenBack.AdmissionId, givenBack.ScheduledStart).ShouldBe((SurgeryStatus.Scheduled, null, slot.Start));
        await CleanBedAsync(nurse, admission.Id);
        await doctor.PostAsync($"{Bookings}/{planned.Id}/cancel", new SurgeryReasonRequest("Booked only for a test of the stay"));
    }

    [Fact]
    public async Task A_patient_in_pre_op_on_the_table_or_in_recovery_is_not_discharged_until_back_on_the_ward()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patientId = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);

        // The repair is booked for the stay and the patient is received in pre-op.
        var booking = await BookAsync(doctor, patientId, visitId: null);
        await ScheduleAsync(doctor, booking.Id);
        await doctor.PostAsync($"{Bookings}/{booking.Id}/receive");

        // The ward has begun the discharge and management has let the family pay later, so billing holds nothing back; the
        // theatre does. The refusal names the case and changes nothing.
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(null, "Family asks to take him home"));
        await ApproveDuesAsync(billing, admission.Id);
        (await DischargeRefusalAsync(nurse, admission.Id)).ShouldBe(("discharge.in_theatre",
            $"The patient is in pre-op for surgery {booking.BookingNumber} ({HerniaRepair}): complete, postpone or cancel it before the discharge."));

        // Nor on the table.
        await OnTheTableAsync(doctor, booking.Id, signedOut: false);
        (await DischargeRefusalAsync(nurse, admission.Id)).ShouldBe(("discharge.in_theatre",
            $"Surgery {booking.BookingNumber} ({HerniaRepair}) is in progress: the patient can be discharged once it is completed in the theatre."));
        (await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}")).Status.ShouldBe(AdmissionStatus.DischargeInitiated);
        (await WhereOnTheBoardAsync(nurse, admission.Id)).ShouldStartWith("In theatre · ");

        // Nor in recovery, from the WHO Sign-out, even once the surgeon has completed the operation: the ward sees him in
        // recovery, not back in his bed.
        await OnTheTableAsync(doctor, booking.Id);
        var inRecovery = ("discharge.in_recovery",
            $"The patient is in recovery after surgery {booking.BookingNumber} ({HerniaRepair}): discharge them from recovery before the discharge.");
        (await DischargeRefusalAsync(nurse, admission.Id)).ShouldBe(inRecovery);
        await doctor.PostAsync($"{Bookings}/{booking.Id}/complete");
        (await DischargeRefusalAsync(nurse, admission.Id)).ShouldBe(inRecovery);
        (await WhereOnTheBoardAsync(nurse, admission.Id)).ShouldStartWith("In recovery · ");

        // Discharged from recovery, he is back on the ward, in his bed, and goes home.
        await BackFromRecoveryAsync(nurse, booking.Id);
        (await WhereOnTheBoardAsync(nurse, admission.Id)).ShouldNotContain("recovery");
        var discharged = await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home after the repair", NotifyPatient: false));
        discharged.Status.ShouldBe(AdmissionStatus.Discharged);
        await CleanBedAsync(nurse, admission.Id);
    }

    [Fact]
    public async Task A_discharge_saved_while_the_patient_is_received_in_pre_op_waits_for_the_theatre_and_is_refused()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patientId = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);
        var booking = await BookAsync(doctor, patientId, visitId: null);
        await ScheduleAsync(doctor, booking.Id);

        // The ward reports the patient gone (an absconded stay waits for the nurse in charge), before the theatre has him.
        var asked = await server.AskAbscondedAsync(nurse, admission.Id, "Not in the ward at the 6 pm round");
        var inCharge = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var request = await inCharge.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");

        // The theatre is caught as it saves the patient into pre-op: a transaction of the test holds the case's row, which the
        // theatre writes once it holds the stay's lock.
        await using var hold = await HeldTransaction.HoldAsync(server, $"SELECT 1 FROM specialtycare.surgery_bookings WHERE id = {booking.Id} FOR UPDATE");
        var receive = doctor.SendRawAsync(HttpMethod.Post, $"{Bookings}/{booking.Id}/receive");
        await HeldTransaction.WaitUntilLockedAsync(server, admission.Id);

        // The nurse in charge approves meanwhile: the discharge waits for the theatre.
        var discharge = inCharge.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, request.Version));
        (await Task.WhenAny(discharge, Task.Delay(TimeSpan.FromSeconds(2), Ct))).ShouldNotBe(discharge, "the discharge waits for the theatre");

        // The theatre saves, and the discharge then finds the patient in pre-op: the stay goes on.
        await hold.ReleaseAsync();
        using (var received = await receive)
        {
            received.IsSuccessStatusCode.ShouldBeTrue(await received.Content.ReadAsStringAsync(Ct));
        }

        using (var refused = await discharge)
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync(Ct));
            (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("discharge.in_theatre");
        }

        (await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}")).Status.ShouldBe(AdmissionStatus.Admitted);

        // While the theatre has him, the ward is refused at once; the case is called off, and the discharge then goes through.
        (await DischargeRefusalAsync(nurse, admission.Id, DischargeType.Absconded)).Code.ShouldBe("discharge.in_theatre");
        await doctor.PostAsync($"{Bookings}/{booking.Id}/cancel", new SurgeryReasonRequest("Patient left the ward"));
        await server.AbscondAsync(nurse, admission.Id, "Not in the ward at the 6 pm round");
        (await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}")).Status.ShouldBe(AdmissionStatus.Absconded);
        await CleanBedAsync(nurse, admission.Id);
    }

    [Fact]
    public async Task A_patient_received_in_pre_op_while_the_stay_is_discharged_waits_for_the_discharge_and_finds_the_case_cancelled_with_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patientId = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);
        var booking = await BookAsync(doctor, patientId, visitId: null);
        await ScheduleAsync(doctor, booking.Id);

        // The patient has absconded and the nurse in charge approves closing the stay. The discharge is caught between its
        // reads and its save: a transaction of the test holds the stay's row, which the discharge writes last.
        var asked = await server.AskAbscondedAsync(nurse, admission.Id, "Not in the ward at the 6 pm round");
        var inCharge = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var request = await inCharge.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        await using var hold = await HeldTransaction.HoldAsync(server, $"SELECT 1 FROM inpatient.admissions WHERE id = {admission.Id} FOR UPDATE");
        var discharge = inCharge.PostAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, request.Version));
        await HeldTransaction.WaitUntilLockedAsync(server, admission.Id);

        // The theatre receives the patient meanwhile: it waits for the discharge.
        var receive = doctor.SendRawAsync(HttpMethod.Post, $"{Bookings}/{booking.Id}/receive");
        (await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(2), Ct))).ShouldNotBe(receive, "the theatre waits for the discharge");

        // The stay ends, and the case booked during it with it: the theatre then finds it cancelled.
        await hold.ReleaseAsync();
        (await discharge).Status.ShouldBe(ApprovalStatus.Approved);
        (await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}")).Status.ShouldBe(AdmissionStatus.Absconded);
        using (var refused = await receive)
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync(Ct));
            (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("surgery.not_scheduled");
        }

        (await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{booking.Id}")).Status.ShouldBe(SurgeryStatus.Cancelled);
        await CleanBedAsync(nurse, admission.Id);
    }

    /// <summary>The surgeon books a hernia repair under local anaesthesia for the patient, at the OT desk.</summary>
    private static async Task<SurgeryCreated> BookAsync(ApiClient doctor, Guid patientId, Guid? visitId)
    {
        var surgeonId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var hernia = (await doctor.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?search={Hernia}")).Single(s => s.Code == Hernia);
        return await doctor.PostAsync<SurgeryCreated>(Bookings, new CreateSurgeryBookingRequest(
            patientId, AdmissionId: null, visitId, hernia.Id, ProcedureCode: null, HerniaRepair, Laterality.Right, "Right inguinal hernia", surgeonId,
            AssistantIds: null, AnaesthetistId: null, AnaesthesiaType.Local, SurgeryPriority.Elective, PreferredDate: null, TheatreId: null,
            ScheduledStart: null, ScheduledEnd: null, Notes: null));
    }

    /// <summary>
    /// The OT coordinator gives the booking an hour in the minor theatre on a day years ahead, when no other test of the run
    /// has the theatre, the surgeon or the patient.
    /// </summary>
    private static async Task<(DateTimeOffset Start, DateTimeOffset End)> ScheduleAsync(ApiClient doctor, Guid bookingId)
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero).AddDays(Random.Shared.Next(400, 4000)).AddHours(4);
        var booking = await doctor.GetAsync<SurgeryDetail>($"{Bookings}/{bookingId}");
        var theatreId = await MinorTheatreAsync(doctor);
        await doctor.PostAsync($"{Bookings}/{bookingId}/schedule", new ScheduleSurgeryRequest(theatreId, start, start.AddHours(1), booking.Version));
        return (start, start.AddHours(1));
    }

    /// <summary>
    /// The surgery is under way in the minor theatre, its WHO Sign-in, Time-out and Sign-out done: the theatre's steps before
    /// completion are covered by their own tests.
    /// </summary>
    private async Task OnTheTableAsync(ApiClient doctor, Guid bookingId, bool signedOut = true)
    {
        var theatreId = await MinorTheatreAsync(doctor);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var booking = await db.Set<SurgeryBooking>().IgnoreQueryFilters().SingleAsync(b => b.Id == bookingId, Ct);
        var now = DateTimeOffset.UtcNow;
        booking.TheatreId ??= theatreId;
        booking.Status = SurgeryStatus.InProgress;
        booking.SignInCompletedAt = booking.InRoomAt = now.AddMinutes(-70);
        booking.TimeOutCompletedAt = now.AddMinutes(-65);
        booking.StartedAt = now.AddMinutes(-60);
        booking.SignOutCompletedAt = signedOut ? now.AddMinutes(-5) : null;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>
    /// The recovery nurse receives the patient, charts him awake on room air (a modified Aldrete score of 10) and sends him
    /// back to the ward. The repair was under local anaesthesia, so there is no anaesthetist to hand over.
    /// </summary>
    private static async Task BackFromRecoveryAsync(ApiClient nurse, Guid bookingId)
    {
        var recovery = $"{Bookings}/{bookingId}/recovery";
        await nurse.PostAsync<RecoveryRecordDto>($"{recovery}/receive", new ReceiveInRecoveryRequest(AirwayDevice.None, OxygenDelivery.RoomAir, PreAnaesthesiaSystolic: 130));
        await nurse.PostAsync<RecoveryRecordDto>($"{recovery}/observations", new AddRecoveryObservationRequest(
            80, 128, 78, 98, false, 14, 36.6m, RecoveryConsciousness.Alert, RecoveryBreathing.DeepBreathAndCough, 4, 1, Ponv.None));
        (await nurse.PostAsync<RecoveryRecordDto>($"{recovery}/discharge", new DischargeFromRecoveryRequest(RecoveryDestination.Ward)))
            .Destination.ShouldBe(RecoveryDestination.Ward);
    }

    /// <summary>Where the board of in-patients of the ward says the patient is: the bed, or in theatre or recovery and the bed.</summary>
    private static async Task<string> WhereOnTheBoardAsync(ApiClient nurse, Guid admissionId)
    {
        var stay = await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admissionId}");
        var board = await nurse.GetAsync<PagedResult<InHospitalPatient>>($"/api/v1/journeys/in-hospital?kind=Inpatient&wardId={stay.WardId}&pageSize=100");
        return board.Items.Single(r => r.Journey.Id == admissionId).Place.ShouldNotBeNull();
    }

    private static async Task<Guid> MinorTheatreAsync(ApiClient doctor) =>
        (await doctor.GetAsync<List<TheatreDto>>("/api/v1/ot/theatres")).Single(t => t.Code == "OT-3").Id;

    /// <summary>The booking the surgery order line became on the OT list.</summary>
    private async Task<Guid> BookingOfAsync(Guid orderItemId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SurgeryBooking>().IgnoreQueryFilters()
            .Where(b => b.OrderItemId == orderItemId).Select(b => b.Id).SingleAsync(Ct);
    }

    /// <summary>The nurse completes the discharge and is refused as against the rules (422): its code and detail.</summary>
    private static async Task<(string? Code, string? Detail)> DischargeRefusalAsync(ApiClient nurse, Guid admissionId, DischargeType type = DischargeType.Normal)
    {
        using var response = await nurse.SendRawAsync(HttpMethod.Post, $"/api/v1/inpatient/admissions/{admissionId}/discharge/complete",
            new CompleteDischargeRequest(type, null, "Discharged home", NotifyPatient: false,
                Absconded: type == DischargeType.Absconded ? new AbscondedReport(DateTimeOffset.UtcNow.AddMinutes(-30), "Ward staff", false, null, null, false, null) : null));
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (problem.GetProperty("code").GetString(), problem.GetProperty("detail").GetString());
    }

    /// <summary>Management approves discharging the patient with his dues, which the family will pay at the follow-up visit.</summary>
    private async Task ApproveDuesAsync(ApiClient billing, Guid admissionId)
    {
        var management = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admissionId}");
        await management.PostAsync($"/api/v1/billing/ip-accounts/{account.Id}/discharge-approval",
            new ApproveDischargeWithDuesRequest("Family to pay at the follow-up visit", account.Version));
    }

    /// <summary>Management lets the patient go with his dues and the nurse completes the discharge he is ready for.</summary>
    private async Task DischargeWithDuesAsync(ApiClient nurse, ApiClient billing, Guid admissionId)
    {
        await ApproveDuesAsync(billing, admissionId);
        (await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admissionId}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home", NotifyPatient: false))).Status.ShouldBe(AdmissionStatus.Discharged);
    }

    /// <summary>
    /// Housekeeping cleans the bed a discharge or a cancelled admission released: the run's tests read the same wards' boards
    /// and housekeeping lists.
    /// </summary>
    private static async Task CleanBedAsync(ApiClient nurse, Guid admissionId)
    {
        var stay = await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admissionId}");
        var task = (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={stay.WardId}"))
            .Single(t => t.BedId == stay.BedId && t.AdmissionId == stay.Id);
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        (await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned")))
            .Status.ShouldBe(HousekeepingStatus.Completed);
    }

    /// <summary>Front office: checks the patient in to see the doctor in his department, without a queue token.</summary>
    private static async Task<VisitDetail> CheckInAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var departmentId = (await frontOffice.GetAsync<JsonElement>($"/api/v1/masters/practitioners/{practitionerId}")).GetProperty("departmentId").GetGuid();
        return await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Swelling in the groin",
            false, null, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
    }

    /// <summary>Front office: admits the patient under the doctor to a bed of the male general ward added for it (<see cref="TestBeds"/>).</summary>
    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Right inguinal hernia", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>A new patient; the random surname, date of birth and mobile number keep the duplicate check quiet.</summary>
    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Murugan"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
