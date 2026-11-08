using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Inpatient;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Inpatient.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Maker-checker for the ward's medication orders (slice 5). A nurse, or a doctor writing another doctor's order, records
/// the doctor's verbal or telephone order, read back, and only that doctor countersigns it, from their own account, within
/// the hospital's time; a high-alert, controlled or chemotherapy medicine gives no dose until then unless it was taken as
/// an emergency order. A medicine typed in is handled as high-alert, a dose with no order is always witnessed, and a
/// nurse's stop holds the doses until the doctor countersigns it. What is not countersigned in time is listed for the ward
/// and the doctor, and both the doctor and the ward's nurse in charge are told.
/// </summary>
public sealed class MedicationOrderCountersignTests(HospitalServer server)
{
    private const string Witness = "/api/v1/auth/witness";
    private const string Awaiting = "/api/v1/nursing/medication-orders/awaiting-countersignature";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_nurse_writes_a_doctors_order_only_as_a_verbal_or_telephone_order_read_back_for_the_doctor_to_countersign()
    {
        var (nurse, doctor, admission) = await WardAsync();
        var me = await MeAsync(doctor);
        var ondansetron = await DrugAsync("Ondansetron", highAlert: false);
        var orders = $"/api/v1/nursing/admissions/{admission.Id}/medication-orders";

        // Naming no doctor (she has no practitioner record), in the doctor's name as if written by him, or not read back: refused.
        (await RefusalAsync(nurse, orders, Order(ondansetron, doctor: null, mode: MedicationOrderMode.Verbal, readBack: true)))
            .ShouldBe((HttpStatusCode.BadRequest, null));
        (await RefusalAsync(nurse, orders, Order(ondansetron, me.PractitionerId))).ShouldBe((HttpStatusCode.UnprocessableEntity, "medication.verbal_order_required"));
        (await RefusalAsync(nurse, orders, Order(ondansetron, me.PractitionerId, MedicationOrderMode.Written))).Code.ShouldBe("medication.verbal_order_required");
        (await RefusalAsync(nurse, orders, Order(ondansetron, me.PractitionerId, MedicationOrderMode.Telephone, readBack: false)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "medication.read_back_required"));

        var order = await nurse.PostAsync<MedicationOrderDto>(orders, Order(ondansetron, me.PractitionerId, MedicationOrderMode.Telephone, readBack: true, frequency: "STAT"));
        (order.Mode, order.AwaitingCountersignature, order.OrderedByPractitionerId, order.CanCountersign).ShouldBe(
            (MedicationOrderMode.Telephone, true, me.PractitionerId, false));
        order.EnteredByName.ShouldNotBeNullOrWhiteSpace();
        (order.CountersignDueAt!.Value - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromHours(23.9), TimeSpan.FromHours(24));

        // An ordinary medicine is given before the doctor countersigns: its dose is on the round now.
        order.DosesHeld.ShouldBeFalse();
        (await ChartAsync(nurse, admission.Id)).ShouldContain(e => e.MedicationOrderId == order.Id && e.Status == MarStatus.Due && e.HeldBecause == null);

        // The doctor is told, and the order is in the trail as a telephone order.
        (await NotificationsAsync(me.Id, "Verbal order to countersign")).ShouldContain(n => n.Body!.Contains(order.DrugName, StringComparison.Ordinal));
        (await AuditAsync(order.Id)).ShouldContain(e => e.Action == "medication.verbal_order" && e.Severity == AuditSeverity.Warning
            && e.Summary!.Contains("telephone order", StringComparison.Ordinal));

        // Only the doctor named countersigns it: not the nurse (no prescriber), not another doctor, not on an old version.
        var countersign = $"/api/v1/nursing/medication-orders/{order.Id}/countersign";
        (await RefusalAsync(nurse, countersign, new CountersignMedicationOrderRequest(order.Version))).Status.ShouldBe(HttpStatusCode.Forbidden);
        var secondDoctor = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        (await RefusalAsync(secondDoctor, countersign, new CountersignMedicationOrderRequest(order.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "medication.countersign_not_prescriber"));
        (await RefusalAsync(doctor, countersign, new CountersignMedicationOrderRequest(order.Version + 1))).Status.ShouldBe(HttpStatusCode.Conflict);

        var listed = await doctor.GetAsync<List<MedicationOrderDto>>(orders);
        listed.Single(o => o.Id == order.Id).CanCountersign.ShouldBeTrue("the doctor named is offered the countersignature");
        var signed = await doctor.PostAsync<MedicationOrderDto>(countersign, new CountersignMedicationOrderRequest(order.Version));
        (signed.AwaitingCountersignature, signed.CountersignedByName, signed.CanCountersign).ShouldBe((false, me.DisplayName, false));
        (await RefusalAsync(doctor, countersign, new CountersignMedicationOrderRequest(signed.Version))).Code.ShouldBe("medication.already_countersigned");
    }

    [Fact]
    public async Task A_doctor_signs_an_order_in_their_own_name_at_once_and_writes_another_doctors_as_a_verbal_order()
    {
        var (_, doctor, admission) = await WardAsync();
        var secondDoctor = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        var (me, second) = (await MeAsync(doctor), await MeAsync(secondDoctor));
        var heparin = await DrugAsync("Heparin", highAlert: true);
        var orders = $"/api/v1/nursing/admissions/{admission.Id}/medication-orders";

        // In his own name (named or not), even a high-alert medicine is signed at once and its dose is on the round.
        var own = await doctor.PostAsync<MedicationOrderDto>(orders, Order(heparin, doctor: null, frequency: "STAT"));
        (own.Mode, own.AwaitingCountersignature, own.OrderedByPractitionerId, own.DosesHeld).ShouldBe((MedicationOrderMode.Written, false, me.PractitionerId, false));
        (await ChartAsync(doctor, admission.Id)).ShouldContain(e => e.MedicationOrderId == own.Id && e.Status == MarStatus.Due);

        // In another doctor's name: a verbal order like a nurse's, which that doctor countersigns and he does not.
        (await RefusalAsync(doctor, orders, Order(heparin, second.PractitionerId))).Code.ShouldBe("medication.verbal_order_required");
        var verbal = await doctor.PostAsync<MedicationOrderDto>(orders, Order(heparin, second.PractitionerId, MedicationOrderMode.Verbal, readBack: true));
        (verbal.Mode, verbal.AwaitingCountersignature, verbal.DosesHeld).ShouldBe((MedicationOrderMode.Verbal, true, true));
        var countersign = $"/api/v1/nursing/medication-orders/{verbal.Id}/countersign";
        (await RefusalAsync(doctor, countersign, new CountersignMedicationOrderRequest(verbal.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "medication.countersign_not_prescriber"));
        (await secondDoctor.PostAsync<MedicationOrderDto>(countersign, new CountersignMedicationOrderRequest(verbal.Version)))
            .AwaitingCountersignature.ShouldBeFalse();
    }

    [Fact]
    public async Task A_high_alert_verbal_order_gives_no_dose_until_countersigned_unless_it_was_taken_as_an_emergency()
    {
        var (nurse, doctor, admission) = await WardAsync();
        var me = await MeAsync(doctor);
        var insulin = await DrugAsync("Insulin regular", highAlert: true);
        var orders = $"/api/v1/nursing/admissions/{admission.Id}/medication-orders";

        var held = await nurse.PostAsync<MedicationOrderDto>(orders, Order(insulin, me.PractitionerId, MedicationOrderMode.Verbal, readBack: true, frequency: "STAT"));
        (held.IsHighAlert, held.DosesHeld, held.IsEmergency).ShouldBe((true, true, false));
        (await ChartAsync(nurse, admission.Id)).ShouldNotContain(e => e.MedicationOrderId == held.Id, "no dose goes on the round");
        (await RefusalAsync(nurse, $"/api/v1/nursing/admissions/{admission.Id}/mar", Extra(held.Id, witness: null)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "medication.awaiting_countersignature"));
        (await RefusalAsync(nurse, $"/api/v1/nursing/admissions/{admission.Id}/mar", Extra(held.Id, witness: null) with { GivenNow = false }))
            .Code.ShouldBe("medication.awaiting_countersignature");

        // The doctor countersigns: the dose goes on the round, and is witnessed when given.
        await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/medication-orders/{held.Id}/countersign", new CountersignMedicationOrderRequest(held.Version));
        var dose = (await ChartAsync(nurse, admission.Id)).Single(e => e.MedicationOrderId == held.Id && e.Status == MarStatus.Due);
        dose.NeedsWitness.ShouldBeTrue();

        // Taken as an emergency order, read back: given at once, each dose witnessed, and flagged until countersigned.
        var recording = await RecordingNurseAsync();
        var emergency = await recording.PostAsync<MedicationOrderDto>(orders,
            Order(insulin, me.PractitionerId, MedicationOrderMode.Verbal, readBack: true, frequency: "STAT", emergency: true));
        (emergency.IsEmergency, emergency.DosesHeld, emergency.AwaitingCountersignature).ShouldBe((true, false, true));
        var now = (await ChartAsync(recording, admission.Id)).Single(e => e.MedicationOrderId == emergency.Id && e.Status == MarStatus.Due);
        (await RefusalAsync(recording, $"/api/v1/nursing/mar/{now.Id}/record", Given(now, null))).Code.ShouldBe("mar.witness_required");
        var given = await recording.PostAsync<MarEntryDto>($"/api/v1/nursing/mar/{now.Id}/record", Given(now, await WitnessAsync(recording, admission.PatientId)));
        given.Status.ShouldBe(MarStatus.Given);
        (await recording.GetAsync<List<MedicationOrderDto>>(orders)).Single(o => o.Id == emergency.Id).AwaitingCountersignature.ShouldBeTrue();
    }

    [Fact]
    public async Task A_medicine_typed_in_is_high_alert_whatever_the_form_says_and_a_dose_with_no_order_is_always_witnessed()
    {
        var (nurse, doctor, admission) = await WardAsync();
        var me = await MeAsync(doctor);
        var orders = $"/api/v1/nursing/admissions/{admission.Id}/medication-orders";

        // A drug the master does not have, or a medicine typed in as neither controlled nor high-alert.
        (await RefusalAsync(doctor, orders, Order(Guid.NewGuid(), doctor: null))).ShouldBe((HttpStatusCode.BadRequest, null));
        var typed = await doctor.PostAsync<MedicationOrderDto>(orders, Typed("Morphine 10 mg/ml Injection", frequency: "STAT"));
        (typed.DrugItemId, typed.IsHighAlert, typed.IsControlledDrug).ShouldBe(((Guid?)null, true, false));
        var dose = (await ChartAsync(doctor, admission.Id)).Single(e => e.MedicationOrderId == typed.Id);
        dose.NeedsWitness.ShouldBeTrue();
        (await RefusalAsync(doctor, $"/api/v1/nursing/mar/{dose.Id}/record", Given(dose, null))).Code.ShouldBe("mar.witness_required");

        // Typed in by a nurse as a verbal order, it waits for the doctor as any high-alert verbal order does.
        var verbal = await nurse.PostAsync<MedicationOrderDto>(orders, Typed("Potassium chloride 15% Injection", frequency: "STAT") with
        {
            OrderedByPractitionerId = me.PractitionerId,
            Mode = MedicationOrderMode.Verbal,
            ReadBack = true,
        });
        (verbal.IsHighAlert, verbal.DosesHeld).ShouldBe((true, true));

        // A dose with no order behind it, given now or recorded later, is witnessed.
        var recording = await RecordingNurseAsync();
        var adHoc = $"/api/v1/nursing/admissions/{admission.Id}/mar";
        (await RefusalAsync(recording, adHoc, NoOrder(givenNow: true, witness: null))).Code.ShouldBe("mar.witness_required");
        var later = await recording.PostAsync<MarEntryDto>(adHoc, NoOrder(givenNow: false, witness: null));
        (later.Status, later.NeedsWitness).ShouldBe((MarStatus.Due, true));
        (await RefusalAsync(recording, $"/api/v1/nursing/mar/{later.Id}/record", Given(later, null))).Code.ShouldBe("mar.witness_required");
        (await recording.PostAsync<MarEntryDto>(adHoc, NoOrder(givenNow: true, witness: await WitnessAsync(recording, admission.PatientId))))
            .Status.ShouldBe(MarStatus.Given);
    }

    [Fact]
    public async Task A_nurses_stop_waits_for_the_doctor_with_the_doses_held_and_a_prescribers_stop_is_made_at_once()
    {
        var (nurse, doctor, admission) = await WardAsync();
        var ondansetron = await DrugAsync("Ondansetron", highAlert: false);
        var orders = $"/api/v1/nursing/admissions/{admission.Id}/medication-orders";
        var stat = await doctor.PostAsync<MedicationOrderDto>(orders, Order(ondansetron, doctor: null, frequency: "STAT"));
        var dose = (await ChartAsync(nurse, admission.Id)).Single(e => e.MedicationOrderId == stat.Id);

        // The nurse's stop: the order goes on, its dose is held, and the doctor is asked.
        var asked = await nurse.PostAsync<MedicationOrderDto>($"/api/v1/nursing/medication-orders/{stat.Id}/stop", new StopMedicationOrderRequest("Vomiting has settled"));
        (asked.Status, asked.StopPending, asked.DosesHeld, asked.StopRequestReason).ShouldBe((MedicationOrderStatus.Active, true, true, "Vomiting has settled"));
        (await RefusalAsync(nurse, $"/api/v1/nursing/mar/{dose.Id}/record", Given(dose, null))).Code.ShouldBe("medication.stop_pending");
        (await ChartAsync(nurse, admission.Id)).Single(e => e.Id == dose.Id).HeldBecause.ShouldBe("medication.stop_pending");
        (await AuditAsync(stat.Id)).ShouldContain(e => e.Action == "medication.stop_requested");

        // Only the doctor named answers it; he declines, and the dose is given after all.
        var secondDoctor = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        (await RefusalAsync(secondDoctor, $"/api/v1/nursing/medication-orders/{stat.Id}/stop/decline", new DeclineStopRequest("Not mine to answer", asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "medication.countersign_not_prescriber"));
        var declined = await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/medication-orders/{stat.Id}/stop/decline",
            new DeclineStopRequest("Still nauseous: give the dose", asked.Version));
        (declined.StopPending, declined.DosesHeld).ShouldBe((false, false));
        (await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/mar/{dose.Id}/record", Given(dose, null))).Status.ShouldBe(MarStatus.Given);

        // A twice-daily order: the nurse asks to stop it, the doctor countersigns, and the doses not given are cancelled.
        var bd = await doctor.PostAsync<MedicationOrderDto>(orders, Order(ondansetron, doctor: null, frequency: "BD"));
        var pending = await nurse.PostAsync<MedicationOrderDto>($"/api/v1/nursing/medication-orders/{bd.Id}/stop", new StopMedicationOrderRequest("Discontinued on the round"));
        var confirmed = await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/medication-orders/{bd.Id}/stop/countersign", new ConfirmStopRequest(pending.Version));
        (confirmed.Status, confirmed.StopReason, confirmed.StopPending).ShouldBe((MedicationOrderStatus.Stopped, "Discontinued on the round", false));
        var doses = await DosesAsync(bd.Id);
        doses.ShouldNotBeEmpty();
        doses.ShouldAllBe(d => d.Status == MarStatus.Cancelled);

        // A prescriber's stop is made at once.
        var another = await doctor.PostAsync<MedicationOrderDto>(orders, Order(ondansetron, doctor: null, frequency: "BD"));
        var stopped = await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/medication-orders/{another.Id}/stop", new StopMedicationOrderRequest("Course changed"));
        (stopped.Status, stopped.StopPending).ShouldBe((MedicationOrderStatus.Stopped, false));
    }

    [Fact]
    public async Task What_is_not_countersigned_in_time_is_listed_for_the_ward_and_the_doctor_who_are_told_once()
    {
        var (nurse, doctor, admission) = await WardAsync();
        var me = await MeAsync(doctor);
        var sister = await WardSisterAsync();
        var drug = await DrugAsync("Pantoprazole", highAlert: false);
        var order = await nurse.PostAsync<MedicationOrderDto>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders",
            Order(drug, me.PractitionerId, MedicationOrderMode.Verbal, readBack: true));

        // Within its time it is listed as waiting, not overdue; past it, it is listed as overdue for the ward and for the doctor.
        var ward = admission.WardId;
        (await nurse.GetAsync<List<AwaitingCountersignatureItem>>($"{Awaiting}?wardId={ward}")).Single(i => i.Order.Id == order.Id).Order.CountersignOverdue.ShouldBeFalse();
        (await nurse.GetAsync<List<AwaitingCountersignatureItem>>($"{Awaiting}?wardId={ward}&overdueOnly=true")).ShouldNotContain(i => i.Order.Id == order.Id);
        await ChangeAsync<MedicationOrder>(o => o.Id == order.Id, o => o.CountersignDueAt = DateTimeOffset.UtcNow.AddMinutes(-5));

        var overdue = (await nurse.GetAsync<List<AwaitingCountersignatureItem>>($"{Awaiting}?wardId={ward}&overdueOnly=true")).Single(i => i.Order.Id == order.Id);
        (overdue.Order.CountersignOverdue, overdue.AdmissionId, overdue.WardId).ShouldBe((true, admission.Id, ward));
        (await doctor.GetAsync<List<AwaitingCountersignatureItem>>($"{Awaiting}?mine=true")).ShouldContain(i => i.Order.Id == order.Id && i.Order.CanCountersign);
        var secondDoctor = await server.SignInAsync(HospitalServer.Users.SecondDoctor);
        (await secondDoctor.GetAsync<List<AwaitingCountersignatureItem>>($"{Awaiting}?mine=true")).ShouldNotContain(i => i.Order.Id == order.Id);
        (await sister.Client.GetAsync<List<AwaitingCountersignatureItem>>($"{Awaiting}?overdueOnly=true")).ShouldContain(i => i.Order.Id == order.Id);

        // The doctor and the ward's nurse in charge are told, once, and the trail records it.
        await NotifyOverdueAsync();
        await NotifyOverdueAsync();
        const string title = "Verbal order not countersigned in time";
        (await NotificationsAsync(me.Id, title)).Count(n => n.Body!.Contains(order.DrugName, StringComparison.Ordinal)).ShouldBe(1);
        (await NotificationsAsync(sister.UserId, title)).Count(n => n.Body!.Contains(order.DrugName, StringComparison.Ordinal)).ShouldBe(1);
        (await AuditAsync(order.Id)).ShouldContain(e => e.Action == "medication.countersign_overdue" && e.Severity == AuditSeverity.Warning);

        // Countersigned late, it leaves the list.
        await doctor.PostAsync<MedicationOrderDto>($"/api/v1/nursing/medication-orders/{order.Id}/countersign",
            new CountersignMedicationOrderRequest((await doctor.GetAsync<List<MedicationOrderDto>>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders"))
                .Single(o => o.Id == order.Id).Version));
        (await nurse.GetAsync<List<AwaitingCountersignatureItem>>($"{Awaiting}?wardId={ward}")).ShouldNotContain(i => i.Order.Id == order.Id);
    }

    [Fact]
    public async Task The_time_to_countersign_is_the_hospitals_setting()
    {
        var (nurse, doctor, admission) = await WardAsync();
        var me = await MeAsync(doctor);
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var drug = await DrugAsync("Metoclopramide", highAlert: false);

        // Fewer hours are stricter, and saved at once.
        await server.SaveSettingAsync(admin, MarService.CountersignHoursSetting, new SaveSettingRequest(JsonValue.Create(4), null, false, null));
        try
        {
            var order = await nurse.PostAsync<MedicationOrderDto>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders",
                Order(drug, me.PractitionerId, MedicationOrderMode.Verbal, readBack: true));
            (order.CountersignDueAt!.Value - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromHours(3.9), TimeSpan.FromHours(4));
        }
        finally
        {
            await using var scope = server.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISettingsService>().RemoveAsync(MarService.CountersignHoursSetting, null, CancellationToken.None);
        }
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(ApiClient Nurse, ApiClient Doctor, Admitted Admission)> WardAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var consultantId = (await MeAsync(doctor)).PractitionerId;
        var bed = await TestBeds.AddAsync(server, "SPW");
        var admitted = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Acute gastroenteritis", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "SPW");
        return (nurse, doctor, new Admitted(admitted.Id, patient.Id, ward.Id));
    }

    /// <summary>A drug of the master, neither controlled nor (unless asked) high-alert, with a name of its own.</summary>
    private async Task<Guid> DrugAsync(string name, bool highAlert) =>
        await DrugClassApprovalTests.CreateAsync(await server.SignInAsync(HospitalServer.Users.HospitalAdmin), name, DrugSchedule.H, controlled: false, highAlert);

    private static AddMedicationOrderRequest Order(Guid drugItemId, Guid? doctor, MedicationOrderMode? mode = null, bool readBack = false, string frequency = "TDS",
        bool emergency = false) => new(
        DrugItemId: drugItemId, DrugName: $"Medicine {Letters(6)}", GenericName: null, Strength: null, Dose: "4 mg", Route: "IV", Frequency: frequency,
        DurationDays: 2, StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: doctor, IsHighAlert: false, Mode: mode,
        ReadBack: readBack, Emergency: emergency);

    /// <summary>A medicine typed in, as neither controlled nor high-alert.</summary>
    private static AddMedicationOrderRequest Typed(string name, string frequency) => new(
        DrugItemId: null, DrugName: $"{name} {Letters(4)}", GenericName: null, Strength: null, Dose: "1 ampoule", Route: "IV", Frequency: frequency,
        DurationDays: 1, StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: null, IsHighAlert: false);

    private static AddAdHocDoseRequest Extra(Guid orderId, Guid? witness) => new(
        MedicationOrderId: orderId, DrugName: null, Dose: "4 IU", Route: "SC", ScheduledAt: null, GivenNow: true, Reason: "RBS 320 mg/dL", BatchNumber: null,
        WitnessConfirmationId: witness, Notes: null);

    private static AddAdHocDoseRequest NoOrder(bool givenNow, Guid? witness) => new(
        MedicationOrderId: null, DrugName: "Paracetamol 650 mg Tablet", Dose: "650 mg", Route: "Oral", ScheduledAt: null, GivenNow: givenNow, Reason: "Fever 101 °F",
        BatchNumber: null, WitnessConfirmationId: witness, Notes: null);

    private static RecordAdministrationRequest Given(MarEntryDto dose, Guid? witness) =>
        new(MarStatus.Given, AdministeredAt: null, Reason: null, BatchNumber: null, WitnessConfirmationId: witness, Notes: null, dose.Version);

    /// <summary>
    /// The nurse who records a witnessed dose, from the test's own address: the witness's password counts against the address
    /// it is typed at, as a sign-in does. The second nurse's account is made on its first sign-in, so it is signed in first.
    /// </summary>
    private async Task<ApiClient> RecordingNurseAsync()
    {
        await server.SignInAsync(HospitalServer.Users.SecondNurse);
        return server.FromOwnAddress(await server.SignInAsync(HospitalServer.Users.Nurse));
    }

    /// <summary>The second nurse's confirmation, at the recording nurse's screen, for this patient's dose.</summary>
    private async Task<Guid> WitnessAsync(ApiClient recording, Guid patientId) =>
        (await recording.PostAsync<WitnessConfirmed>(Witness,
            new { userName = HospitalServer.Users.SecondNurse, password = server.Password, code = (string?)null, purpose = "mar.dose", patientId })).ConfirmationId;

    private static async Task<List<MarEntryDto>> ChartAsync(ApiClient api, Guid admissionId) =>
        await api.GetAsync<List<MarEntryDto>>($"/api/v1/nursing/admissions/{admissionId}/mar");

    private static async Task<(Guid Id, Guid PractitionerId, string DisplayName)> MeAsync(ApiClient api)
    {
        var me = await api.GetAsync<JsonElement>("/api/v1/auth/me");
        return (me.GetProperty("id").GetGuid(), me.GetProperty("practitionerId").GetGuid(), me.GetProperty("displayName").GetString()!);
    }

    /// <summary>
    /// A ward sister: a nurse whose account also holds a role with <c>nursing.in_charge</c>, which no built-in role has and a
    /// hospital gives to its nurses in charge. Made on first use.
    /// </summary>
    private async Task<(ApiClient Client, Guid UserId)> WardSisterAsync()
    {
        const string name = "it-ward-sister";
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            if (!await db.Set<User>().IgnoreQueryFilters().AnyAsync(u => u.UserName == name, Ct))
            {
                await server.CreateStaffAccountAsync(name, SystemRoles.Nurse);
                var userId = await db.Set<User>().IgnoreQueryFilters().Where(u => u.UserName == name).Select(u => u.Id).SingleAsync(Ct);
                var role = new Role { Name = "Ward sister (tests)", Description = "Nurse in charge of a ward" };
                role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = InpatientPermissions.NursingInCharge });
                db.Add(role);
                db.Add(new UserRole { UserId = userId, RoleId = role.Id });
                await db.SaveChangesAsync(Ct);
            }
        }

        var (client, _) = await server.SignInNewSessionAsync(name);
        return (client, (await client.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid());
    }

    private async Task NotifyOverdueAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<MarService>().NotifyOverdueCountersignaturesAsync(Ct);
    }

    private async Task<List<UserNotification>> NotificationsAsync(Guid userId, string title)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<UserNotification>().AsNoTracking()
            .Where(n => n.UserId == userId && n.Title == title).ToListAsync(Ct);
    }

    private async Task<List<AuditEvent>> AuditAsync(Guid orderId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var id = orderId.ToString();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(e => e.EntityType == nameof(MedicationOrder) && e.EntityId == id).ToListAsync(Ct);
    }

    private async Task<List<MedicationAdministration>> DosesAsync(Guid orderId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<MedicationAdministration>()
            .IgnoreQueryFilters([HospitalDbContext.FacilityFilter]).AsNoTracking()
            .Where(d => d.MedicationOrderId == orderId).ToListAsync(Ct);
    }

    private async Task ChangeAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> which, Action<T> change) where T : class
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        change(await db.Set<T>().IgnoreQueryFilters().SingleAsync(which, Ct));
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        response.IsSuccessStatusCode.ShouldBeFalse(await response.Content.ReadAsStringAsync(Ct));
        var text = await response.Content.ReadAsStringAsync(Ct);
        var code = text.Length > 0 && JsonSerializer.Deserialize<JsonElement>(text, ApiClient.Json) is { ValueKind: JsonValueKind.Object } problem
                   && problem.TryGetProperty("code", out var found) && response.StatusCode != HttpStatusCode.BadRequest
            ? found.GetString()
            : null;
        return (response.StatusCode, code);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed record Admitted(Guid Id, Guid PatientId, Guid WardId);
}
