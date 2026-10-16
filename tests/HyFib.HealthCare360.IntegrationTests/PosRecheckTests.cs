using System.Net;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Integrations.Domain;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Shifts;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The background re-check of terminal sales the counter stopped polling (INT-POS), with the training terminal: a sale
/// the customer completes after the counter screen was closed gets its receipt once, however often it is re-checked, and
/// a sale nobody pays is cancelled on the terminal and marked timed out once the facility's timeout has passed.
/// </summary>
public sealed class PosRecheckTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sale_completed_after_the_counter_stopped_polling_is_recorded_once()
    {
        // The training terminal approves a minute after the sale was pushed; the counter never asks again.
        await using var terminal = await TerminalSimulatorAsync(approveAfterSeconds: 60);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await OpenCounterShiftAsync(billing);
        var bill = await FinalizedBillAsync(billing);
        var started = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, "OPD-7", PaymentMode.Card));
        started.Status.ShouldBe(PosTransactionStatus.AwaitingCustomer);

        // Two minutes on: the customer has paid on the terminal and the counter screen is long closed.
        await AgeAsync(started, TimeSpan.FromMinutes(2), terminalToo: true);
        await RecheckAsync();
        await RecheckAsync();

        var sale = await SaleAsync(started.Id);
        sale.Status.ShouldBe(PosTransactionStatus.Approved);
        sale.ReceiptId.ShouldNotBeNull();
        (await ReceiptsForAsync(sale.TransactionId)).ShouldBe(1, "a second re-check finds the receipt and records nothing");
        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).DueAmount.ShouldBe(0m);
    }

    [Fact]
    public async Task A_sale_nobody_pays_is_cancelled_on_the_terminal_and_timed_out_after_the_facilitys_timeout()
    {
        await using var terminal = await TerminalSimulatorAsync(approveAfterSeconds: 300);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        await OpenCounterShiftAsync(billing);
        var bill = await FinalizedBillAsync(billing);
        var started = await billing.PostAsync<PosSaleDto>("/api/v1/billing/pos/sales", new StartPosSaleRequest(bill.Id, null, bill.DueAmount, "OPD-7", PaymentMode.Upi));

        // Within the timeout (15 minutes unless the facility says otherwise) the job only asks.
        await AgeAsync(started, TimeSpan.FromMinutes(3), terminalToo: false);
        await RecheckAsync();
        (await SaleAsync(started.Id)).Status.ShouldBe(PosTransactionStatus.AwaitingCustomer);

        await AgeAsync(started, TimeSpan.FromMinutes(16), terminalToo: false);
        await RecheckAsync();
        var sale = await SaleAsync(started.Id);
        sale.Status.ShouldBe(PosTransactionStatus.TimedOut);
        sale.CompletedAt.ShouldNotBeNull();
        sale.Message.ShouldNotBeNull().ShouldContain("15 minutes");
        sale.ReceiptId.ShouldBeNull();
        (await SimulatorStatusAsync(sale.TransactionId)).ShouldBe(SimulatorTransactionStatus.Cancelled, "the terminal stops waiting for the customer");

        // Done once: the next run leaves it alone, and the counter sees the outcome.
        await RecheckAsync();
        (await SaleAsync(started.Id)).Version.ShouldBe(sale.Version);
        (await billing.GetAsync<PosSaleDto>($"/api/v1/billing/pos/sales/{started.Id}")).Status.ShouldBe(PosTransactionStatus.TimedOut);
        (await billing.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}")).DueAmount.ShouldBe(bill.DueAmount);
    }

    private async Task RecheckAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PosRecheckRunner>().RecheckSalesAsync(Ct);
    }

    /// <summary>Moves the sale's start back (and the training terminal's, which approves by its own clock).</summary>
    private async Task AgeAsync(PosSaleDto sale, TimeSpan age, bool terminalToo)
    {
        var startedAt = DateTimeOffset.UtcNow - age;
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        await db.Set<PosTransaction>().IgnoreQueryFilters().Where(s => s.Id == sale.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, startedAt), Ct);
        if (terminalToo)
        {
            await db.Set<SimulatorTransaction>().Where(t => t.ExternalId == sale.TransactionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, startedAt), Ct);
        }
    }

    private async Task<PosTransaction> SaleAsync(Guid id)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<PosTransaction>().IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == id, Ct);
    }

    private async Task<int> ReceiptsForAsync(string transactionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<Receipt>().IgnoreQueryFilters()
            .CountAsync(r => r.Payments.Any(p => p.ExternalId == transactionId), Ct);
    }

    private async Task<SimulatorTransactionStatus> SimulatorStatusAsync(string transactionId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<SimulatorTransaction>().AsNoTracking()
            .Where(t => t.ExternalId == transactionId).Select(t => t.Status).SingleAsync(Ct);
    }

    private static async Task OpenCounterShiftAsync(ApiClient billing)
    {
        using var current = await billing.SendRawAsync(HttpMethod.Get, "/api/v1/billing/shifts/current");
        if (current.StatusCode == HttpStatusCode.NoContent)
        {
            await billing.PostAsync<ShiftDto>("/api/v1/billing/shifts/open", new OpenShiftRequest("OP counter 1", OpeningFloat: 0m));
        }
    }

    private async Task<BillDetail> FinalizedBillAsync(ApiClient billing)
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var surname = "Pos" + new string(Enumerable.Range(0, 8).Select(_ => (char)('a' + Random.Shared.Next(26))).ToArray());
        var registered = await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mrs.", FirstName: "Lata", MiddleName: null, LastName: surname, Gender: Gender.Female,
            DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 40)), AgeYears: null, AgeMonths: null,
            Phone: "7" + string.Concat(Enumerable.Range(0, 9).Select(_ => Random.Shared.Next(10))), AlternatePhone: null, Email: null, Address: null,
            BloodGroup: null, MaritalStatus: null, GuardianName: null, GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null,
            EmergencyContactRelation: null, Occupation: null, Religion: null, Nationality: null, PreferredLanguage: null, Category: null,
            AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null, PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false));
        var service = (await billing.GetAsync<List<ServiceItemDto>>("/api/v1/masters/lookups/services?search=REG-NEW")).Single(s => s.Code == "REG-NEW");
        await billing.PostAsync<ChargeDto>("/api/v1/billing/charges", new PostChargeRequest(registered.Id, null, null, service.Id, 1m, null, null, null, null, null));
        var draft = await billing.PostAsync<BillDetail>("/api/v1/billing/bills", new CreateBillRequest(null, registered.Id, null));
        var bill = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        bill.DueAmount.ShouldBeGreaterThan(0m);
        return bill;
    }

    /// <summary>Card sales go to the training terminal for the rest of the test; then the setting is put back.</summary>
    private async Task<IAsyncDisposable> TerminalSimulatorAsync(int approveAfterSeconds)
    {
        const string key = "integrations.pos";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await admin.PutAsync($"/api/v1/admin/settings/{key}", new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["simulatorApproveAfterSeconds"] = approveAfterSeconds }, null, false, null));
        return new Restore(() => admin.PutAsync($"/api/v1/admin/settings/{key}",
            new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null)));
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
