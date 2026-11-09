using System.Text.Json;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Registers;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.SharedKernel;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A recalled implant or OT consumable batch traced to the patients operated on with it, over the real API and PostgreSQL:
/// the pharmacy's recall trace lists, beside its own dispenses, every operation that recorded the batch from the OT store,
/// with the patient, the surgery, its date and the implant's serial number. The lines come from SpecialtyCare through a
/// Platform contract, so neither module references the other.
/// </summary>
public sealed class ImplantRecallTraceTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_recalled_implant_batch_lists_every_patient_operated_on_with_it_and_each_implant_serial()
    {
        var cases = new TheatreCases(server);
        var storeKeeper = await server.SignInAsync(Users.HospitalAdmin);
        var nurse = await server.SignInAsync(Users.Nurse);
        var pharmacist = await server.SignInAsync(Users.Pharmacist);
        var name = $"Knee femoral component {Letters(6)}";
        var item = await ImplantAsync(storeKeeper, name);
        var store = (await storeKeeper.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "OTS").Id;
        var (recalled, other) = ($"KF{Letters(8)}", $"KF{Letters(8)}");
        await ReceiveAsync(storeKeeper, store, item, recalled);
        await ReceiveAsync(storeKeeper, store, item, other);

        // Two knees replaced with the recalled lot, a third with another lot.
        var first = await cases.UnderWayAsync(Laterality.Right, "Total knee replacement, right");
        var second = await cases.UnderWayAsync(Laterality.Left, "Total knee replacement, left");
        var third = await cases.UnderWayAsync(Laterality.Right, "Total knee replacement, right");
        await nurse.PostAsync<SurgeryMaterialDto>(Materials(first), new AddSurgeryMaterialRequest(
            MaterialKind.Implant, item, name, null, recalled, "SN-1001", null, 1m, 60_000m));
        await nurse.PostAsync<SurgeryMaterialDto>(Materials(second), new AddSurgeryMaterialRequest(
            MaterialKind.Implant, item, name, null, recalled.ToLowerInvariant(), "sn-1002", null, 1m, 60_000m));
        await nurse.PostAsync<SurgeryMaterialDto>(Materials(third), new AddSurgeryMaterialRequest(
            MaterialKind.Implant, item, name, null, other, "SN-2001", null, 1m, 60_000m));

        // The recall notice names the lot; the pharmacy traces it, typed in any case.
        var trace = await pharmacist.GetAsync<RecallTrace>($"/api/v1/pharmacy/recalls/trace?itemId={item}&batchNumber={recalled.ToLowerInvariant()}");

        var operated = trace.Patients.Where(p => p.Source == RecallTraceSource.Implant).OrderBy(p => p.SerialNumber).ToList();
        operated.Select(p => (p.PatientId, p.SerialNumber, p.Procedure, p.Kept)).ShouldBe([
            ((Guid?)first.PatientId, "SN-1001", "Total knee replacement, right", 1m),
            ((Guid?)second.PatientId, "SN-1002", "Total knee replacement, left", 1m),
        ]);
        trace.Patients.ShouldNotContain(p => p.PatientId == third.PatientId);
        foreach (var row in operated)
        {
            row.Uhid.ShouldNotBeNullOrWhiteSpace();
            row.PatientName.ShouldNotBe("Unknown patient");
            row.Document.ShouldNotBeNullOrWhiteSpace("the surgery's booking number");
            row.At.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(-1), "the date of the operation");
            row.StoreName.ShouldNotBeNullOrWhiteSpace("the theatre");
        }

        var booking = await nurse.GetAsync<JsonElement>(first.Path);
        operated[0].Document.ShouldBe(booking.GetProperty("bookingNumber").GetString());
        (trace.PatientCount, trace.QuantityWithPatients).ShouldBe((2, 2m));
    }

    [Fact]
    public async Task A_line_corrected_to_another_lot_is_traced_by_the_lot_it_was_corrected_to()
    {
        var cases = new TheatreCases(server);
        var storeKeeper = await server.SignInAsync(Users.HospitalAdmin);
        var nurse = await server.SignInAsync(Users.Nurse);
        var pharmacist = await server.SignInAsync(Users.Pharmacist);
        var name = $"Bone plate {Letters(6)}";
        var item = await ImplantAsync(storeKeeper, name);
        var store = (await storeKeeper.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "OTS").Id;
        var (written, used) = ($"BP{Letters(8)}", $"BP{Letters(8)}");
        await ReceiveAsync(storeKeeper, store, item, written);
        await ReceiveAsync(storeKeeper, store, item, used);
        var c = await cases.UnderWayAsync(Laterality.Left, "Plating of fracture of the left radius");

        var plate = await nurse.PostAsync<SurgeryMaterialDto>(Materials(c), new AddSurgeryMaterialRequest(
            MaterialKind.Implant, item, name, null, written, "PL-77", null, 1m, 9_000m));
        var asked = await TheatreCases.AcceptedAsync(nurse, HttpMethod.Post, $"{Materials(c)}/{plate.Id}/corrections",
            new CorrectSurgeryMaterialRequest(used, "PL-77", null, 1m, "The plate used came from the second box"));
        await server.ApproveMaterialCorrectionAsync(asked.Id);

        (await TraceAsync(pharmacist, item, written)).ShouldNotContain(p => p.Source == RecallTraceSource.Implant);
        (await TraceAsync(pharmacist, item, used)).Where(p => p.Source == RecallTraceSource.Implant)
            .ShouldHaveSingleItem().ShouldSatisfyAllConditions(
                p => p.PatientId.ShouldBe(c.PatientId),
                p => p.SerialNumber.ShouldBe("PL-77"));
    }

    private static async Task<IReadOnlyList<RecallTraceRow>> TraceAsync(ApiClient pharmacist, Guid item, string batch) =>
        (await pharmacist.GetAsync<RecallTrace>($"/api/v1/pharmacy/recalls/trace?itemId={item}&batchNumber={batch}")).Patients;

    private static string Materials(TheatreCases.Case c) => $"{c.Path}/materials";

    private static async Task<Guid> ImplantAsync(ApiClient storeKeeper, string name) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: name, GenericName: null, BrandName: "Attune", Type: ItemType.Implant, CategoryId: null, Strength: null,
            DosageForm: null, PackSize: "1 piece", BaseUnit: "PCS", PurchaseUnit: "PCS", PurchaseUnitFactor: 1, HsnCode: "9021", GstRate: 5m,
            Schedule: DrugSchedule.None, IsControlled: false, IsColdChain: false, VedClass: VedClass.Vital, ReorderLevel: 0, ReorderQuantity: 0,
            MinimumStock: 0, MaximumStock: 0, Manufacturer: "DePuy Synthes", Barcode: null, IsActive: true, Version: null,
            Specification: "Cobalt-chromium, size 5", CeilingPrice: null)))
        .GetProperty("id").GetGuid();

    private async Task ReceiveAsync(ApiClient storeKeeper, Guid storeId, Guid itemId, string batch)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var line = new GoodsReceiptLineInput(itemId, null, batch, null, today.AddYears(3), 5m, 0m, 40_000m, 70_000m, 0m, null, AcceptShortExpiry: true);
        await GoodsReceipts.IntoStockAsync(server, storeKeeper,
            new CreateGoodsReceiptRequest(vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null, [line], SendForApproval: true));
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
