using System.Net;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Features.Flags;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Inpatient;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Domain;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Items;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Receipts;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Vendors;
using HyFib.HealthCare360.Platform.Auditing;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ControlledDrugRegisterEntry = HyFib.HealthCare360.Modules.Pharmacy.Domain.ControlledDrugRegisterEntry;
using PaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;
using SalePayment = HyFib.HealthCare360.Modules.Pharmacy.Domain.SalePayment;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// The pharmacy journey end to end, over the real API, as the counter and the ward live it.
/// <para>OP: a doctor signs an e-prescription over an allergy alert; the queue shows the override and its reason and will not
/// dispense the line until a pharmacist acknowledges it from their own sign-in (audited); the pharmacist dispenses and takes
/// cash; another pharmacist takes a return that pays cash back, which waits for the accountant. The stock ledger carries the
/// receipt, the dispensing and the return.</para>
/// <para>IP: the ward's nurse indents a narcotic for an admitted patient; the pharmacist issues it against the indent, which
/// charges the stay; the ward returns a sealed ampoule. The NDPS register carries the receipt, the issue to the patient and
/// the return with their running balance, and the stay's running bill the charge and its credit.</para>
/// </summary>
public sealed class PharmacyJourneyTests(HospitalServer server)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_e_prescription_signed_over_an_allergy_is_acknowledged_dispensed_paid_and_returned_with_approval()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var second = await server.SignInAsync(HospitalServer.Users.SecondPharmacist);
        var suffix = Letters(6);

        // Stock: 20 capsules of amoxicillin at ₹8 each in the OP pharmacy.
        var store = await StoreAsync(storeKeeper, "OPPH");
        var amoxicillin = await MedicineAsync(storeKeeper, $"Amoxicillin 500 mg Capsule {suffix}", "Amoxicillin", "500 mg", "CAP", DrugSchedule.H, controlled: false);
        var receipt = await ReceiveAsync(storeKeeper, store, amoxicillin, 20m, mrp: 8m);

        // The patient is allergic to amoxicillin; the doctor prescribes it all the same, giving a reason.
        var patient = (await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice)).Id;
        await nurse.PostAsync<JsonElement>($"/api/v1/emr/patients/{patient}/allergies",
            new RecordAllergyRequest("Amoxicillin", AllergyType.Drug, "Mild rash in childhood", AllergySeverity.Mild, null, null));
        var draft = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patient, EncounterId: null, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null,
            RefillsAllowed: 0,
            [new PrescriptionItemRequest(amoxicillin, $"Amoxicillin 500 mg Capsule {suffix}", "Amoxicillin", "500 mg", "Capsule", "Oral", "1", DoseFrequency.BD,
                null, null, 5, 10m, null, false, null)]));
        using (var blocked = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/clinical/prescriptions/{draft.Id}/sign",
                   new SignPrescriptionRequest(null, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version)))
        {
            (blocked.StatusCode, await CodeAsync(blocked)).ShouldBe((HttpStatusCode.UnprocessableEntity, "prescription.safety_block"));
        }

        const string reason = "Rash only, as a child; tolerated a course last year";
        var signed = (await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{draft.Id}/sign",
            new SignPrescriptionRequest(reason, AcknowledgeWarnings: true, ConfirmControlledDrugs: false, draft.Version))).Prescription;

        // The queue shows the override; the line carries the alert, and the prescription the doctor's reason.
        var listed = (await pharmacist.GetAsync<PagedResult<QueueListItem>>($"/api/v1/pharmacy/queue?search={signed.PrescriptionNumber}")).Items.ShouldHaveSingleItem();
        listed.HasUnacknowledgedOverride.ShouldBeTrue();
        var entry = await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{listed.Id}?storeId={store}");
        entry.SafetyOverrideReason.ShouldBe(reason);
        var line = entry.Lines.ShouldHaveSingleItem();
        line.SafetyOverride.ShouldNotBeNull().ShouldContain("ALLERGY");
        line.OverrideAcknowledgedAt.ShouldBeNull();

        // Not dispensed until acknowledged.
        var dispense = new DispenseRequest(store, [new DispenseLineRequest(line.Id, amoxicillin, 10m, null, null, false)], "Take after food", 0m,
            [new SalePayment(PaymentMode.Cash, 80m, null)], false);
        using (var held = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/queue/{entry.Id}/dispense", dispense))
        {
            (held.StatusCode, await CodeAsync(held)).ShouldBe((HttpStatusCode.UnprocessableEntity, "pharmacy.override_unacknowledged"));
        }

        // The doctor does not hold pharmacy.dispense: the acknowledgement is the pharmacist's.
        using (var notTheirs = await doctor.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/queue/{entry.Id}/lines/{line.Id}/acknowledge-override",
                   new AcknowledgeOverrideRequest(null)))
        {
            notTheirs.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        await pharmacist.PostAsync($"/api/v1/pharmacy/queue/{entry.Id}/lines/{line.Id}/acknowledge-override",
            new AcknowledgeOverrideRequest("Asked the patient: rash only; counselled to stop and return if it recurs"));
        var acknowledged = (await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{entry.Id}")).Lines.Single();
        acknowledged.OverrideAcknowledgedBy.ShouldNotBeNull();
        acknowledged.OverrideAcknowledgedAt.ShouldNotBeNull();
        var pharmacistId = await UserIdAsync(pharmacist);
        var audit = await AuditAsync("pharmacy.safety_override_acknowledged", entry.Id);
        (audit.UserId, audit.PatientId, audit.Severity).ShouldBe(((Guid?)pharmacistId, (Guid?)patient, AuditSeverity.Warning));
        audit.Summary.ShouldNotBeNull().ShouldContain(reason);

        // Dispensed, and paid in cash.
        var sold = await pharmacist.PostAsync<PharmacySaleCreated>($"/api/v1/pharmacy/queue/{entry.Id}/dispense", dispense);
        (sold.NetAmount, sold.PaidAmount, sold.DueAmount, sold.Status).ShouldBe((80m, 80m, 0m, SaleStatus.Paid));
        var sale = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{sold.Id}");
        sale.Items.ShouldHaveSingleItem().Quantity.ShouldBe(10m);
        (await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{entry.Id}")).Status.ShouldBe(QueueEntryStatus.Dispensed);

        // Two capsules come back sealed: another pharmacist takes the return, which pays cash out and so waits for the
        // accountant (slice 2); approved, the cash goes back and the capsules return to the shelf.
        var returned = await server.ReturnSaleAsync(second, sold.Id,
            new SaleReturnRequest([new SaleReturnLine(sale.Items.Single().Id, 2m, true)], "Course shortened by the doctor"));
        (returned.RefundAmount, returned.CashRefund).ShouldBe((16m, 16m));

        // The stock ledger: received 20, dispensed 10, 2 back: 12 in the store.
        var ledger = await LedgerAsync(amoxicillin);
        ledger.Select(l => (l.MovementType, l.Quantity, l.BalanceAfter)).ShouldBe([
            (StockMovementType.Grn, 20m, 20m),
            (StockMovementType.Dispense, -10m, 10m),
            (StockMovementType.Return, 2m, 12m),
        ]);
        (ledger[0].ReferenceId, ledger[1].ReferenceId, ledger[2].ReferenceId).ShouldBe(((Guid?)receipt, (Guid?)sold.Id, (Guid?)returned.Id));
    }

    [Fact]
    public async Task A_ward_indent_for_a_narcotic_is_issued_charged_to_the_stay_and_returned_with_the_ndps_register_in_step()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var storeKeeper = await server.SignInAsync(HospitalServer.Users.HospitalAdmin);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var suffix = Letters(6);

        // Ten ampoules of morphine (NDPS, Schedule X) at ₹50 each in the IP pharmacy.
        var store = await StoreAsync(storeKeeper, "IPPH");
        var morphine = await MedicineAsync(storeKeeper, $"Morphine 10 mg/ml Injection {suffix}", "Morphine", "10 mg/ml", "AMP", DrugSchedule.X, controlled: true);
        await ReceiveAsync(storeKeeper, store, morphine, 10m, mrp: 50m);

        var patient = (await TeleconsultationJourneyTests.RegisterPatientAsync(frontOffice)).Id;
        var admission = await AdmitAsync(frontOffice, doctor, patient);

        // The ward's nurse indents three ampoules for the patient.
        var indent = await nurse.PostAsync<PharmacyDocumentCreated>("/api/v1/pharmacy/ward-indents", new RaiseWardIndentRequest(
            patient, admission.Id, admission.VisitId, "Surgical ward", null, IndentUrgency.Stat, null, "Post-operative pain",
            [new WardIndentLineInput(morphine, 3m, null)]));
        var indentLine = (await pharmacist.GetAsync<WardIndentDetail>($"/api/v1/pharmacy/ward-indents/{indent.Id}?storeId={store}")).Lines.ShouldHaveSingleItem();

        // The pharmacist issues them against the indent, confirming the controlled drug; the stay is charged.
        var issued = await pharmacist.PostAsync<IpIssueCreated>("/api/v1/pharmacy/ip-issues", new IpIssueRequest(
            store, indent.Id, null, patient, admission.Id, admission.VisitId, [new IpIssueLineInput(morphine, 3m, null, indentLine.Id, null, null, false)],
            ConfirmControlledDrugs: true));
        issued.TotalAmount.ShouldBe(150m);
        var issueLine = (await pharmacist.GetAsync<IpIssueDetail>($"/api/v1/pharmacy/ip-issues/{issued.Id}")).Lines.ShouldHaveSingleItem();

        // One sealed ampoule comes back from the ward: restocked, at once, and credited to the stay.
        var back = await pharmacist.PostAsync<WardReturnCreated>($"/api/v1/pharmacy/ip-issues/{issued.Id}/returns",
            new WardReturnRequest([new WardReturnLineInput(issueLine.Id, 1m, true)], "Dose reduced; ampoule unopened"));
        back.CreditAmount.ShouldBe(50m);

        // The stock ledger and the NDPS register move together: in 10, out 3 to the patient, back 1, a balance of 8.
        (await LedgerAsync(morphine)).Select(l => (l.MovementType, l.Quantity, l.BalanceAfter)).ShouldBe([
            (StockMovementType.Grn, 10m, 10m),
            (StockMovementType.Dispense, -3m, 7m),
            (StockMovementType.Return, 1m, 8m),
        ]);
        var register = await RegisterAsync(morphine);
        register.Select(r => (r.MovementType, r.QuantityIn, r.QuantityOut, r.BalanceAfter)).ShouldBe([
            (StockMovementType.Grn, 10m, 0m, 10m),
            (StockMovementType.Dispense, 0m, 3m, 7m),
            (StockMovementType.Return, 1m, 0m, 8m),
        ]);
        (register[1].PatientId, register[1].ReferenceId, register[2].PatientId, register[2].ReferenceId)
            .ShouldBe(((Guid?)patient, (Guid?)issued.Id, (Guid?)patient, (Guid?)back.Id));
        register[1].PatientUhid.ShouldNotBeNullOrEmpty();

        // The stay's running bill: the issue charged at ₹150, and the ampoule returned taken off, to ₹100 for morphine.
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        var bill = await billing.GetAsync<RunningBill>($"/api/v1/billing/ip-accounts/{account.Id}/running-bill");
        var morphineLines = bill.Lines.Where(l => l.ServiceCode == "PHARM-MED" && l.Description.Contains(suffix, StringComparison.Ordinal)).ToList();
        morphineLines.ShouldNotBeEmpty();
        morphineLines.Where(l => l.Status != Modules.Billing.Domain.ChargeStatus.Cancelled).Sum(l => l.NetAmount).ShouldBe(100m);
    }

    // ------------------------------------------------------------------ steps

    private static async Task<Guid> StoreAsync(ApiClient api, string code) =>
        (await api.GetAsync<List<StoreDto>>("/api/v1/inventory/stores")).Single(s => s.Code == code).Id;

    /// <summary>A medicine counted and bought by its base unit, carrying GST at 5% (so whole-rupee MRPs stay whole).</summary>
    private static async Task<Guid> MedicineAsync(
        ApiClient storeKeeper, string name, string generic, string strength, string unit, DrugSchedule schedule, bool controlled) =>
        (await storeKeeper.PostAsync<JsonElement>("/api/v1/inventory/items", new SaveItemRequest(
            Code: $"IT-{Letters(10)}", Name: name, GenericName: generic, BrandName: null, Type: ItemType.Drug, CategoryId: null, Strength: strength,
            DosageForm: null, PackSize: null, BaseUnit: unit, PurchaseUnit: unit, PurchaseUnitFactor: 1, HsnCode: "300490", GstRate: 5m, Schedule: schedule,
            IsControlled: controlled, IsColdChain: false, VedClass: VedClass.Essential, ReorderLevel: 0, ReorderQuantity: 0, MinimumStock: 0, MaximumStock: 0,
            Manufacturer: "Test Pharma", Barcode: null, IsActive: true, Version: null)))
        .GetProperty("id").GetGuid();

    /// <summary>The store receives <paramref name="units"/> at <paramref name="mrp"/> each; someone else posts it. Returns the receipt's id.</summary>
    private async Task<Guid> ReceiveAsync(ApiClient storeKeeper, Guid storeId, Guid itemId, decimal units, decimal mrp)
    {
        var vendor = (await storeKeeper.GetAsync<PagedResult<VendorListItem>>("/api/v1/purchase/vendors?pageSize=100")).Items.Single(v => v.Code == "VEN-SBPD");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return (await GoodsReceipts.IntoStockAsync(server, storeKeeper, new CreateGoodsReceiptRequest(
            vendor.Id, storeId, null, $"INV-{Letters(10)}", today, today, null,
            [new GoodsReceiptLineInput(itemId, null, $"B{Letters(8)}", null, today.AddYears(2), units, 0m, mrp / 2, mrp, 0m, null, AcceptShortExpiry: false)],
            SendForApproval: true))).Id;
    }

    private async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId)
    {
        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var bed = await TestBeds.AddAsync(server, "SPW");
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: AdmissionSource.Direct, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Fracture of the right femur", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    private async Task<List<StockLedgerEntry>> LedgerAsync(Guid itemId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<StockLedgerEntry>().IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.ItemId == itemId).OrderBy(l => l.OccurredAt).ThenBy(l => l.Id).ToListAsync(Ct);
    }

    private async Task<List<ControlledDrugRegisterEntry>> RegisterAsync(Guid itemId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<ControlledDrugRegisterEntry>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.ItemId == itemId).OrderBy(r => r.OccurredAt).ThenBy(r => r.Id).ToListAsync(Ct);
    }

    private async Task<AuditEvent> AuditAsync(string action, Guid entityId)
    {
        await using var scope = server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<AuditEvent>().AsNoTracking()
            .SingleAsync(a => a.Action == action && a.EntityId == entityId.ToString(), Ct);
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
