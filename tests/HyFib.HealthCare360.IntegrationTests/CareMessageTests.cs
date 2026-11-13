using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.DischargeSummaries;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static HyFib.HealthCare360.IntegrationTests.DischargeStays;
using PaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the front desk, the wards and the billing desk tell a patient about their own care (queue token, admission,
/// discharge, payment receipt), read from the outbox the real API fills, over PostgreSQL: by SMS, and on WhatsApp once the
/// hospital switches each WhatsApp template on (they are seeded "Not in use" until Meta approves them, so the server's
/// own outbox skips them); in the patient's language; never on a number another patient shares when the record is in a
/// protected category. The discharge's WhatsApp message is text only until the hospital switches on the
/// <c>discharge.summary</c> template, which carries the signed summary (<see cref="DischargeSummaryWhatsAppTests"/>).
/// </summary>
public sealed class CareMessageTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_queue_token_goes_by_sms_and_on_whatsapp_in_the_patients_language_and_waits_on_whatsapp_until_its_template_is_switched_on()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var (patientId, phone) = await RegisterAsync(frontOffice, "Tamil");
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var department = await GeneralMedicineAsync(frontOffice);

        var visit = await OpVisitAsync(frontOffice, doctor, patientId);

        var token = visit.Token.ShouldNotBeNull();
        var sent = await DeliveriesAsync("visit.token", phone, since);
        sent.Select(d => d.Channel).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
        sent.ShouldAllBe(d => d.Language == "ta" && d.PatientIdIs(patientId));
        sent.ShouldAllBe(d => d.Values["token"] == token.Label && d.Values["department"] == department.Name);

        // Seeded "Not in use" until Meta approves the WhatsApp template: the server's own outbox skips it, and says why.
        var whatsApp = await LogEventuallyAsync(sent.Single(d => d.Channel == NotificationChannel.WhatsApp).LogId, NotificationStatus.Skipped);
        whatsApp.Error.ShouldBe(TemplateSwitch.NotInUseReason(NotificationChannel.WhatsApp));
    }

    [Fact]
    public async Task A_protected_record_gets_no_token_message_on_a_number_another_patient_shares()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var (patientId, phone) = await RegisterAsync(frontOffice, null);
        await RegisterAsync(frontOffice, null, phone);
        await PutInCategoryAsync(patientId, SensitiveCategory.Mtp);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        var visit = await OpVisitAsync(frontOffice, doctor, patientId);

        visit.Token.ShouldNotBeNull("The desk still hands the patient a printed token.");
        (await DeliveriesAsync("visit.token", phone, since)).ShouldBeEmpty("The token names the department, and the number is shared.");
    }

    [Fact]
    public async Task Admission_and_discharge_go_by_sms_and_on_whatsapp_and_the_discharge_summary_template_stays_off_until_the_hospital_switches_it_on()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var nurse = await server.SignInAsync(Users.Nurse);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var (patient, stay) = await AdmitAsync(server, frontOffice, doctor);
        var phone = await PhoneAsync(patient.Id);

        var admitted = await DeliveriesAsync("admission.admitted", phone, since);
        admitted.Select(d => d.Channel).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
        admitted.ShouldAllBe(d => d.Values["bed"] == stay.BedLabel && d.Language == "en");

        var draft = await doctor.PutAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/by-admission/{stay.Id}", Summary(patient.Id));
        var signed = await doctor.PostAsync<DischargeSummaryDto>($"/api/v1/clinical/discharge-summaries/{draft.Id}/sign", new SignDischargeSummaryRequest(draft.Version));
        signed.Status.ShouldBe(DischargeSummaryStatus.Signed);
        await InitiateAndClearAsync(server, nurse, stay.Id);
        (await nurse.PostAsync<DischargeResult>(Complete(stay.Id), new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home", NotifyPatient: true)))
            .Status.ShouldBe(AdmissionStatus.Discharged);

        var discharged = await DeliveriesAsync("discharge.completed", phone, since);
        discharged.Select(d => d.Channel).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
        discharged.ShouldAllBe(d => d.Delivery.Document == null,
            "the signed summary goes only in the discharge.summary template, which is seeded not in use");
        (await DeliveriesAsync("discharge.summary", phone, since)).ShouldBeEmpty("its WhatsApp template is not in use, so the discharge keeps the text-only message");
        await CleanBedAsync(nurse, stay.Id);
    }

    [Fact]
    public async Task A_receipt_goes_on_whatsapp_too_except_with_the_payment_that_settles_the_bill_which_sends_the_bill_itself()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var (patientId, phone) = await RegisterAsync(frontOffice, "हिन्दी");
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Digits(8)}", "Dressing (receipt messages)", null, ServiceCategory.Procedure, null, 450m, 0m, null, true, false, null, true, null));
        var billing = await server.SignInAsync(Users.Billing);
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patientId, null));
        draft = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/lines", new AddBillLineRequest(service.Id, 1m, null, null, null, null, draft.Version));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        await OpenShiftAsync(billing);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        // Part of the bill: the receipt goes by SMS and on WhatsApp, in Hindi.
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Cash, 200m, null, null, null, null, null, null)], Notes: null));
        var part = await DeliveriesAsync("bill.receipt", phone, since);
        part.Select(d => d.Channel).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
        part.ShouldAllBe(d => d.Language == "hi");

        // The rest settles it: the bill itself goes on WhatsApp, so the receipt goes by SMS alone.
        var settledFrom = DateTimeOffset.UtcNow.AddSeconds(-1);
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Cash, bill.DueAmount - 200m, null, null, null, null, null, null)], Notes: null));
        (await DeliveriesAsync("bill.receipt", phone, settledFrom)).Select(d => d.Channel).ShouldBe([NotificationChannel.Sms]);
        (await DeliveriesAsync(BillSharing.Template, phone, settledFrom)).ShouldHaveSingleItem().Channel.ShouldBe(NotificationChannel.WhatsApp);
    }

    [Fact]
    public async Task The_whatsapp_templates_added_for_care_messages_are_stored_not_in_use_until_the_hospital_switches_them_on()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationTemplate>().AsNoTracking()
            .Where(t => t.FacilityId == null && t.Language == "en" && t.Channel == NotificationChannel.WhatsApp)
            .Select(t => new { t.Key, t.IsActive })
            .ToListAsync(Ct);

        foreach (var key in new[] { "visit.token", "bill.receipt", "admission.admitted", "discharge.completed", "feedback.request", "followup.reminder", "complaint.acknowledged" })
        {
            stored.ShouldContain(t => t.Key == key && !t.IsActive, $"The WhatsApp template '{key}' is stored, and not in use until Meta approves it.");
        }

        stored.ShouldContain(t => t.Key == "appointment.confirmed" && t.IsActive, "WhatsApp templates the hospital already had stay in use.");
    }

    private sealed record Sent(NotificationDelivery Delivery, Guid? PatientId)
    {
        public NotificationChannel Channel => Delivery.Channel;

        public string Language => Delivery.Language;

        public IReadOnlyDictionary<string, string> Values => Delivery.Values;

        public Guid LogId => Delivery.LogId;

        public bool PatientIdIs(Guid id) => PatientId == id;
    }

    /// <summary>The messages of <paramref name="key"/> queued to the number since then, on every channel.</summary>
    private async Task<List<Sent>> DeliveriesAsync(string key, string phone, DateTimeOffset since)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var messages = await db.Set<OutboxMessage>().AsNoTracking()
            .Where(m => (m.Type == NotificationService.SmsType || m.Type == NotificationService.WhatsAppType) && m.CreatedAt >= since)
            .ToListAsync(Ct);
        var deliveries = messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == key && d.To == phone).ToList();
        var logIds = deliveries.Select(d => d.LogId).ToList();
        var patients = await db.Set<NotificationLog>().AsNoTracking().Where(l => logIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.PatientId, Ct);
        return deliveries.Select(d => new Sent(d, patients.GetValueOrDefault(d.LogId))).ToList();
    }

    private async Task<NotificationLog> LogEventuallyAsync(Guid logId, NotificationStatus status)
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(1); waited += TimeSpan.FromSeconds(1))
        {
            await using (var scope = server.Services.CreateAsyncScope())
            {
                var log = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking().SingleAsync(l => l.Id == logId, Ct);
                if (log.Status == status)
                {
                    return log;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        throw new ShouldAssertException($"The message was not marked {status} by the server's outbox.");
    }

    private async Task<string> PhoneAsync(Guid patientId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Patient>().AsNoTracking()
            .Where(p => p.Id == patientId).Select(p => p.Phone!).SingleAsync(Ct);
    }

    private async Task PutInCategoryAsync(Guid patientId, SensitiveCategory category)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var patient = await db.Set<Patient>().SingleAsync(p => p.Id == patientId, Ct);
        patient.SensitiveCategories |= category;
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The front office checks the patient in to the doctor's general medicine OP, with a token and its message.</summary>
    private static async Task<VisitDetail> OpVisitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var department = await GeneralMedicineAsync(frontOffice);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        return await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, department.Id, practitionerId, null, PayerType.Self, null,
            ReferralSource.Self, null, "Fever for two days", false, null, null, TokenPriority.Normal, IssueToken: true, NotifyPatient: true));
    }

    private static async Task<LookupItem> GeneralMedicineAsync(ApiClient frontOffice) =>
        (await frontOffice.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments")).Single(d => d.Code == "GM");

    private static async Task OpenShiftAsync(ApiClient billing)
    {
        using var shift = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (shift.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    /// <summary>A new patient preferring <paramref name="language"/>, on a new number or on <paramref name="phone"/>; the id and the number (E.164).</summary>
    private static async Task<(Guid PatientId, string Phone)> RegisterAsync(ApiClient frontOffice, string? language, string? phone = null)
    {
        var mobile = phone ?? "9" + Digits(9);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Selvi", MiddleName: null, LastName: "Care" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1970, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: mobile, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: language,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (created.Id, Phone.Normalize(mobile)!);
    }

    private static SaveDischargeSummaryRequest Summary(Guid patientId) => new(
        patientId, null, null, null, null, "Community-acquired pneumonia", [], "Fever and cough", "Treated with IV antibiotics; afebrile for 48 hours", null, null,
        "Stable, ambulant", "Amoxicillin-clavulanate 625 mg BD x 5 days", "Plenty of fluids", null, null, null);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
}
