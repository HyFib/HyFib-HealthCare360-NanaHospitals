using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Features.Tariffs;
using HyFib.HealthCare360.Modules.Engagement.Features.Portal;
using HyFib.HealthCare360.Modules.Engagement.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.FrontOffice;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Messaging;
using HyFib.HealthCare360.Platform.Notifications;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// WhatsApp opt-in tracking and bills on WhatsApp (INT-WA), through the real API and the WhatsApp delivery handler:
/// where an opt-in comes from (the desk, the portal, a START reply), that a STOP outranks it until the number's holder
/// lifts it, that reminders need it while a bill does not, and that a settled bill goes to the patient with its PDF.
/// </summary>
public sealed class WhatsAppOptInTests(HospitalServer server)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly NotificationFile BillPdf = new("bill-NHOB260000001.pdf", [0x25, 0x50, 0x44, 0x46], "application/pdf");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_desk_records_the_opt_in_at_registration_and_a_stop_outranks_it_until_the_number_replies_start()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patientId, phone) = await RegisterAsync(frontOffice, optIn: true);

        var status = await frontOffice.GetAsync<WhatsAppConsentDto>($"/api/v1/patients/{patientId}/whatsapp");
        status.OptedIn.ShouldBeTrue();
        status.Source.ShouldBe(WhatsAppOptInSource.RegistrationDesk);
        status.CapturedByName.ShouldNotBeNullOrWhiteSpace("The desk opt-in names who recorded it.");
        status.Reachable.ShouldBeTrue();
        status.Phone.ShouldNotBe(phone, "The number is shown masked.");

        // The number replies STOP: nothing but sign-in codes until it says otherwise.
        var stopAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        await ReplyAsync(phone, optedOut: true, stopAt);
        status = await frontOffice.GetAsync<WhatsAppConsentDto>($"/api/v1/patients/{patientId}/whatsapp");
        (status.OptedOut, status.Reachable).ShouldBe((true, false));

        // The desk can record another opt-in, but it does not lift the STOP: a family member may be at the desk.
        status = await frontOffice.PostAsync<WhatsAppConsentDto>($"/api/v1/patients/{patientId}/whatsapp/opt-in");
        (status.OptedOut, status.Reachable).ShouldBe((true, false));

        // START from the number lifts it, and is itself recorded as an opt-in (once, however often Meta delivers it).
        var startAt = DateTimeOffset.UtcNow.AddSeconds(5);
        await ReplyAsync(phone, optedOut: false, startAt, "wamid.START1");
        await ReplyAsync(phone, optedOut: false, startAt, "wamid.START1");
        status = await frontOffice.GetAsync<WhatsAppConsentDto>($"/api/v1/patients/{patientId}/whatsapp");
        (status.OptedOut, status.Reachable, status.Source).ShouldBe((false, true, WhatsAppOptInSource.WhatsAppStart));

        var optIns = await OptInsAsync(phone);
        optIns.Select(o => o.Source).ShouldBe(
            [WhatsAppOptInSource.RegistrationDesk, WhatsAppOptInSource.RegistrationDesk, WhatsAppOptInSource.WhatsAppStart], ignoreOrder: true);
        optIns.ShouldAllBe(o => o.PatientId == null || o.PatientId == patientId);
        optIns.Single(o => o.Source == WhatsAppOptInSource.WhatsAppStart).ProviderMessageId.ShouldBe("wamid.START1");
    }

    [Fact]
    public async Task A_reminder_goes_on_whatsapp_only_to_a_number_that_opted_in_while_a_bill_needs_no_opt_in()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patientId, phone) = await RegisterAsync(frontOffice, optIn: false);
        await using var scope = server.Services.CreateAsyncScope();
        var whatsApp = new FakeWhatsApp();

        var reminder = await DeliveryAsync(scope, "appointment.reminder", phone, patientId, null);
        await Handler(scope, whatsApp).HandleAsync(Message(reminder.Delivery), Ct);
        whatsApp.Sent.ShouldBeEmpty();
        var log = await LogAsync(scope, reminder.LogId);
        (log.Status, log.Error).ShouldBe((NotificationStatus.Suppressed, WhatsAppOptOutPolicy.NotOptedInReason));

        // A bill is about the patient's own care: it goes without an opt-in, with its PDF in the document header.
        var bill = await DeliveryAsync(scope, "bill.shared", phone, patientId, new NotificationDocument("billing.bill", Guid.NewGuid()));
        await Handler(scope, whatsApp).HandleAsync(Message(bill.Delivery), Ct);
        var sent = whatsApp.Sent.ShouldHaveSingleItem();
        sent.TemplateName.ShouldBe("bill_shared");
        sent.HeaderDocument.ShouldNotBeNull().FileName.ShouldBe(BillPdf.FileName);

        // Once the patient agrees at the desk, the reminder goes.
        await frontOffice.PostAsync<WhatsAppConsentDto>($"/api/v1/patients/{patientId}/whatsapp/opt-in");
        var later = await DeliveryAsync(scope, "appointment.reminder", phone, patientId, null);
        await Handler(scope, whatsApp).HandleAsync(Message(later.Delivery), Ct);
        whatsApp.Sent.Count.ShouldBe(2);

        // After STOP, neither goes.
        await ReplyAsync(phone, optedOut: true, DateTimeOffset.UtcNow);
        var afterStop = await DeliveryAsync(scope, "bill.shared", phone, patientId, new NotificationDocument("billing.bill", Guid.NewGuid()));
        await Handler(scope, whatsApp).HandleAsync(Message(afterStop.Delivery), Ct);
        whatsApp.Sent.Count.ShouldBe(2);
        (await LogAsync(scope, afterStop.LogId)).Error.ShouldBe(WhatsAppOptOutPolicy.SuppressedReason);
    }

    [Fact]
    public async Task A_patient_whose_language_has_no_template_gets_the_english_one_asked_for_in_english()
    {
        // The bill template is approved in English only; a Tamil reader's bill falls back to it, so Meta must be asked
        // for the English template, not for a Tamil one that does not exist.
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patientId, phone) = await RegisterAsync(frontOffice, optIn: false);
        await using var scope = server.Services.CreateAsyncScope();
        var whatsApp = new FakeWhatsApp();

        var bill = await DeliveryAsync(scope, "bill.shared", phone, patientId, new NotificationDocument("billing.bill", Guid.NewGuid()), "ta");
        await Handler(scope, whatsApp).HandleAsync(Message(bill.Delivery), Ct);

        var sent = whatsApp.Sent.ShouldHaveSingleItem();
        (sent.TemplateName, sent.Language).ShouldBe(("bill_shared", "en"));
    }

    [Fact]
    public async Task In_the_portal_only_the_numbers_holder_switches_whatsapp_and_switching_on_lifts_a_stop()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patientId, phone) = await RegisterAsync(frontOffice, optIn: false);
        var portal = await server.SignInToPortalAsync(phone);
        var path = $"/api/v1/portal/communication-preferences?patientId={patientId}";

        var preferences = await portal.GetAsync<CommunicationPreferences>(path);
        (preferences.WhatsApp, preferences.WhatsAppEditable).ShouldBe((false, true));

        await ReplyAsync(phone, optedOut: true, DateTimeOffset.UtcNow.AddMinutes(-1));
        await portal.PutAsync("/api/v1/portal/communication-preferences",
            new CommunicationPreferencesRequest(patientId, preferences.Marketing, preferences.Updates, WhatsApp: true));
        preferences = await portal.GetAsync<CommunicationPreferences>(path);
        preferences.WhatsApp.ShouldBeTrue("Signed in with the number, so switching on lifts its STOP as START would.");
        preferences.WhatsAppChangedAt.ShouldNotBeNull();
        (await OptInsAsync(phone)).ShouldHaveSingleItem().Source.ShouldBe(WhatsAppOptInSource.Portal);

        // A family member signed in with another number sees the choice but can't change it.
        var sonPhone = "9" + Digits(9);
        await frontOffice.PostAsync($"/api/v1/patients/{patientId}/portal-family", new LinkFamilyMemberRequest(sonPhone, FamilyRelation.Child, "Patient", true));
        var son = await server.SignInToPortalAsync(sonPhone);
        var seen = await son.GetAsync<CommunicationPreferences>(path);
        (seen.WhatsApp, seen.WhatsAppEditable).ShouldBe((true, false));
        using (var refused = await son.SendRawAsync(HttpMethod.Put, "/api/v1/portal/communication-preferences",
                   new CommunicationPreferencesRequest(patientId, seen.Marketing, seen.Updates, WhatsApp: false)))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("portal.whatsapp_not_holder");
        }

        // Switching off is an opt-out, exactly as a STOP.
        await portal.PutAsync("/api/v1/portal/communication-preferences",
            new CommunicationPreferencesRequest(patientId, seen.Marketing, seen.Updates, WhatsApp: false));
        var status = await frontOffice.GetAsync<WhatsAppConsentDto>($"/api/v1/patients/{patientId}/whatsapp");
        (status.OptedIn, status.OptedOut, status.Reachable).ShouldBe((true, true, false));
    }

    [Fact]
    public async Task A_bill_goes_to_the_patient_on_whatsapp_with_its_pdf_when_it_is_paid_and_when_the_desk_sends_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patientId, phone) = await RegisterAsync(frontOffice, optIn: false);
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var service = await admin.PostAsync<ServiceItemDto>("/api/v1/masters/services", new SaveServiceItemRequest(
            $"SV-{Digits(8)}", "Dressing (WhatsApp bill)", null, ServiceCategory.Procedure, null, 450m, 0m, null, true, false, null, true, null));
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patientId, null));
        draft = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/lines", new AddBillLineRequest(service.Id, 1m, null, null, null, null, draft.Version));

        // A draft is only an estimate: it is not sent, and makes no document.
        using (var refused = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{draft.Id}/send-whatsapp"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.bill_not_shareable");
        }

        await using (var scope = server.Services.CreateAsyncScope())
        {
            var source = scope.ServiceProvider.GetServices<INotificationDocumentSource>().Single(s => s.Kind == "billing.bill");
            (await source.CreateAsync(draft.Id, Ct)).ShouldBeNull();
        }

        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        (await BillMessagesAsync(phone)).ShouldBeEmpty("Finalising alone sends nothing; the payment does.");

        using (var shift = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current"))
        {
            if (shift.StatusCode == HttpStatusCode.NoContent)
            {
                await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
            }
        }

        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            bill.Id, [new PaymentLineInput(PaymentMode.Cash, bill.DueAmount, null, null, null, null, null, null)], Notes: null));

        var paid = (await BillMessagesAsync(phone)).ShouldHaveSingleItem();
        paid.Document.ShouldBe(new NotificationDocument("billing.bill", bill.Id));
        paid.Values["billNumber"].ShouldBe(bill.BillNumber);
        paid.Channel.ShouldBe(NotificationChannel.WhatsApp);

        // The desk sends it again on request.
        await billing.PostAsync($"/api/v1/billing/bills/{bill.Id}/send-whatsapp");
        (await BillMessagesAsync(phone)).Count.ShouldBe(2);

        // The document is the A4 bill, made when the message goes out (where this machine can make PDFs).
        await using (var scope = server.Services.CreateAsyncScope())
        {
            if (scope.ServiceProvider.GetRequiredService<IPdfRenderer>().Status.Available)
            {
                var source = scope.ServiceProvider.GetServices<INotificationDocumentSource>().Single(s => s.Kind == "billing.bill");
                var file = (await source.CreateAsync(bill.Id, Ct)).ShouldNotBeNull();
                (file.FileName, file.ContentType).ShouldBe(($"bill-{bill.BillNumber}.pdf", "application/pdf"));
            }
        }

        // A number that replied STOP is told so at the desk rather than queued and suppressed.
        await ReplyAsync(phone, optedOut: true, DateTimeOffset.UtcNow);
        using var stopped = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/send-whatsapp");
        stopped.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await stopped.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.whatsapp_opted_out");
    }

    private async Task<(Guid PatientId, string Phone)> RegisterAsync(ApiClient frontOffice, bool optIn)
    {
        var mobile = "9" + Digits(9);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Meena", MiddleName: null, LastName: "Wa" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: mobile, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true, WhatsAppOptIn: optIn));
        return (created.Id, Phone.Normalize(mobile)!);
    }

    /// <summary>A STOP or START reply as Integrations raises it for the webhook, handled by the platform's recorder.</summary>
    private async Task ReplyAsync(string phone, bool optedOut, DateTimeOffset at, string? messageId = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await new WhatsAppOptOutRecorder(db).HandleAsync(
            new WhatsAppOptOutChanged(phone, optedOut, at, messageId ?? "wamid." + Guid.NewGuid().ToString("N"), null), Ct);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<List<WhatsAppOptIn>> OptInsAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<WhatsAppOptIn>().AsNoTracking()
            .Where(o => o.Phone == phone).ToListAsync(Ct);
    }

    private async Task<List<NotificationDelivery>> BillMessagesAsync(string phone)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => m.Type == NotificationService.WhatsAppType)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .ToListAsync(Ct);
        return messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == "bill.shared" && d.To == phone).ToList();
    }

    private static async Task<(NotificationDelivery Delivery, Guid LogId)> DeliveryAsync(
        AsyncServiceScope scope, string key, string phone, Guid patientId, NotificationDocument? document, string language = "en")
    {
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var log = new NotificationLog
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Channel = NotificationChannel.WhatsApp,
            TemplateKey = key,
            PatientId = patientId,
            Status = NotificationStatus.Queued,
        };
        db.Add(log);
        await db.SaveChangesAsync(Ct);
        var values = new Dictionary<string, string>
        {
            ["doctor"] = "Dr. Rao",
            ["date"] = "20-10-2026",
            ["time"] = "10:30",
            ["billNumber"] = "NHOB260000001",
            ["amount"] = "450.00",
        };
        return (new NotificationDelivery(log.Id, key, NotificationChannel.WhatsApp, phone, "Meena", language, values, null, document), log.Id);
    }

    private static async Task<NotificationLog> LogAsync(AsyncServiceScope scope, Guid id) =>
        await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<NotificationLog>().AsNoTracking().SingleAsync(l => l.Id == id, Ct);

    private static OutboxMessage Message(NotificationDelivery delivery) =>
        new() { Type = NotificationService.WhatsAppType, Payload = JsonSerializer.Serialize(delivery, Json) };

    private static WhatsAppDeliveryHandler Handler(AsyncServiceScope scope, IWhatsAppSender whatsApp) => new(
        scope.ServiceProvider.GetRequiredService<HospitalDbContext>(), scope.ServiceProvider.GetRequiredService<NotificationTemplateRenderer>(),
        [new BillSource()], whatsApp, TimeProvider.System, NullLogger<WhatsAppDeliveryHandler>.Instance);

    private sealed class BillSource : INotificationDocumentSource
    {
        public string Kind => "billing.bill";

        public Task<NotificationFile?> CreateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<NotificationFile?>(BillPdf);
    }

    private sealed class FakeWhatsApp : IWhatsAppSender
    {
        public List<WhatsAppTemplateMessage> Sent { get; } = [];

        public string Provider => "fake";

        public Task<SendResult> SendTemplateAsync(WhatsAppTemplateMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(SendResult.Ok("wamid.OUT" + Sent.Count));
        }

        public Task<SendResult> SendDocumentAsync(WhatsAppDocumentMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SendResult> SendTextAsync(WhatsAppTextMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
}
