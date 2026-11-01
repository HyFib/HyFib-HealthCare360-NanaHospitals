using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.HealthCheckups;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Imaging;
using HyFib.HealthCare360.Modules.Diagnostics.Domain.Laboratory;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabOrders;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Orders and the end of a visit or a stay, over the real API. Nothing is ordered on a visit that is over or was
/// cancelled, nor for a stay the patient was discharged from or that was cancelled. When a visit is cancelled, the tests
/// whose sample is not drawn and the scans not done are withdrawn with their charges; when a stay ends, its tests whose
/// sample is not drawn are, while a test already drawn carries on so its result reaches the record. The order, the lab
/// and the bill say the same.
/// </summary>
public sealed class ClosedEncounterTests(HospitalServer server)
{
    private const string Orders = "/api/v1/clinical/orders";
    private const string Bookings = "/api/v1/health-checkups/bookings";
    private const string Cbc = "LAB-CBC";
    private const string Crp = "LAB-CRP";
    private const string ChestXray = "RAD-XR-CHEST";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Nothing_more_is_ordered_on_a_visit_closed_at_the_desk_or_cancelled()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patientId = await RegisterPatientAsync(frontOffice);
        var cbc = await ServiceAsync(doctor, Cbc);

        // The doctor sees the patient, and the desk closes the visit before the blood count is ordered.
        var visit = await CheckInAsync(frontOffice, doctor, patientId);
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, visit.Id, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Fever"));
        await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/complete");

        // Neither on the visit nor from the consultation's note: the refusal names the visit.
        foreach (var order in new[] { Order(patientId, cbc, visitId: visit.Id), Order(patientId, cbc) with { EncounterId = encounter.Id } })
        {
            var refused = await RefusalAsync(doctor, order);
            (refused.Code, refused.Detail).ShouldBe(("visit.closed",
                $"Visit {visit.VisitNumber} is closed: nothing more can be ordered on it. Order on the patient's current visit or admission."));
        }

        // A visit checked in by mistake and cancelled takes none either.
        var mistaken = await CheckInAsync(frontOffice, doctor, patientId);
        await frontOffice.PostAsync($"/api/v1/visits/{mistaken.Id}/cancel", new CancelVisitRequest("Checked in twice by mistake"));
        (await RefusalAsync(doctor, Order(patientId, cbc, visitId: mistaken.Id))).Code.ShouldBe("visit.cancelled");
        (await doctor.GetAsync<PagedResult<OrderDto>>($"{Orders}?patientId={patientId}")).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_cancelled_visit_withdraws_its_tests_not_drawn_and_its_scan_not_done_with_their_charges_while_a_drawn_test_carries_on()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patientId = await RegisterPatientAsync(frontOffice);

        // The doctor orders a blood count, a CRP and a chest X-ray at the consultation.
        var visit = await CheckInAsync(frontOffice, doctor, patientId);
        var order = await doctor.PostAsync<OrderDto>(Orders, Order(patientId, [await ServiceAsync(doctor, Cbc), await ServiceAsync(doctor, Crp),
            await ServiceAsync(doctor, ChestXray)], visitId: visit.Id));

        // The lab draws the CRP's tube (the counter waives payment here: this test is about the visit); the blood count's
        // tube is not drawn, nor the X-ray taken, when the patient leaves and the desk cancels the visit.
        var accession = await AccessionAsync(lab, patientId);
        await WithoutPaymentGateAsync(() => lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{accession.Id}/collect",
            new CollectSamplesRequest([TubeOf(accession, Crp)], null, "Left arm")));
        await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/cancel", new CancelVisitRequest("Patient left before the tests"));
        const string reason = "Visit cancelled: Patient left before the tests";

        // The doctor's order shows the blood count and the X-ray withdrawn, with the reason; the CRP goes on.
        var lines = (await doctor.GetAsync<OrderDto>($"{Orders}/{order.Id}")).Items.ToDictionary(i => i.ServiceCode);
        (lines[Cbc].Status, lines[Cbc].CancelReason).ShouldBe((OrderItemStatus.Cancelled, reason));
        (lines[ChestXray].Status, lines[ChestXray].CancelReason).ShouldBe((OrderItemStatus.Cancelled, reason));
        lines[Crp].Status.ShouldBe(OrderItemStatus.Ordered);

        // So does the lab: the blood count is cancelled and off the collection list, the CRP's tube is in its hands.
        accession = await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{accession.Id}");
        var tests = accession.Items.ToDictionary(i => i.TestCode);
        (tests[Cbc].Status, tests[Cbc].CancelReason).ShouldBe((LabOrderItemStatus.Cancelled, reason));
        tests[Crp].Status.ShouldBe(LabOrderItemStatus.Collected);
        accession.Samples.Single(s => s.Id == tests[Cbc].SampleId).Status.ShouldBe(SampleStatus.Cancelled);

        // And radiology: the X-ray is off its worklist.
        (await StudyAsync(order.Items.Single(i => i.ServiceCode == ChestXray).Id)).ShouldBe((ImagingStudyStatus.Cancelled, reason));

        // And the bill: the blood count's and the X-ray's charges are withdrawn, while the drawn CRP stays charged.
        var charges = (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?visitId={visit.Id}&pageSize=200")).Items
            .Where(c => c.ServiceCode is Cbc or Crp or ChestXray).ToDictionary(c => c.ServiceCode);
        (charges[Cbc].Status, charges[ChestXray].Status).ShouldBe((ChargeStatus.Cancelled, ChargeStatus.Cancelled));
        charges[Crp].Status.ShouldBe(ChargeStatus.Unbilled);
    }

    [Fact]
    public async Task A_discharge_withdraws_the_stays_tests_not_drawn_while_a_drawn_one_carries_on_and_the_stay_takes_no_more_orders()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patientId = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);

        // On the evening round the doctor orders a blood count and a CRP; the CRP's tube is drawn at once, the blood count is
        // left for the morning round.
        var order = await doctor.PostAsync<OrderDto>(Orders, Order(patientId, [await ServiceAsync(doctor, Cbc), await ServiceAsync(doctor, Crp)],
            visitId: admission.VisitId, admissionId: admission.Id));
        var accession = await AccessionAsync(lab, patientId);
        await lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{accession.Id}/collect", new CollectSamplesRequest([TubeOf(accession, Crp)], null, "Ward"));

        // The patient goes home before the morning round: management lets him go with his dues.
        await DischargeAsync(nurse, billing, admission);

        // The blood count is withdrawn with the reason, in the order, the lab and the bill; the CRP goes on to its result.
        const string reason = "Patient discharged before the sample was collected";
        var lines = (await doctor.GetAsync<OrderDto>($"{Orders}/{order.Id}")).Items.ToDictionary(i => i.ServiceCode);
        (lines[Cbc].Status, lines[Cbc].CancelReason, lines[Crp].Status).ShouldBe((OrderItemStatus.Cancelled, reason, OrderItemStatus.Ordered));
        var tests = (await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{accession.Id}")).Items.ToDictionary(i => i.TestCode);
        (tests[Cbc].Status, tests[Cbc].CancelReason, tests[Crp].Status).ShouldBe((LabOrderItemStatus.Cancelled, reason, LabOrderItemStatus.Collected));
        var charges = (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admission.Id}&pageSize=200")).Items
            .Where(c => c.ServiceCode is Cbc or Crp).ToDictionary(c => c.ServiceCode);
        (charges[Cbc].Status, charges[Crp].Status).ShouldBe((ChargeStatus.Cancelled, ChargeStatus.Unbilled));

        // Nothing more is ordered for the stay, from the ward or the record: the refusal says the patient was discharged.
        var refused = await RefusalAsync(doctor, Order(patientId, await ServiceAsync(doctor, Cbc), admissionId: admission.Id));
        (refused.Code, refused.Detail).ShouldBe(("admission.closed",
            $"The patient was discharged from admission {admission.AdmissionNumber}: nothing more can be ordered on it. Order on a new visit."));
        await CleanBedAsync(nurse, admission.Id);
    }

    [Fact]
    public async Task A_cancelled_admission_withdraws_its_tests_not_drawn_and_its_scan_not_done_and_takes_no_more_orders()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patientId = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);

        // The doctor orders a blood count and a chest X-ray for the stay, from its record (by the admission alone).
        var order = await doctor.PostAsync<OrderDto>(Orders, Order(patientId, [await ServiceAsync(doctor, Cbc), await ServiceAsync(doctor, ChestXray)],
            admissionId: admission.Id));

        // The admission was made for the wrong patient, and is cancelled before anything was done.
        await frontOffice.PostAsync($"/api/v1/inpatient/admissions/{admission.Id}/cancel", new CancelAdmissionRequest("Admitted under the wrong UHID"));
        const string reason = "Admission cancelled: Admitted under the wrong UHID";
        var lines = (await doctor.GetAsync<OrderDto>($"{Orders}/{order.Id}")).Items;
        lines.ShouldAllBe(l => l.Status == OrderItemStatus.Cancelled && l.CancelReason == reason);
        (await StudyAsync(lines.Single(l => l.ServiceCode == ChestXray).Id)).ShouldBe((ImagingStudyStatus.Cancelled, reason));
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admission.Id}&pageSize=200")).Items
            .Where(c => c.ServiceCode is Cbc or ChestXray).Select(c => c.Status).ShouldBe([ChargeStatus.Cancelled, ChargeStatus.Cancelled]);

        // Nothing more is ordered for it.
        var refused = await RefusalAsync(doctor, Order(patientId, await ServiceAsync(doctor, Cbc), admissionId: admission.Id));
        (refused.Code, refused.Detail).ShouldBe(("admission.cancelled", $"Admission {admission.AdmissionNumber} was cancelled: nothing can be ordered on it."));
        await CleanBedAsync(nurse, admission.Id);
    }

    [Fact]
    public async Task A_cancelled_visit_cancels_the_health_checkup_checked_in_on_it_with_its_package_charge_and_its_tests()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patientId = await RegisterPatientAsync(frontOffice);
        var package = await CheckupPackageAsync(frontOffice, doctor);

        // A walk-in check-up: booked on the patient's visit, so checked in and the package charged at once.
        var visit = await CheckInAsync(frontOffice, doctor, patientId);
        var booking = await frontOffice.PostAsync<BookingDto>(Bookings, new BookCheckupRequest(patientId, package.Id, Today(), visit.Id, null));
        booking.Status.ShouldBe(BookingStatus.CheckedIn);

        // The patient leaves before the blood is drawn, and the desk cancels the visit.
        await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/cancel", new CancelVisitRequest("Patient left before the check-up"));

        // The check-up goes with it, as its own cancellation does: the booking, the package charge and the blood count.
        var cancelled = await frontOffice.GetAsync<BookingDto>($"{Bookings}/{booking.Id}");
        (cancelled.Status, cancelled.CancelReason).ShouldBe((BookingStatus.Cancelled, "Visit cancelled: Patient left before the check-up"));
        (await PackageChargeAsync(billing, visit.Id, package)).Status.ShouldBe(ChargeStatus.Cancelled);
        (await AccessionAsync(lab, patientId)).Items.Single(i => i.TestCode == Cbc).Status.ShouldBe(LabOrderItemStatus.Cancelled);
    }

    [Fact]
    public async Task A_cancelled_visit_keeps_a_health_checkup_whose_package_is_on_a_finalised_bill_with_its_tests_and_tells_the_desk()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var patientId = await RegisterPatientAsync(frontOffice);
        var package = await CheckupPackageAsync(frontOffice, doctor);
        var visit = await CheckInAsync(frontOffice, doctor, patientId);
        var booking = await frontOffice.PostAsync<BookingDto>(Bookings, new BookCheckupRequest(patientId, package.Id, Today(), visit.Id, null));

        // The package is billed and the bill finalised before the desk cancels the visit.
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(visit.Id, null, null));
        await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        await frontOffice.PostAsync($"/api/v1/visits/{visit.Id}/cancel", new CancelVisitRequest("Visit opened twice"));

        // The patient has paid for the check-up: it stays, and so does its blood count, until billing refunds the package.
        (await frontOffice.GetAsync<BookingDto>($"{Bookings}/{booking.Id}")).Status.ShouldBe(BookingStatus.CheckedIn);
        (await PackageChargeAsync(billing, visit.Id, package)).Status.ShouldBe(ChargeStatus.Billed);
        (await AccessionAsync(lab, patientId)).Items.Single(i => i.TestCode == Cbc).Status.ShouldBe(LabOrderItemStatus.Ordered);

        // The check-up desk is told what to do.
        var notice = (await admin.GetAsync<PagedResult<UserNotificationDto>>("/api/v1/notifications?pageSize=200")).Items
            .Where(n => n.Link == $"/health-checkups/bookings/{booking.Id}").ShouldHaveSingleItem();
        notice.Title.ShouldBe("Visit cancelled — health checkup kept");
        notice.Body.ShouldNotBeNull().ShouldContain($"Visit {visit.VisitNumber} was cancelled, but health checkup {booking.BookingNumber}");
    }

    [Fact]
    public async Task A_health_checkup_is_neither_booked_nor_checked_in_on_a_visit_closed_at_the_desk_or_cancelled()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var patientId = await RegisterPatientAsync(frontOffice);
        var package = await CheckupPackageAsync(frontOffice, doctor);
        var closed = await CheckInAsync(frontOffice, doctor, patientId);
        await frontOffice.PostAsync($"/api/v1/visits/{closed.Id}/complete");
        var cancelled = await CheckInAsync(frontOffice, doctor, patientId);
        await frontOffice.PostAsync($"/api/v1/visits/{cancelled.Id}/cancel", new CancelVisitRequest("Checked in twice by mistake"));

        // Not booked as a walk-in on either visit: the refusal names the visit.
        (await RefusalAsync(frontOffice, Bookings, new BookCheckupRequest(patientId, package.Id, Today(), closed.Id, null))).ShouldBe(("visit.closed",
            $"Visit {closed.VisitNumber} is closed: a health checkup cannot be booked or checked in on it. Link the patient's current visit."));
        (await RefusalAsync(frontOffice, Bookings, new BookCheckupRequest(patientId, package.Id, Today(), cancelled.Id, null))).ShouldBe(("visit.cancelled",
            $"Visit {cancelled.VisitNumber} was cancelled: a health checkup cannot be booked or checked in on it."));

        // Nor checked in on one when booked ahead: the booking waits as it was, and nothing is charged.
        var booking = await frontOffice.PostAsync<BookingDto>(Bookings, new BookCheckupRequest(patientId, package.Id, Today(), null, null));
        (await RefusalAsync(frontOffice, $"{Bookings}/{booking.Id}/check-in", new CheckInCheckupRequest(closed.Id))).Code.ShouldBe("visit.closed");
        (await frontOffice.GetAsync<BookingDto>($"{Bookings}/{booking.Id}")).Status.ShouldBe(BookingStatus.Booked);
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?patientId={patientId}&pageSize=200")).Items
            .ShouldNotContain(c => c.ServiceCode == package.Code);
    }

    [Fact]
    public async Task An_order_placed_while_the_stay_is_being_discharged_waits_for_the_discharge_and_is_refused()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patientId = await RegisterPatientAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);
        var cbc = await ServiceAsync(doctor, Cbc);

        // The discharge is caught between its reads and its save: a transaction of the test holds the stay's row, which the
        // discharge writes last. The patient left the ward unseen, so it is recorded without billing clearance.
        await using var hold = await HeldTransaction.HoldAsync(server, $"SELECT 1 FROM inpatient.admissions WHERE id = {admission.Id} FOR UPDATE");
        var discharge = nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Absconded, DischargedAt: null, "Not in the ward at the 6 pm round", NotifyPatient: false));
        await HeldTransaction.WaitUntilLockedAsync(server, admission.Id);

        // The doctor orders a blood count for the stay meanwhile: the order waits for the discharge to be saved.
        var order = doctor.SendRawAsync(HttpMethod.Post, Orders, Order(patientId, cbc, admissionId: admission.Id));
        (await Task.WhenAny(order, Task.Delay(TimeSpan.FromSeconds(2), Ct))).ShouldNotBe(order, "the order waits for the discharge");

        // The discharge is saved, and the order then finds the stay ended.
        await hold.ReleaseAsync();
        (await discharge).Status.ShouldBe(AdmissionStatus.Absconded);
        using var refused = await order;
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync(Ct));
        (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("admission.closed");
        (await doctor.GetAsync<PagedResult<OrderDto>>($"{Orders}?patientId={patientId}")).Items.ShouldBeEmpty();
        await CleanBedAsync(nurse, admission.Id);
    }

    [Fact]
    public async Task A_visit_cancelled_while_an_order_is_being_placed_on_it_waits_for_the_order_and_withdraws_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);
        var patientId = await RegisterPatientAsync(frontOffice);
        var visit = await CheckInAsync(frontOffice, doctor, patientId);
        var cbc = await ServiceAsync(doctor, Cbc);

        // The order is caught as it reads the visit: a transaction of the test holds the visit's row, which the order reads
        // FOR SHARE once it holds the visit's lock.
        await using var hold = await HeldTransaction.HoldAsync(server, $"SELECT 1 FROM patients.visits WHERE id = {visit.Id} FOR UPDATE");
        var order = doctor.PostAsync<OrderDto>(Orders, Order(patientId, cbc, visitId: visit.Id));
        await HeldTransaction.WaitUntilLockedAsync(server, visit.Id);

        // The desk cancels the visit meanwhile: the cancellation waits for the order.
        var cancel = frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/visits/{visit.Id}/cancel", new CancelVisitRequest("Patient left before the blood test"));
        (await Task.WhenAny(cancel, Task.Delay(TimeSpan.FromSeconds(2), Ct))).ShouldNotBe(cancel, "the cancellation waits for the order");

        // The order is saved first, and the cancellation then withdraws its blood count, in the order and in the lab.
        await hold.ReleaseAsync();
        var placed = await order;
        using var cancelled = await cancel;
        cancelled.IsSuccessStatusCode.ShouldBeTrue(await cancelled.Content.ReadAsStringAsync(Ct));
        const string reason = "Visit cancelled: Patient left before the blood test";
        var line = (await doctor.GetAsync<OrderDto>($"{Orders}/{placed.Id}")).Items.ShouldHaveSingleItem();
        (line.Status, line.CancelReason).ShouldBe((OrderItemStatus.Cancelled, reason));
        (await AccessionAsync(lab, patientId)).Items.Single(i => i.TestCode == Cbc).Status.ShouldBe(LabOrderItemStatus.Cancelled);
    }

    [Fact]
    public async Task An_order_placed_while_the_desk_closes_the_visit_waits_for_the_close_and_is_refused()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var patientId = await RegisterPatientAsync(frontOffice);
        var visit = await CheckInAsync(frontOffice, doctor, patientId);

        // The desk's close of the visit is written but not yet committed, as it is for the moment its save takes. A visit
        // closed at the desk withdraws nothing, so its close takes no lock: the order reads the visit's row FOR SHARE.
        await using var hold = await HeldTransaction.HoldAsync(server, $"UPDATE patients.visits SET status = 'Completed' WHERE id = {visit.Id}");
        var order = doctor.SendRawAsync(HttpMethod.Post, Orders, Order(patientId, await ServiceAsync(doctor, Cbc), visitId: visit.Id));
        (await Task.WhenAny(order, Task.Delay(TimeSpan.FromSeconds(2), Ct))).ShouldNotBe(order, "the order waits for the close");

        // The close commits, and the order finds the visit closed.
        await hold.CommitAsync();
        using var refused = await order;
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await refused.Content.ReadAsStringAsync(Ct));
        (await refused.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("visit.closed");
    }

    /// <summary>A check-up of a blood count alone, for anyone.</summary>
    private static async Task<PackageDto> CheckupPackageAsync(ApiClient frontOffice, ApiClient doctor) =>
        await frontOffice.PostAsync<PackageDto>("/api/v1/health-checkups/packages", new SavePackageRequest(
            "HC-CE" + Letters(8), "Fever screen", null, PackageGender.Any, MinAgeYears: null, MaxAgeYears: null, Price: 1500m, IsActive: true,
            [new PackageItemRequest(PackageItemKind.Investigation, await ServiceAsync(doctor, Cbc), null, PackageGender.Any, null)], Version: null));

    /// <summary>The package's one charge on the visit.</summary>
    private static async Task<ChargeDto> PackageChargeAsync(ApiClient billing, Guid visitId, PackageDto package) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?visitId={visitId}&pageSize=200")).Items
        .Single(c => c.ServiceCode == package.Code);

    private DateOnly Today() => server.Services.GetRequiredService<TimeProvider>().Today();

    private static PlaceOrderRequest Order(Guid patientId, Guid serviceItemId, Guid? visitId = null, Guid? admissionId = null) =>
        Order(patientId, [serviceItemId], visitId, admissionId);

    private static PlaceOrderRequest Order(Guid patientId, IEnumerable<Guid> serviceItemIds, Guid? visitId = null, Guid? admissionId = null) =>
        new(patientId, EncounterId: null, visitId, admissionId, OrderPriority.Routine, "Fever for three days",
            [.. serviceItemIds.Select(id => new OrderLineRequest(id, null))], OrderSetId: null, ConfirmDuplicates: true);

    /// <summary>The doctor places the order and is refused as against the rules (422): its code and detail.</summary>
    private static Task<(string? Code, string? Detail)> RefusalAsync(ApiClient doctor, PlaceOrderRequest order) => RefusalAsync(doctor, Orders, order);

    /// <summary>The request is refused as against the rules (422): its code and detail.</summary>
    private static async Task<(string? Code, string? Detail)> RefusalAsync(ApiClient client, string path, object body)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync(Ct));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (problem.GetProperty("code").GetString(), problem.GetProperty("detail").GetString());
    }

    private static async Task<Guid> ServiceAsync(ApiClient doctor, string code) =>
        (await doctor.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?search={code}")).Single(s => s.Code == code).Id;

    /// <summary>The patient's one lab accession, made from the doctor's order.</summary>
    private static async Task<LabOrderDetail> AccessionAsync(ApiClient lab, Guid patientId)
    {
        var listed = (await lab.GetAsync<PagedResult<LabOrderListItem>>($"/api/v1/lab/orders?patientId={patientId}")).Items.ShouldHaveSingleItem();
        return await lab.GetAsync<LabOrderDetail>($"/api/v1/lab/orders/{listed.Id}");
    }

    private static Guid TubeOf(LabOrderDetail accession, string testCode) =>
        accession.Items.Single(i => i.TestCode == testCode).SampleId.ShouldNotBeNull();

    /// <summary>The status and cancellation reason of the imaging study made from an order line.</summary>
    private async Task<(ImagingStudyStatus Status, string? CancelReason)> StudyAsync(Guid orderItemId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var study = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ImagingStudy>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(s => s.SourceOrderItemId == orderItemId, Ct);
        return (study.Status, study.CancelReason);
    }

    /// <summary>
    /// Runs <paramref name="collect"/> with the lab's payment gate off in the administrator's facility, as the counter waives
    /// it, and puts it back.
    /// </summary>
    private async Task WithoutPaymentGateAsync(Func<Task> collect)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var facilityId = (await admin.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId
                         ?? throw new InvalidOperationException("The hospital administrator has no facility.");
        await admin.SaveSettingAsync("lab.requirePaymentBeforeCollection", new SaveSettingRequest(JsonValue.Create(false), facilityId, false, null));
        try
        {
            await collect();
        }
        finally
        {
            await admin.SaveSettingAsync("lab.requirePaymentBeforeCollection", new SaveSettingRequest(JsonValue.Create(true), facilityId, false, null));
        }
    }

    /// <summary>
    /// The nurse begins the discharge, management approves discharging the patient with his dues, and the nurse completes
    /// it: the patient goes home.
    /// </summary>
    private async Task DischargeAsync(ApiClient nurse, ApiClient billing, AdmissionCreated admission)
    {
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(null, "Fit to go home"));
        var management = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        await management.PostAsync($"/api/v1/billing/ip-accounts/{account.Id}/discharge-approval",
            new ApproveDischargeWithDuesRequest("Family to pay at the follow-up visit", account.Version));
        (await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home", NotifyPatient: false))).Status.ShouldBe(AdmissionStatus.Discharged);
    }

    /// <summary>Housekeeping cleans the bed a discharge or a cancelled admission released: the wards' beds are shared by the run.</summary>
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
            patientId, VisitType.OutPatient, departmentId, practitionerId, null, PayerType.Self, null, ReferralSource.Self, null, "Fever",
            false, null, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
    }

    /// <summary>Front office: admits the patient under the doctor to a free bed of the male general ward.</summary>
    private static async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patientId}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Fever under evaluation", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>A new patient; the random surname, date of birth and mobile number keep the duplicate check quiet.</summary>
    private static async Task<Guid> RegisterPatientAsync(ApiClient frontOffice)
    {
        var surname = Letters(8);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Arjun"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1965, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null,
            AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        return created.Id;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
