using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Services;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Registers;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Indents;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Reports;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stock;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Transfers;
using HyFib.HealthCare360.Modules.SupplyChain.Features.VendorAccounts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The store's second-person controls (MAKER-CHECKER slice 13), over the real API: indents, transfers counted in at the
/// other end, recalled batches released, per-store reorder levels, the recall trace to patients, and purchase returns and
/// supplier payable adjustments that reach Tally only once someone else approves them.
/// <para>The hospital administrator keeps the stores; the Super Administrator (<see cref="HospitalServer.Users.Admin"/>) is
/// the second person who holds the same store and approval permissions.</para>
/// </summary>
public sealed class StoreControlsTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Whoever_raised_an_indent_neither_approves_nor_issues_it()
    {
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var other = await server.SignInAsync(HospitalServer.Users.Admin);
        var central = await StoreAsync(store, "CS");
        var icu = await StoreAsync(store, "ICUS");
        var item = await MedicineAsync(store, DrugSchedule.Otc);
        await ReceiveAsync(store, central, item, 20m, 10m);

        var indent = await store.PostAsync<JsonElement>("/api/v1/inventory/indents",
            new CreateIndentRequest(central, icu, null, IndentPriority.Routine, null, [new IndentLineInput(item, 5m, null)]));
        var id = indent.GetProperty("id").GetGuid();

        using (var own = await store.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/indents/{id}/approve", new ApproveIndentRequest(null)))
        {
            (own.StatusCode, await CodeAsync(own)).ShouldBe((HttpStatusCode.Forbidden, Indent.ApproveOwnCode));
        }

        await other.PostAsync($"/api/v1/inventory/indents/{id}/approve", new ApproveIndentRequest(null));
        var line = (await other.GetAsync<IndentDetail>($"/api/v1/inventory/indents/{id}")).Lines.ShouldHaveSingleItem();
        var issue = new IssueIndentRequest([new IndentIssueLineInput(line.Id, 5m, null)], "ICU nurse", null);
        using (var own = await store.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/indents/{id}/issue", issue))
        {
            (own.StatusCode, await CodeAsync(own)).ShouldBe((HttpStatusCode.Forbidden, Indent.IssueOwnCode));
        }

        await other.PostAsync<JsonElement>($"/api/v1/inventory/indents/{id}/issue", issue);
        (await other.GetAsync<IndentDetail>($"/api/v1/inventory/indents/{id}")).Status.ShouldBe(IndentStatus.Issued);
    }

    [Fact]
    public async Task A_transfer_is_received_by_someone_else_and_a_shortfall_waits_for_a_third_person()
    {
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var other = await server.SignInAsync(HospitalServer.Users.Admin);
        var central = await StoreAsync(store, "CS");
        var op = await StoreAsync(store, "OPPH");
        var item = await MedicineAsync(store, DrugSchedule.Otc);
        await ReceiveAsync(store, central, item, 30m, 10m);

        // Received in full by the pharmacy: booked in at once.
        var full = await DispatchAsync(store, central, op, item, 10m);
        using (var own = await store.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/transfers/{full}/receive", new ReceiveTransferRequest(null, null)))
        {
            (own.StatusCode, await CodeAsync(own)).ShouldBe((HttpStatusCode.Forbidden, StockTransfer.ReceiveOwnCode));
        }

        using (var received = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/transfers/{full}/receive", new ReceiveTransferRequest(null, null)))
        {
            received.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await pharmacist.GetAsync<TransferDetail>($"/api/v1/inventory/transfers/{full}")).Status.ShouldBe(TransferStatus.Received);

        // Counted short: nothing moves until someone who approves adjustments, and did not send it, decides.
        var shortTransfer = await DispatchAsync(store, central, op, item, 10m);
        var line = (await pharmacist.GetAsync<TransferDetail>($"/api/v1/inventory/transfers/{shortTransfer}")).Lines.ShouldHaveSingleItem();
        using (var over = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/transfers/{shortTransfer}/receive",
                   new ReceiveTransferRequest([new ReceivedLineInput(line.Id, 11m)], null)))
        {
            (over.StatusCode, await CodeAsync(over)).ShouldBe((HttpStatusCode.UnprocessableEntity, TransferService.OverReceivedCode));
        }

        var asked = await pharmacist.PostAsync<ApprovalRequestDto>($"/api/v1/inventory/transfers/{shortTransfer}/receive",
            new ReceiveTransferRequest([new ReceivedLineInput(line.Id, 8m)], "Two strips crushed in the box"));
        asked.Kind.ShouldBe(TransferService.ShortfallKind);
        (await pharmacist.GetAsync<TransferDetail>($"/api/v1/inventory/transfers/{shortTransfer}")).Status.ShouldBe(TransferStatus.InTransit);

        // The administrator sent it: they may not decide what arrived.
        using (var dispatcher = await store.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            (dispatcher.StatusCode, await CodeAsync(dispatcher)).ShouldBe((HttpStatusCode.Forbidden, StockTransfer.ShortfallOwnCode));
        }

        var waiting = await other.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}");
        waiting.Status.ShouldBe(ApprovalStatus.Pending);
        await MakerChecker.ApproveAsync(other, waiting);

        var received2 = await pharmacist.GetAsync<TransferDetail>($"/api/v1/inventory/transfers/{shortTransfer}");
        received2.Status.ShouldBe(TransferStatus.Received);
        received2.Lines.Single().ReceivedQuantity.ShouldBe(8m);

        // The pharmacy's ledger: 10 in, then 10 in and 2 written off short.
        (await LedgerAsync(item, op)).Select(l => (l.MovementType, l.Quantity, l.BalanceAfter)).ShouldBe([
            (StockMovementType.TransferIn, 10m, 10m),
            (StockMovementType.TransferIn, 10m, 20m),
            (StockMovementType.Adjustment, -2m, 18m),
        ]);
        var audit = await AuditAsync("inventory.transfer_shortfall_accepted", shortTransfer);
        audit.Summary.ShouldNotBeNull().ShouldContain("sent 10, received 8");
    }

    [Fact]
    public async Task The_receiver_of_a_short_transfer_does_not_approve_its_shortfall()
    {
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var op = await StoreAsync(store, "OPPH");
        var ip = await StoreAsync(store, "IPPH");
        var item = await MedicineAsync(store, DrugSchedule.Otc);
        await ReceiveAsync(store, op, item, 10m, 10m);

        // The pharmacist sends it; the administrator counts it short at the IP pharmacy and may not approve their own count.
        var transfer = await DispatchAsync(pharmacist, op, ip, item, 6m);
        var line = (await store.GetAsync<TransferDetail>($"/api/v1/inventory/transfers/{transfer}")).Lines.Single();
        var asked = await store.PostAsync<ApprovalRequestDto>($"/api/v1/inventory/transfers/{transfer}/receive",
            new ReceiveTransferRequest([new ReceivedLineInput(line.Id, 5m)], null));
        using var self = await store.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version));
        (self.StatusCode, await CodeAsync(self)).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
    }

    [Fact]
    public async Task A_recalled_batch_is_released_only_with_a_second_persons_approval_and_a_quality_hold_at_once()
    {
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var op = await StoreAsync(store, "OPPH");
        var item = await MedicineAsync(store, DrugSchedule.Otc);
        await ReceiveAsync(store, op, item, 10m, 10m);
        var batch = await BatchAsync(store, op, item);

        // A local quality hold is released at once.
        await store.PostAsync($"/api/v1/inventory/stock/batches/{batch.BatchId}/block", new BlockBatchRequest("Label smudged; checking"));
        using (var released = await store.SendRawAsync(HttpMethod.Post, $"/api/v1/inventory/stock/batches/{batch.BatchId}/unblock", new UnblockBatchRequest(null)))
        {
            released.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        // A recall waits for someone else who may release recalls.
        await store.PostAsync($"/api/v1/inventory/stock/batches/{batch.BatchId}/block", new BlockBatchRequest("Manufacturer recall RC-114", Recall: true));
        (await BatchAsync(store, op, item)).IsRecalled.ShouldBeTrue();
        var asked = await store.PostAsync<ApprovalRequestDto>($"/api/v1/inventory/stock/batches/{batch.BatchId}/unblock",
            new UnblockBatchRequest("Recall withdrawn by the manufacturer's letter of 3 November"));
        asked.Kind.ShouldBe(RecallReleaseApprovals.Kind);
        (await BatchAsync(store, op, item)).IsBlocked.ShouldBeTrue();
        using (var self = await store.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            (self.StatusCode, await CodeAsync(self)).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        }

        await MakerChecker.ApproveAsync(pharmacist, await pharmacist.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}"));
        var after = await BatchAsync(store, op, item);
        (after.IsBlocked, after.IsRecalled).ShouldBe((false, false));
        (await AuditAsync("stock_batch.recall_released", batch.BatchId)).Summary.ShouldNotBeNull().ShouldContain("recalled (Manufacturer recall RC-114) to released for use");
    }

    [Fact]
    public async Task A_stores_own_reorder_level_overrides_the_items_and_every_change_is_audited()
    {
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var central = await StoreAsync(store, "CS");
        var icu = await StoreAsync(store, "ICUS");
        var item = await MedicineAsync(store, DrugSchedule.Otc);
        await ReceiveAsync(store, central, item, 10m, 10m);

        // The item has no level of its own, so no store reorders it.
        (await ReorderAsync(store, central)).ShouldNotContain(r => r.ItemId == item);

        await store.PutAsync($"/api/v1/inventory/stores/{central}/levels/{item}", new SaveStoreItemLevelRequest(25m, 40m, 5m, 60m, null));
        var due = (await ReorderAsync(store, central)).Where(r => r.ItemId == item).ShouldHaveSingleItem();
        var levelId = await LevelIdAsync(central, item);
        (due.ReorderLevel, due.MaximumStock, due.SuggestedQuantity).ShouldBe((25m, 60m, 50m));
        (await ReorderAsync(store, icu)).ShouldNotContain(r => r.ItemId == item);
        var flagged = await store.GetAsync<PagedResult<StockSummaryRow>>($"/api/v1/inventory/stock?storeId={central}&belowReorder=true&pageSize=200");
        flagged.Items.ShouldContain(r => r.ItemId == item && r.BelowReorder && r.ReorderLevel == 25m);

        var level = (await store.GetAsync<List<StoreItemLevelDto>>($"/api/v1/inventory/stores/{central}/levels")).Single(l => l.ItemId == item);
        await store.PutAsync($"/api/v1/inventory/stores/{central}/levels/{item}", new SaveStoreItemLevelRequest(5m, 40m, 0m, 60m, level.Version));
        (await ReorderAsync(store, central)).ShouldNotContain(r => r.ItemId == item);

        using (var removed = await store.SendRawAsync(HttpMethod.Delete, $"/api/v1/inventory/stores/{central}/levels/{item}"))
        {
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var trail = await AuditsAsync(levelId);
        trail.Select(a => a.Action).ShouldBe(["stock_level.saved", "stock_level.saved", "stock_level.removed"]);
        trail[1].Summary.ShouldNotBeNull().ShouldContain("reorder at 5");
        trail[1].Summary.ShouldNotBeNull().ShouldContain("was reorder at 25");
    }

    [Fact]
    public async Task The_recall_trace_lists_every_patient_who_had_the_batch_and_who_ran_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var op = await StoreAsync(store, "OPPH");
        var ip = await StoreAsync(store, "IPPH");
        var item = await MedicineAsync(store, DrugSchedule.Otc);
        var batchNumber = $"B{Letters(8)}";
        await ReceiveAsync(store, op, item, 20m, 10m, batchNumber);
        await ReceiveAsync(store, ip, item, 10m, 10m, batchNumber);

        // A registered patient and a walk-in buy at the counter; an admitted patient has it issued to the ward.
        var registered = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            op, registered.Id, null, null, null, [new CounterSaleLine(item, 3m, null)], 0m, [new SalePayment(PharmacyPaymentMode.Cash, 60m, null)], false));
        await pharmacist.PostAsync<PharmacySaleCreated>("/api/v1/pharmacy/sales/counter", new CounterSaleRequest(
            op, null, "Walk-in Ravi", "9000000001", null, [new CounterSaleLine(item, 2m, null)], 0m, [new SalePayment(PharmacyPaymentMode.Cash, 40m, null)], false));
        var inpatient = (await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice)).Id;
        var admission = await AdmitAsync(frontOffice, doctor, inpatient);
        await pharmacist.PostAsync<IpIssueCreated>("/api/v1/pharmacy/ip-issues", new IpIssueRequest(
            ip, null, null, inpatient, admission.Id, admission.VisitId, [new IpIssueLineInput(item, 4m, null, null, null, null, false)], false));

        var trace = await pharmacist.GetAsync<RecallTrace>($"/api/v1/pharmacy/recalls/trace?itemId={item}&batchNumber={batchNumber.ToLowerInvariant()}");
        trace.Patients.Select(p => (p.Source, p.PatientId, p.Kept)).ShouldBe([
            (RecallTraceSource.CounterSale, (Guid?)registered.Id, 3m),
            (RecallTraceSource.CounterSale, null, 2m),
            (RecallTraceSource.WardIssue, (Guid?)inpatient, 4m),
        ]);
        trace.Patients[1].PatientName.ShouldBe("Walk-in Ravi");
        trace.Patients[1].Phone.ShouldBe("+919000000001", "the counter keeps the number in international form");
        (trace.PatientCount, trace.QuantityWithPatients).ShouldBe((3, 9m));
        trace.Stock.Sum(s => s.OnHand).ShouldBe(21m);

        var audit = await AuditAsync("pharmacy.recall_trace", item);
        audit.UserId.ShouldBe(await UserIdAsync(pharmacist));
        audit.Category.ShouldBe(AuditCategories.PhiAccess);
        audit.Summary.ShouldNotBeNull().ShouldContain(batchNumber);
    }

    [Fact]
    public async Task A_debit_note_posts_and_reaches_tally_only_once_someone_else_approves_it()
    {
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var other = await server.SignInAsync(HospitalServer.Users.Admin);
        var central = await StoreAsync(store, "CS");
        var item = await MedicineAsync(store, DrugSchedule.Otc);
        await ReceiveAsync(store, central, item, 10m, 20m);
        var batch = await BatchAsync(store, central, item);
        var vendor = await VendorAsync(store);

        // A recalled batch goes back to the supplier: held, it still can be returned.
        await store.PostAsync($"/api/v1/inventory/stock/batches/{batch.BatchId}/block", new BlockBatchRequest("Manufacturer recall RC-201", Recall: true));
        var reference = $"RA-{Letters(6)}";
        var asked = await store.PostAsync<ApprovalRequestDto>("/api/v1/purchase/debit-notes", new RaiseDebitNoteRequest(
            vendor, central, null, reference, "Recall RC-201", [new DebitNoteLineInput(batch.BatchId, 4m)]));
        asked.Kind.ShouldBe(VendorAccountService.DebitNoteKind);
        var note = await store.GetAsync<DebitNoteDto>($"/api/v1/purchase/debit-notes/{asked.SubjectId}");
        (note.Status, note.TaxableAmount, note.GstAmount, note.TotalAmount).ShouldBe((VendorDocumentStatus.Draft, 80m, 4m, 84m));
        note.ApprovalWaitingSince.ShouldNotBeNull();
        (await LedgerAsync(item, central)).ShouldNotContain(l => l.MovementType == StockMovementType.PurchaseReturn);
        (await VouchersAsync()).ShouldNotContain(v => v.Number == note.Number);

        using (var self = await store.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            (self.StatusCode, await CodeAsync(self)).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        }

        await MakerChecker.ApproveAsync(other, await other.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}"));
        (await store.GetAsync<DebitNoteDto>($"/api/v1/purchase/debit-notes/{note.Id}")).Status.ShouldBe(VendorDocumentStatus.Posted);
        (await LedgerAsync(item, central)).Last().ShouldSatisfyAllConditions(
            l => l.MovementType.ShouldBe(StockMovementType.PurchaseReturn),
            l => l.Quantity.ShouldBe(-4m),
            l => l.BalanceAfter.ShouldBe(6m));

        var voucher = (await VouchersAsync()).Single(v => v.Number == note.Number);
        voucher.VoucherType.ShouldBe("Debit Note");
        voucher.Narration.ShouldContain(reference);
        voucher.Entries.Where(e => e.IsDebit).Sum(e => e.Amount).ShouldBe(84m);
        voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount).ShouldBe(84m);
        voucher.Entries.ShouldContain(e => !e.IsDebit && e.LedgerName == "Purchase Returns" && e.Amount == 80m);
        (await AuditAsync("purchase.debit_note_posted", note.Id)).Summary.ShouldNotBeNull().ShouldContain("approved by");
    }

    [Fact]
    public async Task A_supplier_payable_adjustment_posts_as_a_journal_only_once_someone_else_approves_it()
    {
        var purchase = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var other = await server.SignInAsync(HospitalServer.Users.Admin);
        var vendor = await VendorAsync(purchase);

        var asked = await purchase.PostAsync<ApprovalRequestDto>("/api/v1/purchase/payable-adjustments", new RaisePayableAdjustmentRequest(
            vendor, PayableAdjustmentDirection.Reduce, 1250m, "Rate difference on invoice SB-2291", "CN-SB-77"));
        asked.Kind.ShouldBe(VendorAccountService.AdjustmentKind);
        var listed = (await purchase.GetAsync<PagedResult<PayableAdjustmentDto>>($"/api/v1/purchase/payable-adjustments?vendorId={vendor}&pageSize=100"))
            .Items.Single(a => a.Id == asked.SubjectId);
        listed.Status.ShouldBe(VendorDocumentStatus.Draft);
        (await VouchersAsync()).ShouldNotContain(v => v.Number == listed.Number);

        using (var self = await purchase.SendRawAsync(HttpMethod.Post, $"/api/v1/approvals/{asked.Id}/approve", new ApproveRequest(null, asked.Version)))
        {
            (self.StatusCode, await CodeAsync(self)).ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        }

        await MakerChecker.ApproveAsync(other, await other.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}"));
        var voucher = (await VouchersAsync()).Single(v => v.Number == listed.Number);
        voucher.VoucherType.ShouldBe("Journal");
        voucher.Entries.ShouldContain(e => !e.IsDebit && e.LedgerName == "Purchase Rate Differences" && e.Amount == 1250m);
        voucher.Entries.ShouldContain(e => e.IsDebit && e.Amount == 1250m);
    }

    // ------------------------------------------------------------------ steps

    private static async Task<Guid> StoreAsync(ApiClient api, string code) =>
        (await api.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == code).Id;

    private static async Task<Guid> VendorAsync(ApiClient api) =>
        (await api.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD").Id;

    /// <summary>A medicine counted and bought by its base unit, carrying GST at 5%, with no reorder level of its own.</summary>
    private static async Task<Guid> MedicineAsync(ApiClient store, DrugSchedule schedule) =>
        (await store.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: $"Paracetamol 500 mg Tablet {Letters(6)}", GenericName: "Paracetamol", BrandName: null, Type: ItemType.Drug,
            CategoryId: null, Strength: "500 mg", DosageForm: null, PackSize: null, BaseUnit: "TAB", PurchaseUnit: "TAB", PurchaseUnitFactor: 1, HsnCode: "300490",
            GstRate: 5m, Schedule: schedule, IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential, ReorderLevel: 0, ReorderQuantity: 0,
            MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
        .GetProperty("id").GetGuid();

    /// <summary>The store receives <paramref name="units"/> bought at <paramref name="rate"/> each (MRP, the counter price, twice that); someone else posts it.</summary>
    private async Task ReceiveAsync(ApiClient store, Guid storeId, Guid itemId, decimal units, decimal rate, string? batchNumber = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await GoodsReceipts.IntoStockAsync(server, store, new CreateGoodsReceiptRequest(
            await VendorAsync(store), storeId, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(itemId, null, batchNumber ?? $"B{Letters(8)}", null, today.AddYears(2), units, 0m, rate, rate * 2, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: true));
    }

    private static async Task<BatchStock> BatchAsync(ApiClient api, Guid storeId, Guid itemId) =>
        (await api.GetAsync<List<BatchStock>>($"/api/v1/inventory/stock/batches?storeId={storeId}&itemId={itemId}&includeEmpty=true")).ShouldHaveSingleItem();

    private static async Task<Guid> DispatchAsync(ApiClient api, Guid from, Guid to, Guid itemId, decimal quantity) =>
        (await api.PostAsync<JsonElement>("/api/v1/inventory/transfers", new DispatchTransferRequest(from, to, null, [new TransferLineInput(itemId, quantity, null)])))
        .GetProperty("id").GetGuid();

    private static async Task<List<ReorderRow>> ReorderAsync(ApiClient api, Guid storeId) =>
        await api.GetAsync<List<ReorderRow>>($"/api/v1/inventory/reports/reorder?storeId={storeId}");

    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, "SPW");
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Lower respiratory tract infection", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    private async Task<List<StockLedgerEntry>> LedgerAsync(Guid itemId, Guid storeId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<StockLedgerEntry>().IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ItemId == itemId && l.StoreId == storeId).OrderBy(l => l.OccurredAt).ThenBy(l => l.Id).ToListAsync(Ct);
    }

    /// <summary>Today's Tally day book for the facility the administrator works in.</summary>
    private async Task<IReadOnlyList<Platform.Integrations.AccountingVoucher>> VouchersAsync()
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var facility = await db.Set<Platform.Organization.Facility>().IgnoreQueryFilters().OrderBy(f => f.CreatedAt).Select(f => f.Id).FirstAsync(Ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, HospitalTimeZone.Zone).DateTime);
        return await scope.ServiceProvider.GetRequiredService<TallyService>().BuildVouchersAsync(facility, today, Ct);
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.Action == action && a.EntityId == entityId.ToString()).OrderByDescending(a => a.OccurredAt).FirstAsync(Ct);
    }

    private async Task<List<AuditEvent>> AuditsAsync(Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .Where(a => a.EntityId == entityId.ToString() && a.Action.StartsWith("stock_level.")).OrderBy(a => a.OccurredAt).ThenBy(a => a.Id).ToListAsync(Ct);
    }

    /// <summary>The store's own level row for the item (the audit trail is kept against it).</summary>
    private async Task<Guid> LevelIdAsync(Guid storeId, Guid itemId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<StoreItemLevel>().IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.StoreId == storeId && l.ItemId == itemId).Select(l => l.Id).SingleAsync(Ct);
    }

    private static async Task<Guid> UserIdAsync(ApiClient user) => (await user.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("id").GetGuid();

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var problem = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
