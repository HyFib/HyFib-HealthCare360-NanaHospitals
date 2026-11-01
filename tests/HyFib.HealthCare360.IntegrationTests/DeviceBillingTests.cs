using System.Globalization;
using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.SpecialtyCare.Domain.OperationTheatre;
using HyFib.HealthCare360.Modules.SpecialtyCare.Features.Ot;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Migrations = HyFib.HealthCare360.Server.Persistence.Migrations;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Implants and other devices on a bill, over the real API, as NPPA asks a hospital to bill a coronary stent or a knee
/// implant (DPCO 2013): the bill names the device's brand, its manufacturer or importer, its batch and its specification,
/// and a unit is never charged above the MRP printed on its pack, nor a price-controlled implant above its NPPA ceiling
/// price, even from a pack printed before the ceiling was cut.
/// </summary>
public sealed class DeviceBillingTests(HospitalServer server)
{
    private const string Bills = "/api/v1/billing/bills";
    private const string Brand = "Attune";
    private const string Maker = "DePuy Synthes, imported by Johnson and Johnson Medical India";
    private const string Specification = "Primary total knee, cobalt-chromium femoral component, size 5";

    // A pack printed before the ceiling was cut carries an MRP above it.
    private const decimal PackMrp = 60_000m;
    private const decimal Ceiling = 57_456m;

    // A consignment drug-eluting stent's ceiling with GST, as a hospital's price list might give it.
    private const decimal StentCeiling = 31_080m;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_knee_implant_from_the_ot_store_is_billed_with_its_brand_maker_batch_and_specification_at_no_more_than_its_ceiling()
    {
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // The store keeper adds the implant, with what its bill must say and its NPPA ceiling price. An implant's batch is
        // received with the MRP on its pack: without one, the receipt is refused, as a medicine's is.
        var name = $"Knee implant system {Letters(6)}";
        var implant = await ImplantAsync(storeKeeper, name, Brand, Maker, Specification, Ceiling);
        var store = await StoreAsync(storeKeeper, "OTS");
        var batch = $"KN{Letters(8)}";
        using (var noMrp = await ReceiveRawAsync(storeKeeper, store, Line(implant, batch, 2, 41_000m, 0m)))
        {
            noMrp.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await noMrp.Content.ReadAsStringAsync(Ct)).ShouldContain("grn.mrp_required");
        }

        using (var received = await ReceiveRawAsync(storeKeeper, store, Line(implant, batch, 2, 41_000m, PackMrp)))
        {
            received.EnsureSuccessStatusCode();
        }

        // In theatre, the nurse records the implant. Its pack says ₹60,000, yet no more than the ceiling may be charged.
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var booking = await SurgeryUnderWayAsync(doctor, patientId, visit.Id);
        using (var aboveCeiling = await nurse.SendRawAsync(HttpMethod.Post, Materials(booking), StoreImplant(implant, name, PackMrp, "SN-A1")))
        {
            aboveCeiling.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ProblemTextAsync(aboveCeiling)).ShouldBe($"unitPrice: {name} can be charged at most ₹57,456.00 a unit: its NPPA ceiling price.");
        }

        // At the ceiling it is recorded. No lot was written: the batch the OT store issued is recorded, and the item master,
        // not the dialog, says whose the implant is and what it is.
        var material = await nurse.PostAsync<SurgeryMaterialDto>(Materials(booking), StoreImplant(implant, name, Ceiling, "SN-A1") with { Manufacturer = "Typed maker" });
        (material.ItemName, material.Brand, material.Manufacturer, material.Specification).ShouldBe((name, Brand, Maker, Specification));
        (material.LotNumber, material.SerialNumber, material.Mrp, material.CeilingPrice, material.UnitPrice).ShouldBe((batch, "SN-A1", PackMrp, Ceiling, Ceiling));

        // The bill names the implant, and beneath it what NPPA asks: brand, maker, batch, serial, specification and MRP.
        var bill = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(visit.Id, null, null));
        var line = bill.Lines.Single(l => l.ServiceCode == "OT-IMPLANT");
        (line.Description, line.UnitPrice).ShouldBe(($"Implant: {name}", Ceiling));
        line.Device.ShouldBe(new DeviceDetails(Brand, Maker, Specification, batch, "SN-A1", PackMrp, Ceiling));
        var printed = (await billing.GetAsync<PrintableDocumentDto>($"{Bills}/{bill.Id}/document")).Html;
        printed.ShouldContain(
            $"Brand {Brand} · made or imported by {Maker} · Batch {batch} · S/N SN-A1 · {Specification} · MRP ₹60,000.00 · NPPA ceiling price ₹57,456.00");

        // A later batch arrives. Two packs with no lot written would come from both batches: each is recorded on a line of
        // its own, so that a recall by either lot finds this patient.
        var later = $"KN{Letters(8)}";
        using (var received = await ReceiveRawAsync(storeKeeper, store, Line(implant, later, 1, 41_000m, PackMrp, years: 4)))
        {
            received.EnsureSuccessStatusCode();
        }

        using (var two = await nurse.SendRawAsync(HttpMethod.Post, Materials(booking), StoreImplant(implant, name, Ceiling, null) with { Quantity = 2m }))
        {
            two.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            var problem = await two.Content.ReadAsStringAsync(Ct);
            (problem.Contains("surgery.material_batches"), problem.Contains($"2 batches ({batch}, {later})")).ShouldBe((true, true), problem);
        }

        // The other pack of the first batch, with neither lot nor serial written, is recorded with the batch the store issues:
        // a recall finds it.
        (await nurse.PostAsync<SurgeryMaterialDto>(Materials(booking), StoreImplant(implant, name, Ceiling, null))).LotNumber.ShouldBe(batch);

        // The desk cannot charge an implant by hand, nor add one to the draft, outside the surgery record or the IP pharmacy.
        var otImplant = await ServiceAsync(billing, "OT-IMPLANT");
        using (var typed = await billing.SendRawAsync(HttpMethod.Post, "/api/v1/billing/charges", new PostChargeRequest(
                   patientId, visit.Id, null, otImplant, 1m, 70_000m, "Knee implant", null, null, null)))
        {
            typed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await typed.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.implant_from_record");
        }

        using var added = await billing.SendRawAsync(HttpMethod.Post, $"{Bills}/{bill.Id}/lines",
            new AddBillLineRequest(otImplant, 1m, 70_000m, "Knee implant", null, null, bill.Version));
        added.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await added.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.implant_from_record");
    }

    [Fact]
    public async Task A_consignment_stent_needs_its_brand_maker_and_mrp_and_is_charged_no_more_than_its_mrp_or_its_ceiling()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var booking = await SurgeryUnderWayAsync(doctor, patientId, visit.Id);

        // A stent the vendor brought on consignment is not in the item master: its pack gives what the bill says.
        const string spec = "Drug-eluting (everolimus), cobalt-chromium, 3.0 × 28 mm";
        var stent = new AddSurgeryMaterialRequest(
            MaterialKind.Implant, null, "Coronary stent", "Abbott Vascular", "7041231", null, null, 1m, 38_000m, "XIENCE Sierra", spec, 38_933.14m);
        foreach (var (missing, field) in new[] { (stent with { Brand = null }, "brand"), (stent with { Manufacturer = " " }, "manufacturer"), (stent with { Mrp = null }, "mrp") })
        {
            using var refused = await nurse.SendRawAsync(HttpMethod.Post, Materials(booking), missing);
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ProblemTextAsync(refused)).ShouldStartWith($"{field}: ");
        }

        using (var aboveMrp = await nurse.SendRawAsync(HttpMethod.Post, Materials(booking), stent with { UnitPrice = 40_000m }))
        {
            aboveMrp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ProblemTextAsync(aboveMrp)).ShouldBe("unitPrice: Coronary stent can be charged at most ₹38,933.14 a unit: the MRP printed on its pack.");
        }

        // A price-controlled stent is held to its NPPA ceiling too, which the nurse takes from the hospital's price list:
        // below the pack's MRP, ₹38,000 a unit is still too much.
        var priceControlled = stent with { CeilingPrice = StentCeiling };
        using (var aboveCeiling = await nurse.SendRawAsync(HttpMethod.Post, Materials(booking), priceControlled))
        {
            aboveCeiling.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await ProblemTextAsync(aboveCeiling)).ShouldBe("unitPrice: Coronary stent can be charged at most ₹31,080.00 a unit: its NPPA ceiling price.");
        }

        // At its ceiling it is recorded and billed so, its details beneath it.
        (await nurse.PostAsync<SurgeryMaterialDto>(Materials(booking), priceControlled with { UnitPrice = StentCeiling })).Amount.ShouldBe(StentCeiling);
        var line = (await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(visit.Id, null, null))).Lines.Single(l => l.ServiceCode == "OT-IMPLANT");
        (line.Description, line.UnitPrice).ShouldBe(("Implant: Coronary stent", StentCeiling));
        line.Device.ShouldBe(new DeviceDetails("XIENCE Sierra", "Abbott Vascular", spec, "7041231", null, 38_933.14m, StentCeiling));
    }

    [Fact]
    public async Task An_implant_the_ip_pharmacy_issues_is_charged_at_its_ceiling_when_its_pack_says_more_and_a_return_is_credited_the_same()
    {
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // Two packs of the knee implant in the IP pharmacy, printed at ₹60,000 before the ceiling.
        var name = $"Knee implant system {Letters(6)}";
        var implant = await ImplantAsync(storeKeeper, name, Brand, Maker, Specification, Ceiling);
        var store = await StoreAsync(storeKeeper, "IPPH");
        var batch = $"KP{Letters(8)}";
        using (var received = await ReceiveRawAsync(storeKeeper, store, Line(implant, batch, 2, 41_000m, PackMrp)))
        {
            received.EnsureSuccessStatusCode();
        }

        // The pharmacist issues both to an admitted patient: each is charged at the ceiling, not the pack's MRP.
        var (patientId, _) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var admission = await AdmitAsync(frontOffice, doctor, patientId);
        var created = await pharmacist.PostAsync<IpIssueCreated>("/api/v1/pharmacy/ip-issues", new IpIssueRequest(
            store, null, null, patientId, admission.Id, admission.VisitId, [new IpIssueLineInput(implant, 2m, null, null, null, null, false)], false));
        created.TotalAmount.ShouldBe(2 * Ceiling);
        var issued = (await pharmacist.GetAsync<IpIssueDetail>($"/api/v1/pharmacy/ip-issues/{created.Id}")).Lines.ShouldHaveSingleItem();
        (issued.BatchNumber, issued.UnitMrp, issued.UnitPrice, issued.Amount).ShouldBe((batch, PackMrp, Ceiling, 2 * Ceiling));

        // On the running bill and the patient's bill the implant carries its details, the pack's MRP and the ceiling it is charged at.
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        (await billing.GetAsync<RunningBill>($"/api/v1/billing/ip-accounts/{account.Id}/running-bill")).Lines.Single(l => l.ServiceCode == "PHARM-IMPL")
            .Device.ShouldBe(new DeviceDetails(Brand, Maker, Specification, batch, null, PackMrp, Ceiling));
        var interim = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/interim-bills", new CreateInterimBillRequest(null));
        var line = interim.Lines.Single(l => l.ServiceCode == "PHARM-IMPL");
        (line.Description, line.Quantity, line.UnitPrice).ShouldBe((name, 2m, Ceiling));
        line.Device.ShouldBe(new DeviceDetails(Brand, Maker, Specification, batch, null, PackMrp, Ceiling));

        // NPPA cuts the ceiling, and the store keeper follows. One pack comes back unused: it is credited at what it was
        // charged, and its credit line carries its details as issued, not the new ceiling.
        await ReviseCeilingAsync(storeKeeper, implant, 50_000m);
        var returned = await pharmacist.PostAsync<WardReturnCreated>($"/api/v1/pharmacy/ip-issues/{created.Id}/returns",
            new WardReturnRequest([new WardReturnLineInput(issued.Id, 1m, true)], "Unused, returned unopened"));
        returned.CreditAmount.ShouldBe(Ceiling);
        var credit = (await billing.GetAsync<RunningBill>($"/api/v1/billing/ip-accounts/{account.Id}/running-bill")).Lines
            .Single(l => l.ServiceCode == "PHARM-IMPL" && l.IsReturn);
        (credit.Description, credit.Quantity, credit.UnitPrice).ShouldBe(($"Returned: {name}", -1m, Ceiling));
        credit.Device.ShouldBe(new DeviceDetails(Brand, Maker, Specification, batch, null, PackMrp, Ceiling));

        // The upgrade prices the lines issued before it at their MRP, which is what they were charged.
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Database.ExecuteSqlAsync($"UPDATE pharmacy.ip_issue_lines SET unit_price = 0 WHERE id = {issued.Id}", Ct);
            await db.Database.ExecuteSqlRawAsync(Migrations.DeviceBillDetails.PriceIssuedLinesAtTheirMrp, Ct);
        }

        (await pharmacist.GetAsync<IpIssueDetail>($"/api/v1/pharmacy/ip-issues/{created.Id}")).Lines.ShouldHaveSingleItem().UnitPrice.ShouldBe(PackMrp);

        // The run's tests read the same wards' boards and housekeeping lists.
        await CancelAdmissionAsync(frontOffice, nurse, admission.Id);
    }

    [Fact]
    public async Task Billing_refuses_a_device_charged_above_its_mrp_or_its_ceiling_whichever_module_posts_it()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var poster = scope.ServiceProvider.GetRequiredService<IChargePoster>();
        var implant = await db.Set<ServiceItem>().Where(s => s.Code == "OT-IMPLANT").Select(s => s.Id).SingleAsync(Ct);
        ChargeRequest Charge(decimal price, decimal? ceiling) => new()
        {
            PatientId = patientId,
            VisitId = visit.Id,
            ServiceItemId = implant,
            UnitPriceOverride = price,
            Description = "Implant: knee system",
            SourceModule = "tests.device",
            SourceReference = Guid.CreateVersion7().ToString(),
            Device = new DeviceDetails(Brand, Maker, Specification, "K1", null, PackMrp, ceiling),
        };

        // The poster only stages: nothing is saved.
        (await poster.PostAsync(Charge(61_000m, null), Ct)).Error.ShouldNotBeNull().Code.ShouldBe("billing.device_above_mrp");
        (await poster.PostAsync(Charge(58_000m, Ceiling), Ct)).Error.ShouldNotBeNull().Code.ShouldBe("billing.device_above_ceiling");
        (await poster.PostAsync(Charge(Ceiling, Ceiling), Ct)).IsSuccess.ShouldBeTrue();

        // A credit gives back what was charged, whatever the limit is now.
        (await poster.PostAsync(Charge(Ceiling, 50_000m) with { Quantity = -1m }, Ct)).IsSuccess.ShouldBeTrue();
    }

    private static string Materials(Guid bookingId) => $"/api/v1/ot/bookings/{bookingId}/materials";

    private static AddSurgeryMaterialRequest StoreImplant(Guid itemId, string name, decimal unitPrice, string? serial) =>
        new(MaterialKind.Implant, itemId, name, null, null, serial, null, 1m, unitPrice);

    /// <summary>Store keeper: adds an implant counted and bought by the piece, with its brand, maker, specification and ceiling.</summary>
    private static async Task<Guid> ImplantAsync(ApiClient storeKeeper, string name, string brand, string maker, string specification, decimal ceiling) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: name, GenericName: null, BrandName: brand, Type: ItemType.Implant, CategoryId: null, Strength: null,
            DosageForm: null, PackSize: "1 piece", BaseUnit: "PCS", PurchaseUnit: "PCS", PurchaseUnitFactor: 1, HsnCode: "9021", GstRate: 5m,
            Schedule: DrugSchedule.None, IsControlled: false, IsColdChain: false, VedClass: VedClass.Vital, ReorderLevel: 0, ReorderQuantity: 0,
            MinimumStock: 0, MaximumStock: 0, Manufacturer: maker, Barcode: null, IsActive: true, Version: null, Specification: specification,
            CeilingPrice: ceiling)))
        .GetProperty("id").GetGuid();

    private static async Task<Guid> StoreAsync(ApiClient storeKeeper, string code) =>
        (await storeKeeper.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == code).Id;

    private static GoodsReceiptLineInput Line(Guid itemId, string batch, decimal pieces, decimal rate, decimal mrp, int years = 3) =>
        new(itemId, null, batch, null, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(years)), pieces, 0m, rate, mrp, 0m, null, AcceptShortExpiry: true);

    /// <summary>Store keeper: NPPA revises the implant's ceiling price, and the item master follows.</summary>
    private static async Task ReviseCeilingAsync(ApiClient storeKeeper, Guid itemId, decimal ceiling)
    {
        var d = await storeKeeper.GetAsync<ItemDetail>($"/api/v1/inventory/items/{itemId}");
        await storeKeeper.PutAsync($"/api/v1/inventory/items/{itemId}", new SaveItemRequest(
            d.Code, d.Name, d.GenericName, d.BrandName, d.Type, d.CategoryId, d.Strength, d.DosageForm, d.PackSize, d.BaseUnit, d.PurchaseUnit,
            d.PurchaseUnitFactor, d.HsnCode, d.GstRate, d.Schedule, d.IsControlled, d.IsColdChain, d.VedClass, d.ReorderLevel, d.ReorderQuantity,
            d.MinimumStock, d.MaximumStock, d.Manufacturer, d.Barcode, d.IsActive, d.Version, d.Specification, ceiling));
    }

    /// <summary>Store keeper: receives a vendor invoice into the store and posts it to stock.</summary>
    private static async Task<HttpResponseMessage> ReceiveRawAsync(ApiClient storeKeeper, Guid storeId, GoodsReceiptLineInput line)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return await storeKeeper.SendRawAsync(HttpMethod.Post, "/api/v1/purchase/grns", new CreateGoodsReceiptRequest(
            vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null, [line], Post: true));
    }

    /// <summary>
    /// A surgery under way for the patient's visit, the doctor operating: the theatre's steps before it are covered by
    /// their own tests.
    /// </summary>
    private async Task<Guid> SurgeryUnderWayAsync(ApiClient doctor, Guid patientId, Guid visitId)
    {
        var surgeonId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var facilityId = await db.Set<Visit>().IgnoreQueryFilters().Where(v => v.Id == visitId).Select(v => v.FacilityId).SingleAsync(Ct);
        var booking = new SurgeryBooking
        {
            FacilityId = facilityId,
            BookingNumber = "OT-D" + Random.Shared.Next(100_000, 999_999).ToString(CultureInfo.InvariantCulture),
            PatientId = patientId,
            VisitId = visitId,
            ProcedureCode = "0SRC0J9",
            ProcedureName = "Total knee replacement",
            Laterality = Laterality.Right,
            SurgeonId = surgeonId,
            AnaesthesiaType = AnaesthesiaType.Spinal,
            Status = SurgeryStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-40),
        };
        db.Add(booking);
        await db.SaveChangesAsync(Ct);
        return booking.Id;
    }

    private static async Task<Guid> ServiceAsync(ApiClient billing, string code) =>
        (await billing.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?search={code}")).Single(s => s.Code == code).Id;

    /// <summary>The front office admits the patient under the doctor to a free bed of the male general ward.</summary>
    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Osteoarthritis of the knee", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>The front office cancels the admission and housekeeping cleans its bed, which is vacant again.</summary>
    private static async Task CancelAdmissionAsync(ApiClient frontOffice, ApiClient nurse, Guid admissionId)
    {
        var stay = await frontOffice.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admissionId}");
        using (var cancelled = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/inpatient/admissions/{admissionId}/cancel",
                   new CancelAdmissionRequest("Admitted only to issue an implant")))
        {
            cancelled.EnsureSuccessStatusCode();
        }

        var task = (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={stay.WardId}"))
            .Single(t => t.BedId == stay.BedId && t.AdmissionId == stay.Id);
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        (await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned")))
            .Status.ShouldBe(HousekeepingStatus.Completed);
    }

    /// <summary>Each field's messages ("unitPrice: …"), decoded: the API's JSON escapes ₹.</summary>
    private static async Task<string> ProblemTextAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object
            ? string.Join("\n", errors.EnumerateObject().Select(f => $"{f.Name}: {string.Join(" ", f.Value.EnumerateArray().Select(m => m.GetString()))}"))
            : string.Empty;
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
