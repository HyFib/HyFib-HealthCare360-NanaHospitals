using System.Net;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Networking;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The sweep for records the integrations' simulators made (audit P6), over the real API and database: every kind's query
/// runs on the real schema, a payment the payment simulator took is found by the marks that simulator leaves, and the IT
/// officer's sign-off covers what the sweep found until a simulator makes another record. System health warns about them
/// only on a live server, where they do not belong.
/// </summary>
public sealed class SimulatorRecordSweepTests(HospitalServer server)
{
    private const string Records = "/api/v1/admin/system/simulator-records";

    [Fact]
    public async Task A_payment_the_simulator_took_is_found_signed_off_and_found_again_after_another()
    {
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await using var payments = await PaymentsToSimulatorAsync(admin);
        var first = await PaidLinkAsync(billing);

        var sweep = await admin.PostAsync<SimulatorSweep>($"{Records}/sweep");

        sweep.Findings.Select(f => f.Code).ShouldBe(
        [
            "payments", "refunds", "card-sales", "aadhaar-links", "imaging", "lab-results", "gateway-settlements", "card-settlements",
            "messages", "tally", "attendance", "cardiology-files", "labels",
        ]);
        var found = sweep.Findings.Single(f => f.Code == "payments");
        found.Severity.ShouldBe("critical");
        found.Examples.ShouldContain(first.OrderId, "the newest payment the simulator took, by its order number");
        found.Amount.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(first.Amount);
        var view = await admin.GetAsync<SimulatorRecordsView>(Records);
        (view.Latest?.SweptAt, view.Outstanding).ShouldBe((sweep.SweptAt, true));

        // The sign-off says what was done, and is for the sweep the IT officer reviewed.
        using (var vague = await admin.SendRawAsync(HttpMethod.Post, $"{Records}/sign-off", new SignOffSimulatorRecordsRequest(sweep.SweptAt, "done")))
        {
            vague.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using (var older = await admin.SendRawAsync(HttpMethod.Post, $"{Records}/sign-off",
                   new SignOffSimulatorRecordsRequest(sweep.SweptAt.AddMinutes(-5), "Training receipts cancelled, bills reopened.")))
        {
            older.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        }

        var signed = await admin.PostAsync<SimulatorSignOff>($"{Records}/sign-off",
            new SignOffSimulatorRecordsRequest(sweep.SweptAt, "Training receipts cancelled, bills reopened."));
        (signed.SweptAt, signed.CoversUntil, signed.Total).ShouldBe((sweep.SweptAt, sweep.Newest, sweep.Total));
        (await admin.GetAsync<SimulatorRecordsView>(Records)).Outstanding.ShouldBeFalse();

        // Another payment through the simulator: the next sweep finds it, past what was signed off.
        var second = await PaidLinkAsync(billing);
        var again = await admin.PostAsync<SimulatorSweep>($"{Records}/sweep");
        again.Findings.Single(f => f.Code == "payments").Examples.ShouldContain(second.OrderId);
        (await admin.GetAsync<SimulatorRecordsView>(Records)).ShouldSatisfyAllConditions(
            v => v.Outstanding.ShouldBeTrue(),
            v => v.SignedOff.ShouldNotBeNull().Note.ShouldBe("Training receipts cancelled, bills reopened."));
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
    public async Task The_hospital_administrator_sees_the_sweep_but_only_those_who_manage_the_server_sweep_and_sign_off()
    {
        var hospitalAdmin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        (await hospitalAdmin.GetAsync<SimulatorRecordsView>(Records)).ShouldNotBeNull();
        using var sweep = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"{Records}/sweep");
        using var signOff = await hospitalAdmin.SendRawAsync(HttpMethod.Post, $"{Records}/sign-off",
            new SignOffSimulatorRecordsRequest(DateTimeOffset.UtcNow, "Nothing from training left here."));

        (sweep.StatusCode, signOff.StatusCode).ShouldBe((HttpStatusCode.Forbidden, HttpStatusCode.Forbidden));
    }

    /// <summary>A new patient's registration bill, paid through a link the payment simulator takes at once.</summary>
    private async Task<GatewayOrderDto> PaidLinkAsync(ApiClient billing)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Ms.", FirstName: TestPatients.FirstName("Asha"), MiddleName: null, LastName: "Sweep" + Letters(8), Gender: Gender.Female,
            DateOfBirth: new DateOnly(1985, 1, 1).AddDays(Random.Shared.Next(365 * 20)), AgeYears: null, AgeMonths: null,
            Phone: "8" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(registered.Id, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, registered.Id, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));

        var link = await billing.PostAsync<GatewayOrderDto>("/api/v1/billing/payment-links", new CreatePaymentLinkRequest(bill.Id, null, null, false, false));
        return await billing.PostAsync<GatewayOrderDto>($"/api/v1/billing/payment-links/{link.Id}/refresh");
    }

    /// <summary>
    /// Online payments go to the training gateway for the rest of the test; then the setting is put back, because the
    /// server is shared by every test of the run.
    /// </summary>
    private static async Task<IAsyncDisposable> PaymentsToSimulatorAsync(ApiClient admin)
    {
        const string key = "integrations.payment";
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await admin.SaveSettingAsync(key, new SaveSettingRequest(new JsonObject { ["enabled"] = true, ["provider"] = "simulator" }, null, false, null));
        return new Restore(() => admin.SaveSettingAsync(key, new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('a' + Random.Shared.Next(26))));

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
