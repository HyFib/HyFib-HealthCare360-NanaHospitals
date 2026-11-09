using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.VendorAccounts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Modules.SupplyChain.Services;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.SharedKernel;
using VendorDebitNoteDto = HyFib.HealthCare360.Modules.SupplyChain.Features.VendorAccounts.DebitNoteDto;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// GSTR-3B table 4 over the real API: a goods receipt adds its GST to the month's input tax credit, and goods sent back to
/// the supplier on a posted debit note take theirs off again, in 4(A)(5).
/// </summary>
public sealed class GstInputCreditTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Goods_sent_back_on_a_debit_note_reduce_the_months_input_tax_credit_by_the_notes_gst()
    {
        await using var einvoicing = await server.EInvoicingAsync(EInvoicing.Today().AddDays(-1), Ct);
        var store = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var checker = await server.SignInAsync(HospitalServer.Users.Admin);
        var accountant = await server.SignInAsync(HospitalServer.Users.Accountant);
        var month = $"{EInvoicing.Today():yyyy-MM}";
        var central = (await store.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "CS").Id;
        var vendor = (await store.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD").Id;
        var item = await MedicineAsync(store);

        // Ten tablets at 20, GST 5%: 200 taxable and 10 of GST, within Tamil Nadu (the hospital and the supplier are both 33).
        var before = await ItcAsync(accountant, month);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        await GoodsReceipts.IntoStockAsync(server, store, new CreateGoodsReceiptRequest(
            vendor, central, null, $"INV-{AbdmExportTests.Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(item, null, $"B{AbdmExportTests.Letters(8)}", null, today.AddYears(2), 10m, 0m, 20m, 40m, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: true));
        var received = await ItcAsync(accountant, month);
        Delta(received, before).ShouldBe(new GstReturnAmounts(200m, 0m, 5m, 5m, 0m));

        // Four go back to the supplier: 80 taxable and 4 of GST. Raised, it changes nothing; posted, it comes off the credit.
        var batch = (await store.GetAsync<List<BatchStock>>($"/api/v1/inventory/stock/batches?storeId={central}&itemId={item}&includeEmpty=true")).ShouldHaveSingleItem();
        var asked = await store.PostAsync<ApprovalRequestDto>("/api/v1/purchase/debit-notes", new RaiseDebitNoteRequest(
            vendor, central, null, $"RA-{AbdmExportTests.Letters(6)}", "Damaged in transit", [new DebitNoteLineInput(batch.BatchId, 4m)]));
        (await ItcAsync(accountant, month)).ShouldBe(received, "a debit note waiting for approval is not yet a return");

        await MakerChecker.ApproveAsync(checker, await checker.GetAsync<ApprovalRequestDto>($"/api/v1/approvals/{asked.Id}"));
        var note = await store.GetAsync<VendorDebitNoteDto>($"/api/v1/purchase/debit-notes/{asked.SubjectId}");
        (note.Status, note.TaxableAmount, note.GstAmount).ShouldBe((VendorDocumentStatus.Posted, 80m, 4m));

        var gstr3b = await accountant.GetAsync<Gstr3bReturn>($"/api/v1/billing/gst-returns/gstr3b?month={month}");
        var returned = Delta(gstr3b.ItcAllOther, received);
        returned.ShouldBe(new GstReturnAmounts(-80m, 0m, -2m, -2m, 0m));
        (returned.Igst + returned.Cgst + returned.Sgst).ShouldBe(-note.GstAmount, "the credit comes down by the note's CGST, SGST and IGST");
        gstr3b.Notes.ShouldContain(n => n.Contains("return(s) to suppliers", StringComparison.Ordinal));
    }

    private static async Task<GstReturnAmounts> ItcAsync(ApiClient accountant, string month) =>
        (await accountant.GetAsync<Gstr3bReturn>($"/api/v1/billing/gst-returns/gstr3b?month={month}")).ItcAllOther;

    private static GstReturnAmounts Delta(GstReturnAmounts after, GstReturnAmounts before) => after.Plus(before.Times(-1m));

    /// <summary>A medicine counted and bought by its base unit, carrying GST at 5%.</summary>
    private static async Task<Guid> MedicineAsync(ApiClient store) =>
        (await store.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{AbdmExportTests.Letters(10)}", Name: $"Paracetamol 500 mg Tablet {AbdmExportTests.Letters(6)}", GenericName: "Paracetamol", BrandName: null,
            Type: ItemType.Drug, CategoryId: null, Strength: "500 mg", DosageForm: null, PackSize: null, BaseUnit: "TAB", PurchaseUnit: "TAB", PurchaseUnitFactor: 1,
            HsnCode: "300490", GstRate: 5m, Schedule: DrugSchedule.Otc, IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential, ReorderLevel: 0,
            ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0, Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
        .GetProperty("id").GetGuid();
}
