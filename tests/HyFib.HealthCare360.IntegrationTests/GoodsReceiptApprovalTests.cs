using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Purchase;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Goods go into stock only when someone other than whoever entered the receipt checks it against the supplier's invoice
/// and approves it (maker-checker): the store keeper receives, a purchase officer posts. The checker sees what the invoice
/// says it comes to beside what the lines come to, and approves the receipt as it was sent, or nothing goes in.
/// </summary>
public sealed class GoodsReceiptApprovalTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Goods_go_into_stock_only_when_someone_other_than_whoever_entered_them_approves_the_receipt()
    {
        var (keeper, checker) = await PeopleAsync("it-grn-keeper", "it-grn-checker");
        var (request, storeId, itemId, batchNumber) = await ReceiptAsync();

        // The store keeper enters ten packs at ₹50 + 5 % GST (₹525 in all) from an invoice printed as ₹525.40, and sends it.
        var created = await keeper.PostAsync<GoodsReceiptCreated>(GoodsReceipts.Path, request with { SendForApproval = true, VendorInvoiceAmount = 525.40m });
        created.Status.ShouldBe(GoodsReceiptStatus.Draft);
        var approvalId = created.ApprovalId.ShouldNotBeNull();

        // Nothing is in stock while it waits, and the receipt says it waits.
        (await BatchesAsync(storeId, itemId)).ShouldNotContain(b => b.BatchNumber == batchNumber);
        var waiting = await keeper.GetAsync<GoodsReceiptDetail>($"{GoodsReceipts.Path}/{created.Id}");
        (waiting.Status, waiting.VendorInvoiceAmount, waiting.NetAmount).ShouldBe((GoodsReceiptStatus.Draft, 525.40m, 525m));
        waiting.ApprovalWaitingSince.ShouldNotBeNull();

        // The checker sees the invoice as printed beside what the lines come to.
        var asked = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{approvalId}");
        (asked.Kind, asked.Status, asked.SubjectId).ShouldBe(("purchase.grn", ApprovalStatus.Pending, created.Id));
        Detail(asked, "Total worked out from the lines").ShouldBe("₹525.00");
        Detail(asked, "Invoice total as printed").ShouldBe("₹525.40");
        Detail(asked, "Difference").ShouldBe("-₹0.40");

        // A store keeper may not post receipts at all; the purchase officer approves, and the batch is in stock.
        (await RefusalAsync(keeper, $"/api/v1/approvals/{approvalId}/approve", new ApproveRequest("Mine.", asked.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));
        (await GoodsReceipts.ApproveAsync(checker, approvalId)).Status.ShouldBe(ApprovalStatus.Approved);

        var posted = await keeper.GetAsync<GoodsReceiptDetail>($"{GoodsReceipts.Path}/{created.Id}");
        (posted.Status, posted.ApprovalWaitingSince).ShouldBe((GoodsReceiptStatus.Posted, (DateTimeOffset?)null));
        posted.Lines.ShouldAllBe(l => l.StockBatchId != null);
        (await BatchesAsync(storeId, itemId)).Single(b => b.BatchNumber == batchNumber).OnHand.ShouldBe(10m);

        // A posted receipt is not sent again.
        (await RefusalAsync(keeper, $"{GoodsReceipts.Path}/{created.Id}/approval", new { }))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "grn.not_draft"));
    }

    [Fact]
    public async Task Whoever_entered_a_receipt_never_posts_it_and_a_receipt_changed_after_it_was_sent_posts_nothing()
    {
        var (keeper, checker) = await PeopleAsync("it-grn-keeper2", "it-grn-checker2");
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);

        // The administrator, who may also approve receipts, enters a draft; the store keeper sends it for approval.
        var (draft, storeId, itemId, batchNumber) = await ReceiptAsync();
        var entered = await admin.PostAsync<GoodsReceiptCreated>(GoodsReceipts.Path, draft);
        (entered.Status, entered.ApprovalId).ShouldBe((GoodsReceiptStatus.Draft, (Guid?)null));
        var sent = await keeper.PostAsync<ApprovalRequestDto>($"{GoodsReceipts.Path}/{entered.Id}/approval", new { });

        // Not the store keeper who asked, nor the administrator who entered it: the request waits for someone else.
        (await RefusalAsync(admin, $"/api/v1/approvals/{sent.Id}/approve", new ApproveRequest("Checked.", sent.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "grn.post_own"));
        (await admin.GetAsync<GoodsReceiptDetail>($"{GoodsReceipts.Path}/{entered.Id}")).Status.ShouldBe(GoodsReceiptStatus.Draft);
        (await BatchesAsync(storeId, itemId)).ShouldNotContain(b => b.BatchNumber == batchNumber);
        (await GoodsReceipts.ApproveAsync(checker, sent.Id)).Status.ShouldBe(ApprovalStatus.Approved);
        (await admin.GetAsync<GoodsReceiptDetail>($"{GoodsReceipts.Path}/{entered.Id}")).Status.ShouldBe(GoodsReceiptStatus.Posted);

        // A receipt cancelled after it was sent: approving it puts nothing into stock.
        var (second, _, _, secondBatch) = await ReceiptAsync(storeId, itemId);
        var withdrawn = await keeper.PostAsync<GoodsReceiptCreated>(GoodsReceipts.Path, second with { SendForApproval = true });
        using (var cancelled = await admin.SendRawAsync(HttpMethod.Post, $"{GoodsReceipts.Path}/{withdrawn.Id}/cancel", new InventoryReasonRequest("Entered twice.")))
        {
            cancelled.EnsureSuccessStatusCode();
        }

        var approval = await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{withdrawn.ApprovalId.ShouldNotBeNull()}");
        (await RefusalAsync(checker, $"/api/v1/approvals/{approval.Id}/approve", new ApproveRequest("Checked.", approval.Version)))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "approval.stale"));
        (await admin.GetAsync<GoodsReceiptDetail>($"{GoodsReceipts.Path}/{withdrawn.Id}")).Status.ShouldBe(GoodsReceiptStatus.Cancelled);
        (await BatchesAsync(storeId, itemId)).ShouldNotContain(b => b.BatchNumber == secondBatch);
    }

    /// <summary>A store keeper who receives goods, and a purchase officer who checks receipts and posts them.</summary>
    private async Task<(ApiClient Keeper, ApiClient Checker)> PeopleAsync(string keeper, string checker)
    {
        await server.CreateStaffAccountAsync(keeper, SystemRoles.StoreKeeper);
        await server.CreateStaffAccountAsync(checker, SystemRoles.PurchaseOfficer);
        return (await server.SignInAsync(keeper), await server.SignInAsync(checker));
    }

    /// <summary>
    /// A receipt of ten packs of a medicine of the test's own into the OP pharmacy, at ₹50 a pack with 5 % GST and an MRP
    /// of ₹80, in a batch of its own; a new item unless one is given.
    /// </summary>
    private async Task<(CreateGoodsReceiptRequest Request, Guid StoreId, Guid ItemId, string BatchNumber)> ReceiptAsync(Guid? storeId = null, Guid? itemId = null)
    {
        var admin = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var store = storeId ?? (await admin.GetAsync<List<StoreDto>>("/api/v1/inventory/stores?type=Pharmacy")).Single(s => s.Code == "OPPH").Id;
        var item = itemId ?? (await admin.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: $"Cetirizine 10 mg Tablet {Letters(6)}", GenericName: "Cetirizine", BrandName: null, Type: ItemType.Drug,
            CategoryId: null, Strength: "10 mg", DosageForm: "Tablet", PackSize: "Strip of 10", BaseUnit: "TAB", PurchaseUnit: "TAB", PurchaseUnitFactor: 1,
            HsnCode: "300490", GstRate: 5m, Schedule: DrugSchedule.Otc, IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential,
            ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
            .GetProperty("id").GetGuid();
        var vendor = (await admin.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var batchNumber = $"GRN{Letters(8)}";
        return (new CreateGoodsReceiptRequest(
            vendor.Id, store, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(item, null, batchNumber, null, today.AddYears(2), 10m, 0m, 50m, 80m, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: false), store, item, batchNumber);
    }

    private async Task<List<BatchStock>> BatchesAsync(Guid storeId, Guid itemId) =>
        await (await server.SignInAsync(HospitalServer.Users.Pharmacist)).GetAsync<List<BatchStock>>($"/api/v1/pharmacy/stock/batches?storeId={storeId}&itemId={itemId}");

    private static string Detail(ApprovalRequestDto request, string label) => request.Details.Single(d => d.Label == label).Value;

    private static async Task<(HttpStatusCode Status, string? Code)> RefusalAsync(ApiClient api, string path, object body)
    {
        using var response = await api.SendRawAsync(HttpMethod.Post, path, body);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return (response.StatusCode, problem.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
