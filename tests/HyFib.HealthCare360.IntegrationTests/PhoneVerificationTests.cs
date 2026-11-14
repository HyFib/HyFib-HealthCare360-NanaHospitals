using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Diagnostics.Documents;
using HyFib.HealthCare360.Modules.Diagnostics.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A mobile number the desk typed one digit wrong, or one the telco gave to someone new, reaches a stranger (ASVS V6.6.1).
/// Over the real API: the portal keeps a record from whoever signs in with its number until the number is verified on it
/// (a code at the desk, a number change with a code, or the patient's date of birth or UHID in the portal, five wrong tries
/// a day); and messages to a number nobody has verified go without their documents, the e-mail keeping its PDF.
/// </summary>
public sealed class PhoneVerificationTests(HospitalServer server)
{
    private const string Profiles = "/api/v1/portal/profiles";
    private const string ToConfirm = "/api/v1/portal/records-to-confirm";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Signed_in_with_an_unverified_number_the_portal_shows_no_record_until_its_date_of_birth_is_confirmed()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = NewMobile();
        var born = new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000));
        var patient = await RegisterAsync(frontOffice, "Selvi", phone, born);

        var (portal, refreshCookie) = await server.SignInToPortalWithCookieAsync(phone, numberVerified: false);

        (await portal.GetAsync<List<PatientSummary>>(Profiles)).ShouldBeEmpty("nobody has shown that the number is the patient's");
        (await StatusAsync(portal, HealthRecords(patient.Id))).ShouldBe(HttpStatusCode.Forbidden);
        (await PortalAccountAsync(refreshCookie)).DisplayName.ShouldBe(Phone.Mask(phone), "an unverified record gives the account no name");
        var waiting = await portal.GetAsync<RecordsToConfirmDto>(ToConfirm);
        (waiting.Unconfirmed, waiting.AttemptsLeft, waiting.RetryAfterSeconds).ShouldBe((1, PortalRecordConfirmation.MaxFailures, null));

        // A wrong date of birth counts against the number.
        var wrong = await RefusedAsync(portal, new ConfirmRecordsRequest(born.AddDays(1), null));
        wrong.GetProperty("code").GetString().ShouldBe(PortalRecordConfirmation.NoMatchCode);
        wrong.GetProperty("data").GetProperty("attemptsLeft").GetInt32().ShouldBe(PortalRecordConfirmation.MaxFailures - 1);
        (await portal.GetAsync<List<PatientSummary>>(Profiles)).ShouldBeEmpty();

        // The right one shows the record from now on, and verifies the number on it.
        var confirmed = await portal.PostAsync<RecordsConfirmedDto>(ToConfirm, new ConfirmRecordsRequest(born, null));
        (confirmed.Confirmed, confirmed.Unconfirmed).ShouldBe((1, 0));
        (await portal.GetAsync<List<PatientSummary>>(Profiles)).Select(p => p.Id).ShouldBe([patient.Id]);
        (await StatusAsync(portal, HealthRecords(patient.Id))).ShouldBe(HttpStatusCode.OK);
        (await frontOffice.GetAsync<PatientDetail>(PatientPath(patient.Id))).PhoneVerifiedAt.ShouldNotBeNull();
        (await portal.GetAsync<RecordsToConfirmDto>(ToConfirm)).Unconfirmed.ShouldBe(0);

        // Both tries are in the audit trail, and neither holds what was typed.
        var verified = (await AuditAsync(e => e.PatientId == patient.Id && e.Action == PatientPhoneVerificationService.VerifiedAction)).ShouldHaveSingleItem();
        verified.Summary.ShouldNotBeNull().ShouldContain("patient portal");
        var failed = (await AuditAsync(e => e.Action == PortalRecordConfirmationService.FailedAction && e.Summary!.Contains(Phone.Mask(phone))))
            .ShouldHaveSingleItem();
        failed.Severity.ShouldBe(AuditSeverity.Warning);
        $"{failed.Summary} {failed.Details} {verified.Summary} {verified.Details}".ShouldNotContain(born.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The account a portal session signed in, as the portal reads it: from renewing the session on the portal's own address,
    /// which serves no /api/v1/auth/me (ListenerPaths).
    /// </summary>
    private async Task<AuthUser> PortalAccountAsync(string refreshCookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        request.Headers.Add("Cookie", refreshCookie);
        using var renewed = await server.AnonymousOnPortal(ownAddress: true).Http.SendAsync(request, Ct);
        return (await ApiClient.ReadAsync<AuthResult>(renewed, HttpMethod.Post, "/api/v1/auth/refresh")).User.ShouldNotBeNull();
    }

    [Fact]
    public async Task Five_wrong_answers_in_a_day_stop_the_number_even_from_giving_the_right_one()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = NewMobile();
        var born = new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000));
        var patient = await RegisterAsync(frontOffice, "Muthu", phone, born);
        var portal = await server.SignInToPortalAsync(phone, numberVerified: false);

        for (var left = PortalRecordConfirmation.MaxFailures - 1; left > 0; left--)
        {
            var wrong = await RefusedAsync(portal, new ConfirmRecordsRequest(null, "NH" + Digits(8)));
            (wrong.GetProperty("code").GetString(), wrong.GetProperty("data").GetProperty("attemptsLeft").GetInt32())
                .ShouldBe((PortalRecordConfirmation.NoMatchCode, left));
        }

        var fifth = await RefusedAsync(portal, new ConfirmRecordsRequest(born.AddYears(-1), null));
        fifth.GetProperty("code").GetString().ShouldBe(PortalRecordConfirmation.ThrottledCode);
        fifth.GetProperty("data").GetProperty("retryAfterSeconds").GetInt32().ShouldBeGreaterThan(0);

        var right = await RefusedAsync(portal, new ConfirmRecordsRequest(born, null));
        right.GetProperty("code").GetString().ShouldBe(PortalRecordConfirmation.ThrottledCode, "a guess is not judged once the day's tries are used");
        var status = await portal.GetAsync<RecordsToConfirmDto>(ToConfirm);
        status.AttemptsLeft.ShouldBe(0);
        status.RetryAfterSeconds.ShouldNotBeNull().ShouldBeGreaterThan(0);
        (await portal.GetAsync<List<PatientSummary>>(Profiles)).ShouldBeEmpty();
        (await frontOffice.GetAsync<PatientDetail>(PatientPath(patient.Id))).PhoneVerifiedAt.ShouldBeNull();
        (await AuditAsync(e => e.Action == PortalRecordConfirmationService.ThrottledAction && e.Summary!.Contains(Phone.Mask(phone)))).ShouldHaveSingleItem();

        // The front desk can still verify the number: the code the patient reads out is not a guess.
        await VerifyAtDeskAsync(frontOffice, patient.Id, phone);
        (await portal.GetAsync<List<PatientSummary>>(Profiles)).Select(p => p.Id).ShouldBe([patient.Id]);
    }

    [Fact]
    public async Task Tries_sent_together_stay_within_the_limit_for_a_number()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = NewMobile();
        await RegisterAsync(frontOffice, "Kannan", phone, new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000)));
        var portal = await server.SignInToPortalAsync(phone, numberVerified: false);

        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var response = await portal.SendRawAsync(HttpMethod.Post, ToConfirm, new ConfirmRecordsRequest(null, "NH" + Digits(8)));
            return response.StatusCode;
        }));

        answers.ShouldAllBe(status => status == HttpStatusCode.UnprocessableEntity);
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PortalRecordConfirmation>().AsNoTracking()
                .CountAsync(c => c.AccountPhone == phone && c.Confirmed == 0, Ct))
            .ShouldBe(PortalRecordConfirmation.MaxFailures, "each try is counted before the next is judged");
    }

    [Fact]
    public async Task The_desk_verifies_a_number_with_a_code_the_patient_reads_out_and_a_change_of_number_verifies_the_new_one()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = NewMobile();
        var patient = await RegisterAsync(frontOffice, "Ponni", phone, new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000)));
        (await frontOffice.GetAsync<PatientDetail>(PatientPath(patient.Id))).PhoneVerifiedAt.ShouldBeNull("registering is quick: verifying is a step of its own");
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var sent = await frontOffice.PostAsync<PhoneVerificationDto>(VerifyPath(patient.Id));
        (sent.MaskedPhone, sent.AttemptsLeft).ShouldBe((Phone.Mask(phone), PatientPhoneChange.MaxAttempts));

        // The code goes to the number on the record, by SMS and on WhatsApp, without the patient's name.
        var messages = await DeliveriesAsync(PatientPhoneVerificationService.Template, phone, before);
        messages.Select(m => m.Channel).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
        messages.ShouldAllBe(m => m.Name == null);
        var code = messages.First().Values["code"];

        using (var wrong = await frontOffice.SendRawAsync(HttpMethod.Post, $"{VerifyPath(patient.Id)}/{sent.VerificationId}/confirm",
                   new ConfirmPhoneVerificationRequest(code == "000000" ? "111111" : "000000")))
        {
            wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        var verified = await frontOffice.PostAsync<PhoneVerified>($"{VerifyPath(patient.Id)}/{sent.VerificationId}/confirm", new ConfirmPhoneVerificationRequest(code));
        verified.MaskedPhone.ShouldBe(Phone.Mask(phone));
        var detail = await frontOffice.GetAsync<PatientDetail>(PatientPath(patient.Id));
        detail.PhoneVerifiedAt.ShouldNotBeNull();
        using (var again = await frontOffice.SendRawAsync(HttpMethod.Post, VerifyPath(patient.Id)))
        {
            again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await again.Content.ReadAsStringAsync(Ct)).ShouldContain(PatientPhoneVerificationService.AlreadyVerifiedCode);
        }

        // The portal shows the record to the number at once, with nothing to confirm.
        var portal = await server.SignInToPortalAsync(phone, numberVerified: false);
        (await portal.GetAsync<List<PatientSummary>>(Profiles)).Select(p => p.Id).ShouldBe([patient.Id]);
        (await portal.GetAsync<RecordsToConfirmDto>(ToConfirm)).Unconfirmed.ShouldBe(0);
        (await AuditAsync(e => e.PatientId == patient.Id && e.Action == PatientPhoneVerificationService.CodeSentAction)).ShouldHaveSingleItem();
        var audited = (await AuditAsync(e => e.PatientId == patient.Id && e.Action == PatientPhoneVerificationService.VerifiedAction)).ShouldHaveSingleItem();
        $"{audited.Summary} {audited.Details}".ShouldNotContain(code);

        // A new number given with a code sent to it is verified with the change.
        var newPhone = Phone.Normalize(NewMobile())!;
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var change = await frontOffice.PostAsync<PhoneChangeDto>($"/api/v1/patients/{patient.Id}/phone-change", new RequestPhoneChangeRequest(newPhone));
        var changeCode = (await DeliveriesAsync(PatientPhoneChangeService.Template, newPhone, since)).ShouldHaveSingleItem().Values["code"];
        await frontOffice.PostAsync<PhoneChangeConfirmed>($"/api/v1/patients/{patient.Id}/phone-change/{change.ChangeId}/confirm", new ConfirmPhoneChangeRequest(changeCode));
        var changed = await frontOffice.GetAsync<PatientDetail>(PatientPath(patient.Id));
        changed.Phone.ShouldBe(newPhone);
        changed.PhoneVerifiedAt.ShouldNotBeNull().ShouldBeGreaterThan(detail.PhoneVerifiedAt!.Value);
    }

    [Fact]
    public async Task A_report_goes_to_an_unverified_number_without_its_pdf_while_the_email_keeps_it()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = NewMobile();
        var email = $"report{Digits(6)}@example.org";
        var patient = await RegisterAsync(frontOffice, "Revathi", phone, new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000)), email);
        var report = new NotificationDocument(LabReportDocumentSource.DocumentKind, Guid.NewGuid());

        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        await ReportReadyAsync(patient.Id, report);
        var unverified = await DeliveriesAsync(ReportDelivery.ReportReadyTemplate, phone, since, email);
        unverified.Select(d => d.Channel).ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp, NotificationChannel.Email], ignoreOrder: true);
        unverified.Single(d => d.Channel == NotificationChannel.WhatsApp).Document.ShouldBeNull("the number may not be the patient's");
        unverified.Single(d => d.Channel == NotificationChannel.Sms).Document.ShouldBeNull();
        unverified.Single(d => d.Channel == NotificationChannel.Email).Document.ShouldBe(report, "the e-mail is to the patient's own address");

        await VerifyAtDeskAsync(frontOffice, patient.Id, phone);
        since = DateTimeOffset.UtcNow.AddSeconds(-1);
        await ReportReadyAsync(patient.Id, report);
        var verified = await DeliveriesAsync(ReportDelivery.ReportReadyTemplate, phone, since, email, except: unverified);
        verified.Single(d => d.Channel == NotificationChannel.WhatsApp).Document.ShouldBe(report);
        verified.Single(d => d.Channel == NotificationChannel.Email).Document.ShouldBe(report);
    }

    [Fact]
    public async Task A_prescription_goes_to_an_unverified_number_without_its_pdf()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = NewMobile();
        var patient = await RegisterAsync(frontOffice, "Chitra", phone, new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000)));

        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        await PrescribeAsync(patient.Id);
        var unverified = (await DeliveriesAsync("prescription.shared", phone, since)).ShouldHaveSingleItem();
        (unverified.Channel, unverified.Document).ShouldBe((NotificationChannel.WhatsApp, null));

        await VerifyAtDeskAsync(frontOffice, patient.Id, phone);
        since = DateTimeOffset.UtcNow.AddSeconds(-1);
        await PrescribeAsync(patient.Id);
        (await DeliveriesAsync("prescription.shared", phone, since, except: [unverified])).ShouldHaveSingleItem().Document.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_paid_bill_is_not_sent_to_an_unverified_number_the_receipt_goes_there_instead_and_the_desk_is_told_why()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = NewMobile();
        var patient = await RegisterAsync(frontOffice, "Lalitha", phone, new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000)));
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Digits(8)}", "Dressing (unverified number)", null, ServiceCategory.Procedure, null, 450m, 0m, null, true, false, null, true, null));
        var billing = await server.SignInAsync(Users.Billing);
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patient.Id, null));
        draft = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/lines", new AddBillLineRequest(service.Id, 1m, null, null, null, null, draft.Version));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        using (var shift = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current"))
        {
            if (shift.StatusCode == HttpStatusCode.NoContent)
            {
                await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
            }
        }

        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Cash, bill.DueAmount, null, null, null, null, null, null)], Notes: null));

        (await DeliveriesAsync(BillSharing.Template, phone, since)).ShouldBeEmpty("the bill's PDF would go to whoever holds the number");
        (await DeliveriesAsync("bill.receipt", phone, since)).Select(d => d.Channel)
            .ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);
        using var refused = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/send-whatsapp");
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain(BillSharing.NumberUnverifiedCode);

        await VerifyAtDeskAsync(frontOffice, patient.Id, phone);
        await billing.PostAsync($"/api/v1/billing/bills/{bill.Id}/send-whatsapp");
        (await DeliveriesAsync(BillSharing.Template, phone, since)).ShouldHaveSingleItem().Document
            .ShouldBe(new NotificationDocument("billing.bill", bill.Id));
    }

    [Fact]
    public async Task Only_staff_who_edit_patients_verify_their_number()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var phone = NewMobile();
        var patient = await RegisterAsync(frontOffice, "Amudha", phone, new DateOnly(1950, 1, 1).AddDays(Random.Shared.Next(18000)));
        var sent = await frontOffice.PostAsync<PhoneVerificationDto>(VerifyPath(patient.Id));

        var doctor = await server.SignInAsync(Users.Doctor);
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, VerifyPath(patient.Id), null),
                     (HttpMethod.Post, VerifyPath(patient.Id), null),
                     (HttpMethod.Post, $"{VerifyPath(patient.Id)}/{sent.VerificationId}/confirm", new ConfirmPhoneVerificationRequest("123456")),
                 })
        {
            using var refused = await doctor.SendRawAsync(method, path, body);
            refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, $"{method} {path}");
        }

        (await frontOffice.GetAsync<PhoneVerificationDto>(VerifyPath(patient.Id))).AttemptsLeft.ShouldBe(PatientPhoneChange.MaxAttempts, "the doctor's try was not judged");
    }

    private static string PatientPath(Guid patientId) => $"/api/v1/patients/{patientId}";

    private static string VerifyPath(Guid patientId) => $"/api/v1/patients/{patientId}/phone-verification";

    private static string HealthRecords(Guid patientId) => $"/api/v1/portal/health-records?patientId={patientId}";

    /// <summary>The desk verifies the patient's number: a code to it, read from the outbox, entered.</summary>
    private async Task VerifyAtDeskAsync(ApiClient staff, Guid patientId, string phone)
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var sent = await staff.PostAsync<PhoneVerificationDto>(VerifyPath(patientId));
        var code = (await DeliveriesAsync(PatientPhoneVerificationService.Template, phone, before)).First(d => d.Channel == NotificationChannel.Sms).Values["code"];
        await staff.PostAsync<PhoneVerified>($"{VerifyPath(patientId)}/{sent.VerificationId}/confirm", new ConfirmPhoneVerificationRequest(code));
    }

    private async Task ReportReadyAsync(Guid patientId, NotificationDocument report)
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ReportDelivery>().QueueReportReadyAsync(patientId, "LAB-" + Digits(6), report, null, Ct);
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().SaveChangesAsync(Ct);
    }

    private async Task PrescribeAsync(Guid patientId)
    {
        const string prescriptions = "/api/v1/clinical/prescriptions";
        var doctor = await server.SignInAsync(Users.Doctor);
        var draft = await doctor.PostAsync<PrescriptionDto>(prescriptions, new CreatePrescriptionRequest(
            patientId, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [new PrescriptionItemRequest(null, "Pan 40", "Pantoprazole", "40 mg", "Tablet", Route: "Oral", Dose: "1 tablet", DoseFrequency.OD,
                CustomFrequency: null, DosesPerDay: null, DurationDays: 5, Quantity: null, Instructions: "Before food", IsControlledDrug: false,
                ControlledSchedule: null)]));
        await doctor.PostAsync<SignPrescriptionResult>($"{prescriptions}/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version));
    }

    private static async Task<JsonElement> RefusedAsync(ApiClient portal, ConfirmRecordsRequest request)
    {
        using var response = await portal.SendRawAsync(HttpMethod.Post, ToConfirm, request);
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpStatusCode> StatusAsync(ApiClient client, string path)
    {
        using var response = await client.SendRawAsync(HttpMethod.Get, path);
        return response.StatusCode;
    }

    /// <summary>The messages of a template queued since <paramref name="since"/> to the number (or the e-mail address): the test server sends none.</summary>
    /// <summary>
    /// The deliveries of <paramref name="template"/> to the patient's number or e-mail made since <paramref name="since"/>,
    /// leaving out <paramref name="except"/>: a second round made within a second of the first finds the first's too, as
    /// <paramref name="since"/> allows a second for the clocks.
    /// </summary>
    private async Task<List<NotificationDelivery>> DeliveriesAsync(
        string template, string phone, DateTimeOffset since, string? email = null, IEnumerable<NotificationDelivery>? except = null)
    {
        var earlier = (except ?? []).Select(d => d.LogId).ToHashSet();
        await using var scope = server.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => (m.Type == NotificationService.SmsType || m.Type == NotificationService.WhatsAppType || m.Type == NotificationService.EmailType)
                        && m.CreatedAt >= since)
            .ToListAsync(Ct);
        var number = Phone.Normalize(phone);
        return [.. messages.Select(m => m.Read<NotificationDelivery>())
            .Where(d => d.TemplateKey == template && (d.To == number || (email is not null && d.To == email)) && !earlier.Contains(d.LogId))];
    }

    private async Task<List<AuditEvent>> AuditAsync(System.Linq.Expressions.Expression<Func<AuditEvent, bool>> which)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking().Where(which).ToListAsync(Ct);
    }

    /// <summary>A patient of this test alone, on a number of its own; the duplicate check is skipped (each name is unique anyway).</summary>
    private static Task<PatientCreated> RegisterAsync(ApiClient frontOffice, string firstName, string phone, DateOnly born, string? email = null) =>
        frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: TestPatients.FirstName(firstName), MiddleName: null, LastName: "Verify", Gender: Gender.Female,
            DateOfBirth: born, AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: email, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));

    private static string NewMobile() => Phone.Normalize("9" + Digits(9))!;

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
