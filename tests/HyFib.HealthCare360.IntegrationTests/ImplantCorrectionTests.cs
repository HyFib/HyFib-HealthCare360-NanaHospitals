using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SpecialtyCare.Services;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Security;
using HyFib.HealthCare360.SharedKernel;
using BillingChargeStatus = HyFib.HealthCare360.Modules.Billing.Domain.ChargeStatus;
using OtChargeStatus = HyFib.HealthCare360.Modules.SpecialtyCare.Domain.Common.ChargeStatus;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Implants and consumables once the operation has started (maker-checker slice 10), over the real API and PostgreSQL: a line
/// is no longer removed; a wrong batch, serial or quantity is corrected, the original kept, and the correction waits for the
/// OT in-charge, never the one who asked. Approved, the charge and the stock follow through their usual paths. Before the
/// operation, a line entered by mistake is still removed at once.
/// </summary>
public sealed class ImplantCorrectionTests(HospitalServer server)
{
    private const string Approvals = "/api/v1/approvals";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After_the_operation_starts_an_implant_is_corrected_not_removed_and_the_ot_in_charge_approves_it()
    {
        var cases = new TheatreCases(server);
        var billing = await server.SignInAsync(Users.Billing);
        var coordinator = await CoordinatorAsync();
        var c = await cases.UnderWayAsync(Laterality.Right, "Inguinal hernia repair with mesh");

        // The coordinator records the mesh, brought on consignment, with the wrong lot and two pieces.
        var mesh = await coordinator.PostAsync<SurgeryMaterialDto>(Materials(c), new AddSurgeryMaterialRequest(
            MaterialKind.Implant, null, "Polypropylene mesh", "Ethicon", "PM2611", null, null, 2m, 4_500m, "Prolene", "Polypropylene, 15 x 15 cm", 5_200m));

        // It cannot be removed now: it was used, charged and is in the patient.
        (await TheatreCases.RefusalAsync(coordinator, HttpMethod.Delete, $"{Materials(c)}/{mesh.Id}", null))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.material_locked"));

        // A correction needs a reason and a change; then it waits, and the line stays as it was meanwhile.
        (await TheatreCases.RefusalAsync(coordinator, HttpMethod.Post, $"{Materials(c)}/{mesh.Id}/corrections",
            new CorrectSurgeryMaterialRequest("PM2611", null, null, 2m, "Nothing really"))).ShouldBe((HttpStatusCode.BadRequest, "validation"));
        var asked = await TheatreCases.AcceptedAsync(coordinator, HttpMethod.Post, $"{Materials(c)}/{mesh.Id}/corrections",
            new CorrectSurgeryMaterialRequest("PM2616", null, null, 1m, "One piece used; lot misread from the pack"));
        asked.Kind.ShouldBe(OtClinicalService.CorrectionKind);
        asked.Summary.ShouldContain("lot PM2611 → PM2616; quantity 2 → 1");
        var waiting = (await coordinator.GetAsync<List<SurgeryMaterialDto>>(Materials(c))).ShouldHaveSingleItem();
        (waiting.LotNumber, waiting.Quantity, waiting.PendingCorrection?.ApprovalId).ShouldBe(("PM2611", 2m, asked.Id));
        (await TheatreCases.RefusalAsync(coordinator, HttpMethod.Post, $"{Materials(c)}/{mesh.Id}/corrections",
                new CorrectSurgeryMaterialRequest("PM2616", null, null, 2m, "Again")))
            .ShouldBe((HttpStatusCode.Conflict, "approval.already_requested"));

        // The coordinator, who may approve corrections, does not approve her own; the nurse may not approve any.
        var request = await coordinator.GetAsync<ApprovalRequestDto>($"{Approvals}/{asked.Id}");
        (await TheatreCases.RefusalAsync(coordinator, HttpMethod.Post, $"{Approvals}/{asked.Id}/approve", new ApproveRequest(null, request.Version)))
            .ShouldBe((HttpStatusCode.Forbidden, "approval.self"));
        (await TheatreCases.RefusalAsync(await server.SignInAsync(Users.Nurse), HttpMethod.Post, $"{Approvals}/{asked.Id}/approve",
            new ApproveRequest(null, request.Version))).ShouldBe((HttpStatusCode.Forbidden, "approval.not_permitted"));

        // The OT in-charge (here the hospital administrator) approves: the line is kept as corrected, and one piece of the right lot replaces it.
        await server.ApproveMaterialCorrectionAsync(asked.Id);
        var lines = await coordinator.GetAsync<List<SurgeryMaterialDto>>(Materials(c));
        lines.Count.ShouldBe(2);
        var original = lines.Single(m => m.Id == mesh.Id);
        var corrected = lines.Single(m => m.Id != mesh.Id);
        (original.LotNumber, original.Quantity, original.SupersededByMaterialId, original.ChargeStatus).ShouldBe(("PM2611", 2m, corrected.Id, OtChargeStatus.Cancelled));
        original.CorrectionReason.ShouldBe("One piece used; lot misread from the pack");
        original.CorrectionApprovedByName.ShouldNotBeNull();
        (corrected.LotNumber, corrected.Quantity, corrected.CorrectsMaterialId, corrected.ChargeStatus, corrected.Brand)
            .ShouldBe(("PM2616", 1m, mesh.Id, OtChargeStatus.Posted, "Prolene"));

        // The bill follows: the charge for two is withdrawn, one is charged.
        var charges = (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?visitId={c.VisitId}&pageSize=200")).Items
            .Where(ch => ch.ServiceCode == "OT-IMPLANT").ToList();
        charges.Single(ch => ch.SourceReference.EndsWith(mesh.Id.ToString(), StringComparison.Ordinal)).Status.ShouldBe(BillingChargeStatus.Cancelled);
        var now = charges.Single(ch => ch.SourceReference.EndsWith(corrected.Id.ToString(), StringComparison.Ordinal));
        (now.Quantity, now.UnitPrice).ShouldBe((1m, 4_500m));
        (await cases.AuditAsync(c.PatientId, "ot.material_corrected")).Summary.ShouldContain("approved by");

        // The corrected line is not corrected again, nor removed.
        (await TheatreCases.RefusalAsync(coordinator, HttpMethod.Post, $"{Materials(c)}/{mesh.Id}/corrections",
            new CorrectSurgeryMaterialRequest("PM2616", null, null, 1m, "Once more"))).ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.material_superseded"));
        (await TheatreCases.RefusalAsync(coordinator, HttpMethod.Delete, $"{Materials(c)}/{corrected.Id}", null))
            .ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.material_locked"));
    }

    [Fact]
    public async Task A_store_implant_corrected_to_another_batch_goes_back_to_stock_and_the_right_batch_is_issued()
    {
        var cases = new TheatreCases(server);
        var storeKeeper = await server.SignInAsync(Users.HospitalAdmin);
        var nurse = await server.SignInAsync(Users.Nurse);
        var name = $"Bone screw {Letters(6)}";
        var item = await ItemAsync(storeKeeper, name);
        var store = (await storeKeeper.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "OTS").Id;
        var (first, second) = ($"BS{Letters(8)}", $"BS{Letters(8)}");
        await ReceiveAsync(storeKeeper, store, item, first);
        await ReceiveAsync(storeKeeper, store, item, second);
        var c = await cases.UnderWayAsync(Laterality.Left, "Fixation of fracture of the left radius");

        var screw = await nurse.PostAsync<SurgeryMaterialDto>(Materials(c), new AddSurgeryMaterialRequest(
            MaterialKind.Implant, item, name, null, first, null, null, 1m, 900m));
        (await AvailableAsync(nurse, name, first, second)).ShouldBe((4m, 5m));

        var asked = await TheatreCases.AcceptedAsync(nurse, HttpMethod.Post, $"{Materials(c)}/{screw.Id}/corrections",
            new CorrectSurgeryMaterialRequest(second, null, null, 1m, "The screw used came from the second box"));
        await server.ApproveMaterialCorrectionAsync(asked.Id);

        (await AvailableAsync(nurse, name, first, second)).ShouldBe((5m, 4m));
        (await nurse.GetAsync<List<SurgeryMaterialDto>>(Materials(c))).Single(m => m.CorrectsMaterialId == screw.Id).LotNumber.ShouldBe(second);
    }

    [Fact]
    public async Task Before_the_operation_a_line_entered_by_mistake_is_removed_at_once_and_no_correction_is_asked()
    {
        var cases = new TheatreCases(server);
        var nurse = await server.SignInAsync(Users.Nurse);
        var c = await cases.InPreOpAsync(Laterality.NotApplicable, "Diagnostic laparoscopy");
        var gauze = await nurse.PostAsync<SurgeryMaterialDto>(Materials(c), new AddSurgeryMaterialRequest(
            MaterialKind.Consumable, null, "Gauze roll", null, null, null, null, 1m, 120m));

        (await TheatreCases.RefusalAsync(nurse, HttpMethod.Post, $"{Materials(c)}/{gauze.Id}/corrections",
            new CorrectSurgeryMaterialRequest(null, null, null, 2m, "Two rolls"))).ShouldBe((HttpStatusCode.UnprocessableEntity, "surgery.material_not_locked"));
        using (var removed = await nurse.SendRawAsync(HttpMethod.Delete, $"{Materials(c)}/{gauze.Id}", null))
        {
            removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await nurse.GetAsync<List<SurgeryMaterialDto>>(Materials(c))).ShouldBeEmpty();
    }

    private static string Materials(TheatreCases.Case c) => $"{c.Path}/materials";

    /// <summary>An OT coordinator, who records implants and may approve others' corrections, signed in as herself.</summary>
    private async Task<ApiClient> CoordinatorAsync()
    {
        var userName = "otc" + Guid.NewGuid().ToString("N")[..8];
        await server.CreateStaffAccountAsync(userName, SystemRoles.OtCoordinator);
        return await server.SignInAsync(userName);
    }

    private static async Task<Guid> ItemAsync(ApiClient storeKeeper, string name) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: name, GenericName: null, BrandName: "Synthes", Type: ItemType.Implant, CategoryId: null, Strength: null,
            DosageForm: null, PackSize: "1 piece", BaseUnit: "PCS", PurchaseUnit: "PCS", PurchaseUnitFactor: 1, HsnCode: "9021", GstRate: 5m,
            Schedule: DrugSchedule.None, IsControlled: false, IsColdChain: false, VedClass: VedClass.Vital, ReorderLevel: 0, ReorderQuantity: 0,
            MinimumStock: 0, MaximumStock: 0, Manufacturer: "DePuy Synthes", Barcode: null, IsActive: true, Version: null,
            Specification: "Cortical screw, 3.5 mm x 14 mm, titanium", CeilingPrice: null)))
        .GetProperty("id").GetGuid();

    private async Task ReceiveAsync(ApiClient storeKeeper, Guid storeId, Guid itemId, string batch)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var line = new GoodsReceiptLineInput(itemId, null, batch, null, today.AddYears(3), 5m, 0m, 600m, 1_000m, 0m, null, AcceptShortExpiry: true);
        await GoodsReceipts.IntoStockAsync(server, storeKeeper,
            new CreateGoodsReceiptRequest(vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null, [line], SendForApproval: true));
    }

    /// <summary>What the OT store can issue of each batch now.</summary>
    private static async Task<(decimal First, decimal Second)> AvailableAsync(ApiClient nurse, string name, string first, string second)
    {
        var item = (await nurse.GetAsync<List<OtStoreItem>>($"/api/v1/ot/store-items?search={Uri.EscapeDataString(name)}")).Single(i => i.Name == name);
        decimal Of(string batch) => item.Batches.SingleOrDefault(b => b.BatchNumber == batch)?.Available ?? 0m;
        return (Of(first), Of(second));
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
