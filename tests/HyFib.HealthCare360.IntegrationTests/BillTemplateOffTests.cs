using System.Net;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
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
/// With the bill's WhatsApp template "Not in use", a payment that settles a bill can't send the bill there, so its receipt
/// goes on WhatsApp instead (the patient is not left without either), and the billing desk is told why it can't send the
/// bill. The test switches a hospital-wide template off, so it runs with the feature-switch tests, by itself.
/// </summary>
[Collection(FeatureSwitchCollection.Name)]
public sealed class BillTemplateOffTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_the_bill_template_off_the_receipt_goes_on_whatsapp_and_the_desk_cannot_send_the_bill()
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var (patientId, phone) = await RegisterAsync(frontOffice);
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        var service = await server.CreateServiceAsync(admin, new SaveServiceItemRequest(
            $"SV-{Digits(8)}", "Dressing (bill template off)", null, ServiceCategory.Procedure, null, 450m, 0m, null, true, false, null, true, null));
        var billing = await server.SignInAsync(Users.Billing);
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, patientId, null));
        draft = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/lines", new AddBillLineRequest(service.Id, 1m, null, null, null, null, draft.Version));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        await OpenShiftAsync(billing);

        var switchedOff = await SwitchBillTemplatesAsync(inUse: false);
        try
        {
            var since = DateTimeOffset.UtcNow.AddSeconds(-1);
            await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
                bill.Id, [new PaymentLineInput(PaymentMode.Cash, bill.DueAmount, null, null, null, null, null, null)], Notes: null));

            (await DeliveriesAsync(BillSharing.Template, phone, since)).ShouldBeEmpty("The bill's WhatsApp template is not in use.");
            (await DeliveriesAsync("bill.receipt", phone, since)).Select(d => d.Channel)
                .ShouldBe([NotificationChannel.Sms, NotificationChannel.WhatsApp], ignoreOrder: true);

            using var refused = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{bill.Id}/send-whatsapp");
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.whatsapp_template_off");
        }
        finally
        {
            await SwitchBillTemplatesAsync(inUse: true, only: switchedOff);
        }
    }

    /// <summary>
    /// Sets every stored version of the bill's WhatsApp template in use or not; the ids it changed, so that they alone are
    /// put back.
    /// </summary>
    private async Task<List<Guid>> SwitchBillTemplatesAsync(bool inUse, IReadOnlyCollection<Guid>? only = null)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var templates = await db.Set<NotificationTemplate>()
            .Where(t => t.Key == BillSharing.Template && t.Channel == NotificationChannel.WhatsApp && t.IsActive != inUse)
            .ToListAsync(Ct);
        var changed = templates.Where(t => only is null || only.Contains(t.Id)).ToList();
        changed.ForEach(t => t.IsActive = inUse);
        await db.SaveChangesAsync(Ct);
        return [.. changed.Select(t => t.Id)];
    }

    private async Task<List<NotificationDelivery>> DeliveriesAsync(string key, string phone, DateTimeOffset since)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var messages = await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<OutboxMessage>().AsNoTracking()
            .Where(m => (m.Type == NotificationService.SmsType || m.Type == NotificationService.WhatsAppType) && m.CreatedAt >= since)
            .ToListAsync(Ct);
        return messages.Select(m => m.Read<NotificationDelivery>()).Where(d => d.TemplateKey == key && d.To == phone).ToList();
    }

    private static async Task OpenShiftAsync(ApiClient billing)
    {
        using var shift = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (shift.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    private static async Task<(Guid PatientId, string Phone)> RegisterAsync(ApiClient frontOffice)
    {
        var mobile = "9" + Digits(9);
        var created = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: null, FirstName: "Kavitha", MiddleName: null, LastName: "Bill" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1975, 1, 1).AddDays(Random.Shared.Next(7300)), AgeYears: null, AgeMonths: null,
            Phone: mobile, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null,
            GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null,
            Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null,
            ConfirmedNotDuplicate: true));
        return (created.Id, Phone.Normalize(mobile)!);
    }

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('a' + Random.Shared.Next(26)))]);
}
