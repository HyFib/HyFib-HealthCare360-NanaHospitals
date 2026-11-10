using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Identity;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The sweep for records the integrations' simulators made (audit P6), over the real API and database: every kind's query
/// runs on the real schema, and a payment, an appointment's prepayment and a refund the payment simulator made are found by
/// the marks it leaves and stay open until corrected through the real reversals. No sign-off is taken while one is open
/// (the tests share a database, so the sign-off taken once every one is corrected is tested on its own, in
/// SimulatorRecordWarningTests). System health warns about them only on a live server, where they do not belong.
/// </summary>
public sealed class SimulatorRecordSweepTests(HospitalServer server)
{
    private const string Records = "/api/v1/admin/system/simulator-records";

    [Fact]
    public async Task A_payment_the_simulator_took_stays_open_until_its_money_is_reversed_and_no_sign_off_is_taken_meanwhile()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        await using var payments = await PaymentsToSimulatorAsync(admin);
        var paid = await PaidLinkAsync(billing);
        var receipt = await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{paid.ReceiptId.ShouldNotBeNull()}");

        var sweep = await admin.PostAsync<SimulatorSweep>($"{Records}/sweep");

        sweep.Findings.Select(f => f.Code).ShouldBe(
        [
            "payments", "refunds", "card-sales", "aadhaar-links", "imaging", "lab-results", "einvoices", "gateway-settlements", "card-settlements",
            "messages", "tally", "attendance", "cardiology-files", "labels",
        ]);
        var found = sweep.Findings.Single(f => f.Code == "payments");
        found.Severity.ShouldBe("critical");
        found.Examples.ShouldContain(receipt.ReceiptNumber, "the newest payment the simulator took, by its receipt");
        found.Amount.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(receipt.Amount);
        found.Open.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(1);
        sweep.Findings.Where(f => f.Severity == "critical").ShouldAllBe(f => f.Open != null, "every critical kind says which are still open");
        sweep.Findings.Where(f => f.Severity != "critical").ShouldAllBe(f => f.Open == null, "the others are for the IT officer to acknowledge");
        var open = (await SimulatorRecords.OpenAsync(admin, "payments")).Single(r => r.EntityId == receipt.Id);
        (open.Reference, open.Entity, open.Amount, open.PatientId).ShouldBe((receipt.ReceiptNumber, "Receipt", (decimal?)receipt.Amount, (Guid?)receipt.PatientId));
        var view = await admin.GetAsync<SimulatorRecordsView>(Records);
        (view.Latest?.SweptAt, view.Outstanding).ShouldBe((sweep.SweptAt, true));

        // The sign-off says what was done, is for the sweep the IT officer reviewed, and is not taken while a payment the
        // simulator took still stands as received.
        using (var vague = await admin.SendRawAsync(HttpMethod.Post, $"{Records}/sign-off", new SignOffSimulatorRecordsRequest(sweep.SweptAt, "done")))
        {
            vague.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var older = await admin.SendRawAsync(HttpMethod.Post, $"{Records}/sign-off",
                   new SignOffSimulatorRecordsRequest(sweep.SweptAt.AddMinutes(-5), "Training receipts reversed, bills owe again.")))
        {
            older.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        using (var refused = await admin.SendRawAsync(HttpMethod.Post, $"{Records}/sign-off",
                   new SignOffSimulatorRecordsRequest(sweep.SweptAt, "Training receipts reversed, bills owe again.")))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await refused.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            problem.ShouldContain("simulators.critical_open");
            problem.ShouldContain("online payments the payment simulator took");
        }

        // Its money recorded as not received, one person asking and another approving: still found, and open no more.
        await SimulatorRecords.ReverseAsync(billing, accountant, receipt);
        (await SimulatorRecords.OpenAsync(admin, "payments")).ShouldNotContain(r => r.EntityId == receipt.Id);
        Examples(await admin.PostAsync<SimulatorSweep>($"{Records}/sweep"), "payments").ShouldContain(receipt.ReceiptNumber);
    }

    [Fact]
    public async Task An_appointment_prepayment_the_simulator_took_is_found_though_it_made_no_gateway_order()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var facilityId = (await billing.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        var patient = await RegisterAsync();

        // As the payment simulator's confirmation of an appointment's prepayment records it: an advance and its receipt, no order.
        PatientAdvance advance;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var ct = TestContext.Current.CancellationToken;
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            advance = await scope.ServiceProvider.GetRequiredService<AdvanceService>().RecordOnlinePaymentAsync(
                facilityId, patient.Id, 500m, "pay_sim" + Hex(20), "order_sim" + Hex(20), "simulator", "Online prepayment for an appointment",
                DateTimeOffset.UtcNow, null, null, ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        var receipt = await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{advance.ReceiptId}");
        (await SimulatorRecords.OpenAsync(admin, "payments")).Single(r => r.EntityId == receipt.Id).Amount.ShouldBe(500m);
        Examples(await admin.PostAsync<SimulatorSweep>($"{Records}/sweep"), "payments").ShouldContain(receipt.ReceiptNumber);

        await SimulatorRecords.ReverseAsync(billing, accountant, receipt);
        (await SimulatorRecords.OpenAsync(admin, "payments")).ShouldNotContain(r => r.EntityId == receipt.Id);
    }

    [Fact]
    public async Task A_refund_the_simulator_said_it_paid_back_stays_open_until_recorded_as_not_paid_out()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        await using var payments = await PaymentsToSimulatorAsync(admin);
        await OpenShiftAsync(billing);

        // Paid online, the bill cancelled, and its money paid back through the gateway: the simulator says it was.
        var paid = await PaidLinkAsync(billing);
        var bill = await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{paid.BillId.ShouldNotBeNull()}");
        bill = await hospitalAdmin.PostAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}/cancel", new CancelBillRequest("Billed to the wrong patient", bill.Version));
        var refundId = bill.Refunds.ShouldHaveSingleItem().Id;
        await NoContentAsync(accountant, $"/api/v1/billing/refunds/{refundId}/approve", new ApproveRefundRequest(null));
        await NoContentAsync(billing, $"/api/v1/billing/refunds/{refundId}/pay", new PayRefundRequest(PaymentMode.PaymentGateway, null));
        var refund = await billing.GetAsync<RefundDto>($"/api/v1/billing/refunds/{refundId}");
        refund.Status.ShouldBe(RefundStatus.Paid);
        (await SimulatorRecords.OpenAsync(admin, "refunds")).Single(r => r.EntityId == refund.Id).ShouldSatisfyAllConditions(
            r => r.Reference.ShouldBe(refund.RefundNumber),
            r => r.Amount.ShouldBe(refund.Amount));

        // Recorded as not paid out, one person asking and another approving: open no more.
        using var asked = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/refunds/{refund.Id}/not-paid",
            new ReverseRefundRequest(ReversalReason.TrainingSimulator, "The payment simulator made this refund up."));
        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted, await asked.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await MakerChecker.ApproveAsync(accountant,
            (await asked.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, TestContext.Current.CancellationToken)).ShouldNotBeNull());
        (await SimulatorRecords.OpenAsync(admin, "refunds")).ShouldNotContain(r => r.EntityId == refund.Id);
    }

    [Fact]
    public async Task System_health_warns_about_them_only_on_a_live_server()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await using (await PaymentsToSimulatorAsync(admin))
        {
            await PaidLinkAsync(billing);
        }

        await admin.PostAsync<SimulatorSweep>($"{Records}/sweep");

        // A training server may use the simulators: what they made is expected there.
        (await admin.GetAsync<SystemHealthReport>("/api/v1/admin/system/health")).Warnings.ShouldNotContain(w => w.Code == "simulators.records");
        SystemHealthReport live;
        using (server.AsLiveServer())
        {
            live = await admin.GetAsync<SystemHealthReport>("/api/v1/admin/system/health");
        }

        var warning = live.Warnings.Single(w => w.Code == "simulators.records");
        warning.Severity.ShouldBe("critical", "money the hospital never received is recorded as received");
        warning.Message.ShouldContain("online payments the payment simulator took");
        warning.Message.ShouldContain("Administration › Simulator records");
    }

    [Fact]
    public async Task What_never_took_effect_is_not_found_a_link_nobody_paid_a_declined_sale_and_a_rejected_result()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var facilityId = (await billing.GetAsync<AuthUser>("/api/v1/auth/me")).FacilityId.ShouldNotBeNull();
        await using var payments = await PaymentsToSimulatorAsync(admin);
        var unpaid = await LinkAsync(billing);
        var patientId = (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{unpaid.BillId.ShouldNotBeNull()}")).PatientId;

        // A card sale the terminal simulator declined, and a results message HC360 rejected, as each leaves its record.
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var declined = new PosTransaction
        {
            FacilityId = facilityId,
            Provider = "simulator",
            TerminalId = "SIM-TERM-1",
            TransactionId = "possim_" + Letters(12),
            Reference = "POS-" + Letters(8),
            Amount = 777m,
            Mode = PaymentMode.Card,
            Purpose = ReceiptPurpose.BillPayment,
            PatientId = Guid.CreateVersion7(),
            Status = PosTransactionStatus.Declined,
            Message = "Declined by the terminal simulator.",
        };
        var rejected = new IntegrationLog
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Integration = "lab",
            Provider = "simulator:analyser",
            Operation = "results",
            Direction = IntegrationDirection.Inbound,
            Success = false,
            Reference = "SIMREJ" + Letters(8),
            Error = "No such sample.",
        };
        db.AddRange(declined, rejected);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var sweep = await admin.PostAsync<SimulatorSweep>($"{Records}/sweep");

        // All three are checked, so a sweep that finds more than one says so for each.
        var unpaidOpen = await SimulatorRecords.OpenAsync(admin, "payments");
        sweep.ShouldSatisfyAllConditions(
            () => unpaidOpen.ShouldNotContain(r => r.PatientId == patientId, "nobody paid the link: no receipt to correct"),
            () => Examples(sweep, "card-sales").ShouldNotContain(declined.TransactionId, "a declined sale made no receipt"),
            () => Examples(sweep, "lab-results").ShouldNotContain(rejected.Reference, "a rejected message gave no results"));

        // The link once paid is found, by its receipt, and is open.
        var paid = await billing.PostAsync<GatewayOrderDto>($"/api/v1/billing/payment-links/{unpaid.Id}/refresh");
        var receipt = await billing.GetAsync<ReceiptDto>($"/api/v1/billing/receipts/{paid.ReceiptId.ShouldNotBeNull()}");
        Examples(await admin.PostAsync<SimulatorSweep>($"{Records}/sweep"), "payments").ShouldContain(receipt.ReceiptNumber);
        (await SimulatorRecords.OpenAsync(admin, "payments")).ShouldContain(r => r.EntityId == receipt.Id && r.PatientId == patientId);
    }

    private static IReadOnlyList<string> Examples(SimulatorSweep sweep, string code) => sweep.Findings.Single(f => f.Code == code).Examples;

    [Fact]
    public async Task The_hospital_administrator_sees_the_sweep_but_only_those_who_manage_the_server_sweep_and_sign_off()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        (await hospitalAdmin.GetAsync<SimulatorRecordsView>(Records)).ShouldNotBeNull();
        (await SimulatorRecords.OpenAsync(hospitalAdmin, "messages")).ShouldBeEmpty("a kind the IT officer acknowledges has nothing to correct");
        using (var unknown = await hospitalAdmin.SendRawAsync(HttpMethod.Get, $"{Records}/no-such-kind/open"))
        {
            unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        using var sweep = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"{Records}/sweep");
        using var signOff = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"{Records}/sign-off",
            new SignOffSimulatorRecordsRequest(DateTimeOffset.UtcNow, "Nothing from training left here."));

        (sweep.StatusCode, signOff.StatusCode).ShouldBe((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden));
    }

    /// <summary>A new patient's registration bill, paid through a link the payment simulator takes at once.</summary>
    private async Task<GatewayOrderDto> PaidLinkAsync(ApiClient billing) =>
        await billing.PostAsync<GatewayOrderDto>($"/api/v1/billing/payment-links/{(await LinkAsync(billing)).Id}/refresh");

    /// <summary>
    /// A payment link for a new patient's registration bill. The simulator takes the payment at once, but HC360 marks the
    /// order paid only when it hears so: until the link is refreshed (or the gateway calls back), there is no receipt.
    /// </summary>
    private async Task<GatewayOrderDto> LinkAsync(ApiClient billing)
    {
        var registered = await RegisterAsync();
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(registered.Id, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, registered.Id, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));

        return await billing.PostAsync<GatewayOrderDto>("/api/v1/billing/payment-links", new CreatePaymentLinkRequest(bill.Id, null, null, false, false));
    }

    private async Task<PatientCreated> RegisterAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        return await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: TestPatients.FirstName("Asha"), MiddleName: null, LastName: "Sweep" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "8" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
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
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static string Hex(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => "0123456789abcdef"[Random.Shared.Next(16)]));

    /// <summary>
    /// Online payments go to the training gateway for the rest of the test; then the setting is put back, because the
    /// server is shared by every test of the run.
    /// </summary>
    private async Task<IAsyncDisposable> PaymentsToSimulatorAsync(ApiClient admin)
    {
        const string key = "integrations.payment";
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await server.SaveSettingAsync(admin, key, new SaveSettingRequest(new JsonObject { ["enabled"] = true, ["provider"] = "simulator" }, null, false, null));
        return new Restore(() => server.SaveSettingAsync(admin, key, new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
