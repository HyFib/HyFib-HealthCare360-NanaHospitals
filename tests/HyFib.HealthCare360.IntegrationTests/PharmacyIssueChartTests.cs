using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Mar;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// What the IP pharmacy issues against a ward indent supplies the line already on the patient's medication chart. A
/// replacement ampoule of a verbal STAT order makes no second order and no dose to give again; an e-prescription supplied
/// by an indent raised for it is charted on its own line, and its prescription's IP queue counts what went out, so it is
/// not issued and charged a second time.
/// </summary>
public sealed class PharmacyIssueChartTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_replacement_ampoule_of_a_verbal_stat_order_charts_no_second_order_and_no_dose()
    {
        var (store, storeKeeper) = await PharmacyAsync();
        var furosemide = await MedicineAsync(storeKeeper, "Furosemide 10 mg/ml Injection", "Furosemide", "AMP");
        await ReceiveAsync(storeKeeper, store, furosemide, 5m);
        var (doctor, nurse, pharmacist, patientId, admission) = await WardAsync();
        var doctorId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var orders = $"/api/v1/nursing/admissions/{admission.Id}/medication-orders";

        // The doctor's verbal "Inj. Furosemide 40 mg IV STAT", written and read back by the nurse, given from the ward's stock.
        var stat = await nurse.PostAsync<MedicationOrderDto>(orders, new AddMedicationOrderRequest(
            DrugItemId: furosemide, DrugName: "Furosemide 10 mg/ml Injection", GenericName: "Furosemide", Strength: "10 mg/ml", Dose: "40 mg", Route: "IV",
            Frequency: "STAT", DurationDays: 1, StartAt: null, Instructions: null, IsControlledDrug: false, OrderedByPractitionerId: doctorId,
            Mode: MedicationOrderMode.Verbal, ReadBack: true));
        var dose = (await nurse.GetAsync<List<MarEntryDto>>($"/api/v1/nursing/admissions/{admission.Id}/mar")).Single(e => e.MedicationOrderId == stat.Id);
        (await nurse.PostAsync<MarEntryDto>($"/api/v1/nursing/mar/{dose.Id}/record",
            new RecordAdministrationRequest(MarStatus.Given, null, null, null, null, null, dose.Version))).Status.ShouldBe(MarStatus.Given);

        // A replacement ampoule is indented and issued.
        await IndentAndIssueAsync(nurse, pharmacist, store, patientId, admission, furosemide, 1m, prescriptionId: null);

        // It supplied the order on the chart: no "Furosemide, as directed" beside it, and no dose due.
        var chart = await nurse.GetAsync<List<MedicationOrderDto>>(orders);
        var only = chart.ShouldHaveSingleItem();
        (only.Id, only.LastDispensedAt.HasValue).ShouldBe((stat.Id, true));
        (await nurse.GetAsync<List<MarEntryDto>>($"/api/v1/nursing/admissions/{admission.Id}/mar")).ShouldNotContain(e => e.Status == MarStatus.Due);
    }

    [Fact]
    public async Task An_e_prescription_supplied_by_an_indent_raised_for_it_is_charted_and_not_issued_again_from_the_queue()
    {
        var (store, storeKeeper) = await PharmacyAsync();
        var ceftriaxone = await MedicineAsync(storeKeeper, "Ceftriaxone 1 g Injection", "Ceftriaxone", "VIAL");
        await ReceiveAsync(storeKeeper, store, ceftriaxone, 20m);
        var (doctor, nurse, pharmacist, patientId, admission) = await WardAsync();

        // Inj. Ceftriaxone 1 g IV BD for 5 days, ten vials: on the chart with no dose until the pharmacy supplies it.
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId, EncounterId: null, VisitId: null, AdmissionId: admission.Id, IsInpatient: true, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    ceftriaxone, "Ceftriaxone 1 g Injection", "Ceftriaxone", "1 g", "Injection", Route: "IV", Dose: "1 g", DoseFrequency.BD,
                    CustomFrequency: null, DosesPerDay: null, 5, Quantity: 10, Instructions: null, IsControlledDrug: false, ControlledSchedule: null),
            ]));
        var signed = (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version))).Prescription;
        var order = (await nurse.GetAsync<List<MedicationOrderDto>>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders"))
            .Single(o => o.PrescriptionId == signed.Id);

        // The nurse indents the ten vials for the prescription, as the indent form does when the medicine is on it.
        var issued = await IndentAndIssueAsync(nurse, pharmacist, store, patientId, admission, ceftriaxone, 10m, prescriptionId: signed.Id);

        // The prescription's own line is charted, and nothing else is.
        var chart = await nurse.GetAsync<List<MedicationOrderDto>>($"/api/v1/nursing/admissions/{admission.Id}/medication-orders");
        chart.ShouldHaveSingleItem().Id.ShouldBe(order.Id);
        chart[0].LastDispensedAt.ShouldNotBeNull();
        var doses = await DosesAsync(order.Id);
        doses.ShouldNotBeEmpty();
        doses.ShouldAllBe(d => d.Status == MarStatus.Due && !d.IsAdHoc);
        (await IssueLineAsync(issued.Id)).PrescriptionLineId.ShouldNotBeNull();

        // The prescription's IP queue counts the ten vials: it is done, and is not issued, nor charged, again.
        var waiting = (await pharmacist.GetAsync<PagedResult<QueueListItem>>($"/api/v1/pharmacy/queue?search={signed.PrescriptionNumber}&includeClosed=true"))
            .Items.ShouldHaveSingleItem();
        var entry = await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{waiting.Id}?storeId={store}");
        (entry.Status, entry.Lines.ShouldHaveSingleItem().RemainingQuantity).ShouldBe((QueueEntryStatus.Dispensed, 0m));
        using var again = await pharmacist.SendRawAsync(HttpMethod.Post, "/api/v1/pharmacy/ip-issues", new IpIssueRequest(
            store, null, entry.Id, null, null, null, [new IpIssueLineInput(ceftriaxone, 10m, null, null, entry.Lines[0].Id, null, false)], ConfirmControlledDrugs: false));
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, await again.Content.ReadAsStringAsync(Ct));
        (await again.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct)).GetProperty("code").GetString().ShouldBe("queue.closed");
    }

    // ------------------------------------------------------------------ steps

    /// <summary>The nurse indents the medicine for the patient (for the prescription, when one is named); the pharmacist issues it.</summary>
    private static async Task<IpIssueCreated> IndentAndIssueAsync(ApiClient nurse, ApiClient pharmacist, Guid store, Guid patientId, AdmissionCreated admission,
        Guid itemId, decimal quantity, Guid? prescriptionId)
    {
        var indent = await nurse.PostAsync<PharmacyDocumentCreated>("/api/v1/pharmacy/ward-indents", new RaiseWardIndentRequest(
            patientId, admission.Id, admission.VisitId, "Semi-private ward", null, IndentUrgency.Stat, prescriptionId, null,
            [new WardIndentLineInput(itemId, quantity, null)]));
        var line = (await pharmacist.GetAsync<WardIndentDetail>($"/api/v1/pharmacy/ward-indents/{indent.Id}?storeId={store}")).Lines.ShouldHaveSingleItem();
        return await pharmacist.PostAsync<IpIssueCreated>("/api/v1/pharmacy/ip-issues", new IpIssueRequest(
            store, indent.Id, null, patientId, admission.Id, admission.VisitId, [new IpIssueLineInput(itemId, quantity, null, line.Id, null, null, false)],
            ConfirmControlledDrugs: false));
    }

    /// <summary>A new patient admitted under the doctor to a bed of the semi-private ward, with the ward's nurse and the pharmacist.</summary>
    private async Task<(ApiClient Doctor, ApiClient Nurse, ApiClient Pharmacist, Guid PatientId, AdmissionCreated Admission)> WardAsync()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var patient = await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice);
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "SPW");
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, ward.Id);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient.Id, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Acute pulmonary oedema", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        return (doctor, nurse, pharmacist, patient.Id, admission);
    }

    private async Task<(Guid Store, ApiClient StoreKeeper)> PharmacyAsync()
    {
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        return ((await storeKeeper.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == "IPPH").Id, storeKeeper);
    }

    /// <summary>A medicine of Schedule H (neither controlled nor high-alert), an injection counted by <paramref name="unit"/>.</summary>
    private static async Task<Guid> MedicineAsync(ApiClient storeKeeper, string name, string generic, string unit) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: $"{name} {Letters(6)}", GenericName: generic, BrandName: null, Type: ItemType.Drug, CategoryId: null, Strength: null,
            DosageForm: "Injection", PackSize: null, BaseUnit: unit, PurchaseUnit: unit, PurchaseUnitFactor: 1, HsnCode: "300490", GstRate: 5m, Schedule: DrugSchedule.H,
            IsControlled: false, IsColdChain: false, VedClass: VedClass.Essential, ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0,
            Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
        .GetProperty("id").GetGuid();

    /// <summary>The store receives <paramref name="units"/> at ₹40 each; someone else posts it.</summary>
    private async Task ReceiveAsync(ApiClient storeKeeper, Guid storeId, Guid itemId, decimal units)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = server.Today;
        await GoodsReceipts.IntoStockAsync(server, storeKeeper, new CreateGoodsReceiptRequest(
            vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(itemId, null, $"B{Letters(8)}", null, today.AddYears(2), units, 0m, 20m, 40m, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: true));
    }

    private async Task<IpIssueLine> IssueLineAsync(Guid issueId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<IpIssueLine>().AsNoTracking().SingleAsync(l => l.IpIssueId == issueId, Ct);
    }

    /// <summary>The order's doses on the MAR, whatever their day.</summary>
    private async Task<List<MedicationAdministration>> DosesAsync(Guid orderId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<MedicationAdministration>().AsNoTracking()
            .Where(d => d.MedicationOrderId == orderId).ToListAsync(Ct);
    }

    private static string Letters(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => (char)('A' + Random.Shared.Next(26))));
}
