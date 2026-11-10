using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Modules.Patients.Services;
using HyFib.HealthCare360.Modules.Scheduling.Domain;
using HyFib.HealthCare360.Modules.Scheduling.Features.Appointments;
using HyFib.HealthCare360.Modules.Scheduling.Features.Availability;
using HyFib.HealthCare360.Modules.Scheduling.Features.Portal;
using HyFib.HealthCare360.Modules.Scheduling.Services;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A patient who pays for one portal booking twice (OA-03): the online payment was started again, a second gateway
/// order replaced the first, and both were paid. The training gateway stands in for the real one, and its ledger is what
/// the gateway took and gave back. The money taken on the earlier order is found when its webhook arrives, by the
/// re-check when the webhook is lost, and by the settlement match when neither ran. It pays the booking when nothing else
/// has; otherwise it is the patient's advance, linked to the booking, refunded on the gateway once a second person
/// approves.
/// </summary>
public sealed class BookingPaidTwiceTests(HospitalServer server)
{
    private const string SimulatorSecret = "integration-test-simulator-key";
    private static readonly TimeSpan IndiaOffset = TimeSpan.FromMinutes(330);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(IndiaOffset).DateTime);

    [Fact]
    public async Task The_second_payment_is_kept_as_the_patients_advance_and_goes_back_on_the_gateway_once_a_second_person_approves()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var booking = await RestartedBookingAsync();

        // The patient pays the new checkout: the booking is paid.
        (await booking.Portal.PostAsync<PortalAppointment>($"/api/v1/portal/appointments/{booking.Id}/payment/verify")).PaymentStatus
            .ShouldBe(AppointmentPaymentStatus.PaidOnline);
        var bookingPayment = await SimulatorPaymentIdAsync(booking.SecondOrder);

        // Then pays the first one too, left open on another tab: the gateway takes the money again and says so.
        var secondPayment = await PayAsync(booking.FirstOrder);
        (await DeliverAsync(OrderPaid(booking.FirstOrder, secondPayment), NewEventId()))
            .ShouldBe(new WebhookAck(true), "The webhook of the order the patient replaced is acted on.");

        // The booking keeps the payment that paid it.
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var detail = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booking.Id}");
        detail.GatewayOrderId.ShouldBe(booking.SecondOrder);
        detail.GatewayPaymentId.ShouldBe(bookingPayment);

        // The second payment is the patient's advance, linked to the booking and dated when the gateway took it, and its
        // refund waits for a second person.
        var second = (await AdvanceAsync(secondPayment)).ShouldNotBeNull("The second payment is receipted at once.");
        second.ShouldSatisfyAllConditions(
            a => a.AppointmentId.ShouldBe(booking.Id),
            a => a.AppointmentNumber.ShouldBe(booking.Number),
            a => a.PatientId.ShouldBe(booking.PatientId),
            a => a.Amount.ShouldBe(booking.Fee),
            a => a.GatewayOrderId.ShouldBe(booking.FirstOrder));
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        (await billing.GetAsync<List<PatientAdvanceDto>>($"/api/v1/billing/patients/{booking.PatientId}/advances"))
            .Single(a => a.Id == second.Id).RefundPending.ShouldBe(booking.Fee);
        var refund = (await RefundsAsync(billing, booking.PatientId)).ShouldHaveSingleItem("Only the second payment goes back.");
        refund.ShouldSatisfyAllConditions(
            r => r.AdvanceId.ShouldBe(second.Id),
            r => r.Amount.ShouldBe(booking.Fee),
            r => r.Status.ShouldBe(RefundStatus.Requested),
            r => r.Reason.ShouldContain($"Appointment {booking.Number} was paid twice"));

        // The gateway telling it again records nothing twice.
        (await DeliverAsync(OrderPaid(booking.FirstOrder, secondPayment), NewEventId())).ShouldBe(new WebhookAck(true));
        (await AdvanceCountAsync(secondPayment)).ShouldBe(1);
        (await RefundsAsync(billing, booking.PatientId)).Count.ShouldBe(1);

        // A second person approves the refund and the cashier pays it back on the gateway: the gateway's own ledger
        // shows the second payment refunded, and the one that paid the booking kept.
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        await NoContentAsync(accountant, $"/api/v1/billing/refunds/{refund.Id}/approve", new ApproveRefundRequest(null));
        await OpenShiftAsync(billing);
        await NoContentAsync(billing, $"/api/v1/billing/refunds/{refund.Id}/pay", new PayRefundRequest(PaymentMode.PaymentGateway, null));
        (await billing.GetAsync<RefundDto>($"/api/v1/billing/refunds/{refund.Id}")).Status.ShouldBe(RefundStatus.Paid);
        (await SimulatedRefundsAsync(secondPayment)).ShouldHaveSingleItem().ShouldBe(booking.Fee);
        (await SimulatedRefundsAsync(bookingPayment)).ShouldBeEmpty();

        // The settlement matches both payments to their receipts: nothing is left for the accountant.
        await EventuallyAsync(() => AdvanceAsync(bookingPayment), "The booking's own payment was not receipted.");
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await accounts.PostAsync<SettlementFetchResult>("/api/v1/billing/gateway/settlements/fetch", new FetchSettlementsRequest(Today));
        (await OpenExceptionsAsync(accounts)).ShouldNotContain(e => e.Key == secondPayment || e.Key == bookingPayment);
    }

    [Fact]
    public async Task The_earlier_order_paid_first_pays_the_booking_and_the_later_one_paid_after_is_the_second_payment()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var booking = await RestartedBookingAsync();
        await SetSimulatorAsync(booking.SecondOrder, SimulatorTransactionStatus.Created);

        // Nothing paid the booking yet: the money taken on the earlier order is the booking's own.
        var firstPayment = await PayAsync(booking.FirstOrder);
        (await DeliverAsync(OrderPaid(booking.FirstOrder, firstPayment), NewEventId())).ShouldBe(new WebhookAck(true));
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var detail = await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booking.Id}");
        detail.ShouldSatisfyAllConditions(
            d => d.PaymentStatus.ShouldBe(AppointmentPaymentStatus.PaidOnline),
            d => d.Status.ShouldBe(AppointmentStatus.Confirmed),
            d => d.GatewayOrderId.ShouldBe(booking.FirstOrder),
            d => d.GatewayPaymentId.ShouldBe(firstPayment));
        (await AdvanceAsync(firstPayment)).ShouldNotBeNull().AppointmentId.ShouldBe(booking.Id);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        (await RefundsAsync(billing, booking.PatientId)).ShouldBeEmpty("The booking's own payment is not refunded.");

        // The checkout started later is paid after: that is the second payment.
        var secondPayment = await PayAsync(booking.SecondOrder);
        (await DeliverAsync(OrderPaid(booking.SecondOrder, secondPayment), NewEventId())).ShouldBe(new WebhookAck(true));
        (await frontOffice.GetAsync<AppointmentDetail>($"/api/v1/scheduling/appointments/{booking.Id}")).GatewayPaymentId.ShouldBe(firstPayment);
        var second = (await AdvanceAsync(secondPayment)).ShouldNotBeNull();
        second.AppointmentId.ShouldBe(booking.Id);
        var refund = (await RefundsAsync(billing, booking.PatientId)).ShouldHaveSingleItem();
        refund.AdvanceId.ShouldBe(second.Id);
        refund.Amount.ShouldBe(booking.Fee);
    }

    [Fact]
    public async Task Without_its_webhook_the_re_check_finds_the_second_payment()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var booking = await RestartedBookingAsync();
        (await booking.Portal.PostAsync<PortalAppointment>($"/api/v1/portal/appointments/{booking.Id}/payment/verify")).PaymentStatus
            .ShouldBe(AppointmentPaymentStatus.PaidOnline);
        var secondPayment = await PayAsync(booking.FirstOrder);

        // No webhook comes. A few minutes on, the job asks the gateway about the order the patient replaced.
        await ReplacedAsync(booking.FirstOrder, DateTimeOffset.UtcNow.AddMinutes(-3));
        await using (var scope = server.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<GatewayReconciliationRunner>().RecheckReplacedOrdersAsync(Ct)).ShouldBeGreaterThan(0);
        }

        var second = (await AdvanceAsync(secondPayment)).ShouldNotBeNull("The re-check receipts the second payment.");
        second.AppointmentId.ShouldBe(booking.Id);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        (await RefundsAsync(billing, booking.PatientId)).ShouldHaveSingleItem().AdvanceId.ShouldBe(second.Id);

        // A paid one is not asked about again.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            (await db.Set<ReplacedPaymentOrder>().IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.OrderId == booking.FirstOrder, Ct))
                .GatewayPaymentId.ShouldBe(secondPayment);
        }
    }

    [Fact]
    public async Task When_only_the_settlement_shows_it_recording_the_receipt_links_it_to_the_booking_and_asks_for_the_refund()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var booking = await RestartedBookingAsync();
        (await booking.Portal.PostAsync<PortalAppointment>($"/api/v1/portal/appointments/{booking.Id}/payment/verify")).PaymentStatus
            .ShouldBe(AppointmentPaymentStatus.PaidOnline);
        var bookingPayment = await SimulatorPaymentIdAsync(booking.SecondOrder);
        await EventuallyAsync(() => AdvanceAsync(bookingPayment), "The booking's own payment was not receipted.");
        var secondPayment = await PayAsync(booking.FirstOrder);

        // Neither the webhook nor the re-check ran: the settlement is the first to show the money, and it names the booking.
        var accounts = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await accounts.PostAsync<SettlementFetchResult>("/api/v1/billing/gateway/settlements/fetch", new FetchSettlementsRequest(Today));
        var exception = (await OpenExceptionsAsync(accounts)).Single(e => e.Key == secondPayment);
        exception.ShouldSatisfyAllConditions(
            e => e.Kind.ShouldBe(DiscrepancyKind.UnrecordedPayment),
            e => e.OrderId.ShouldBe(booking.FirstOrder),
            e => e.AppointmentId.ShouldBe(booking.Id),
            e => e.AppointmentNumber.ShouldBe(booking.Number),
            e => e.GatewayAmount.ShouldBe(booking.Fee));

        // The accountant records the receipt: the payment is the booking's second, kept as an advance with its refund
        // asked for, and the exception is closed.
        var recorded = await accounts.PostAsync<DiscrepancyActionResult>($"/api/v1/billing/gateway/exceptions/{exception.Id}/record-receipt");
        recorded.Message.ShouldContain($"Booking {booking.Number} was paid twice");
        recorded.Discrepancy.Status.ShouldBe(DiscrepancyStatus.Resolved);
        var second = (await AdvanceAsync(secondPayment)).ShouldNotBeNull();
        second.AppointmentId.ShouldBe(booking.Id);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        (await RefundsAsync(billing, booking.PatientId)).ShouldHaveSingleItem().AdvanceId.ShouldBe(second.Id);
    }

    /// <summary>A new patient's portal booking whose online payment was started (the first order), closed unpaid and started again.</summary>
    private async Task<Restarted> RestartedBookingAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var phone = "7" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10)));
        var patient = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Ravi"), MiddleName: null, LastName: "Twice" + Letters(8), Gender: Gender.Male,
            DateOfBirth: new DateOnly(1980, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: phone, AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null,
            GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null,
            Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null,
            PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: true));

        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var practitionerId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var today = server.Services.GetRequiredService<TimeProvider>().Today();
        var availability = await frontOffice.GetAsync<List<PractitionerAvailability>>(
            $"/api/v1/scheduling/slots?practitionerId={practitionerId}&from={today:yyyy-MM-dd}&to={today.AddDays(6):yyyy-MM-dd}&mode=InPerson&includeFull=false");
        var slot = availability.SelectMany(a => a.Slots).Where(s => s.Available > 0 && s.StartsAt > DateTimeOffset.UtcNow.AddHours(1)).MinBy(s => s.StartsAt)
                   ?? throw new InvalidOperationException("The doctor has no free slot in the coming week.");

        var portal = await server.SignInToPortalAsync(phone);
        var booked = await portal.PostAsync<PortalBookingResult>("/api/v1/portal/appointments", new PortalBookAppointmentRequest(
            patient.Id, practitionerId, slot.StartsAt, AppointmentVisitType.New, IsTelehealth: false, "Fever for two days", PayOnline: true));
        var first = booked.Payment.ShouldNotBeNull("The training gateway takes online payments.").OrderId;
        booked.Appointment.FeeAmount.ShouldBeGreaterThan(0m);

        // The patient closes the checkout without paying, and starts the payment again: a new order replaces the first.
        await SetSimulatorAsync(first, SimulatorTransactionStatus.Created);
        var again = await portal.PostAsync<PaymentCheckout>($"/api/v1/portal/appointments/{booked.Appointment.Id}/payment");
        again.OrderId.ShouldNotBe(first);
        return new Restarted(booked.Appointment.Id, booked.Appointment.Number, patient.Id, booked.Appointment.FeeAmount, first, again.OrderId, portal);
    }

    /// <summary>The patient pays the order on the training gateway; returns the payment the gateway took.</summary>
    private async Task<string> PayAsync(string orderId)
    {
        await SetSimulatorAsync(orderId, SimulatorTransactionStatus.Captured);
        return await SimulatorPaymentIdAsync(orderId);
    }

    private Task SetSimulatorAsync(string orderId, SimulatorTransactionStatus status) => SetSimulatorAsync(server, orderId, status);

    /// <summary>The patient pays (or leaves) the training gateway's checkout of <paramref name="orderId"/>.</summary>
    internal static async Task SetSimulatorAsync(HospitalServer server, string orderId, SimulatorTransactionStatus status)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SimulatorTransaction>()
            .Where(t => t.ExternalId == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, status), Ct)).ShouldBe(1);
    }

    private async Task<string> SimulatorPaymentIdAsync(string orderId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SimulatorTransaction>().AsNoTracking()
            .Where(t => t.ExternalId == orderId)
            .Select(t => t.PaymentId)
            .SingleAsync(Ct)).ShouldNotBeNull();
    }

    /// <summary>The amounts the training gateway refunded on a payment.</summary>
    private async Task<List<decimal>> SimulatedRefundsAsync(string paymentId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SimulatorTransaction>().AsNoTracking()
            .Where(t => t.Kind == SimulatorTransactionKind.PaymentRefund && t.Reference == paymentId)
            .Select(t => t.Amount)
            .ToListAsync(Ct);
    }

    /// <summary>Moves back when the booking's order was replaced, as time passing would.</summary>
    private async Task ReplacedAsync(string orderId, DateTimeOffset at)
    {
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ReplacedPaymentOrder>().IgnoreQueryFilters()
            .Where(o => o.OrderId == orderId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.ReplacedAt, at), Ct)).ShouldBe(1);
    }

    private async Task<PatientAdvance?> AdvanceAsync(string paymentId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PatientAdvance>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(a => a.GatewayPaymentId == paymentId, Ct);
    }

    private async Task<int> AdvanceCountAsync(string paymentId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PatientAdvance>().IgnoreQueryFilters()
            .CountAsync(a => a.GatewayPaymentId == paymentId, Ct);
    }

    private static async Task<IReadOnlyList<RefundDto>> RefundsAsync(ApiClient billing, Guid patientId) =>
        (await billing.GetAsync<PagedResult<RefundDto>>($"/api/v1/billing/refunds?patientId={patientId}&pageSize=50")).Items;

    private static async Task<IReadOnlyList<GatewayDiscrepancyDto>> OpenExceptionsAsync(ApiClient accounts) =>
        (await accounts.GetAsync<PagedResult<GatewayDiscrepancyDto>>(
            $"/api/v1/billing/gateway/exceptions?status={DiscrepancyStatus.Open}&kind={DiscrepancyKind.UnrecordedPayment}&pageSize=200")).Items;

    /// <summary>Razorpay's <c>order.paid</c> event for a payment on an order.</summary>
    private static string OrderPaid(string orderId, string paymentId) => new JsonObject
    {
        ["entity"] = "event",
        ["event"] = "order.paid",
        ["payload"] = new JsonObject
        {
            ["payment"] = new JsonObject { ["entity"] = new JsonObject { ["id"] = paymentId, ["order_id"] = orderId, ["status"] = "captured" } },
            ["order"] = new JsonObject { ["entity"] = new JsonObject { ["id"] = orderId, ["status"] = "paid" } },
        },
    }.ToJsonString();

    private static string NewEventId() => "evt" + Guid.NewGuid().ToString("N")[..20];

    private async Task<WebhookAck?> DeliverAsync(string body, string eventId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/webhooks/payment-gateway")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Razorpay-Signature", Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SimulatorSecret), Encoding.UTF8.GetBytes(body))));
        request.Headers.Add("X-Razorpay-Event-Id", eventId);
        using var response = await server.Anonymous(ownAddress: true).Http.SendAsync(request, Ct);
        return response.StatusCode == HttpStatusCode.Unauthorized ? null : await ApiClient.ReadAsync<WebhookAck>(response, HttpMethod.Post, "webhook");
    }

    private static async Task OpenShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    private static async Task NoContentAsync(ApiClient client, string path, object body)
    {
        using var response = await client.SendRawAsync(HttpMethod.Post, path, body);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
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

            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        }

        throw new ShouldAssertException(failure);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    /// <summary>
    /// Online payments go to the training gateway, settled at once and signed with a known key, for the rest of the
    /// test; then the settings are put back, because the server is shared by every test of the run.
    /// </summary>
    private Task<IAsyncDisposable> PaymentsToSimulatorAsync() => PaymentsToSimulatorAsync(server);

    /// <summary>Online payments to the training gateway for another test's steps (<see cref="PaymentsToSimulatorAsync()"/>).</summary>
    internal static async Task<IAsyncDisposable> PaymentsToSimulatorAsync(HospitalServer server)
    {
        const string key = "integrations.payment";
        const string secretKey = "integrations.payment.secret";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await server.SaveSettingAsync(admin, key, new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["simulatorSettlementDays"] = 0 }, null, false, null));
        await server.SaveSettingAsync(admin, secretKey, new SaveSettingRequest(
            new JsonObject { ["simulatorSecret"] = SimulatorSecret }, null, true, null));
        return new Restore(async () =>
        {
            await server.SaveSettingAsync(admin, key, new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null));
            await server.SaveSettingAsync(admin, secretKey, new SaveSettingRequest(new JsonObject(), null, true, null));
        });
    }

    private sealed record Restarted(Guid Id, string Number, Guid PatientId, decimal Fee, string FirstOrder, string SecondOrder, ApiClient Portal);

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
