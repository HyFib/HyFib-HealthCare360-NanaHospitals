using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Gateway;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Clinical.Domain;
using HyFib.HealthCare360.Modules.Clinical.Domain.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emergency;
using HyFib.HealthCare360.Modules.Clinical.Features.Emr;
using HyFib.HealthCare360.Modules.Clinical.Features.Encounters;
using HyFib.HealthCare360.Modules.Clinical.Features.Orders;
using HyFib.HealthCare360.Modules.Clinical.Features.Prescriptions;
using HyFib.HealthCare360.Modules.Diagnostics.Features.LabSamples;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Beds;
using HyFib.HealthCare360.Modules.Inpatient.Features.Discharge;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Domain;
using HyFib.HealthCare360.Modules.Patients.Features.Visits;
using HyFib.HealthCare360.Modules.Pharmacy.Domain;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Queue;
using HyFib.HealthCare360.Modules.Pharmacy.Features.Sales;
using HyFib.HealthCare360.Modules.SupplyChain.Features.Stores;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Documents;
using HyFib.HealthCare360.Platform.Http;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.Platform.Settings;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BillPaymentMode = HyFib.HealthCare360.Modules.Billing.Domain.PaymentMode;
using BillStatus = HyFib.HealthCare360.Modules.Billing.Domain.BillStatus;
using DiscrepancyKind = HyFib.HealthCare360.Modules.Billing.Domain.DiscrepancyKind;
using GatewayDiscrepancy = HyFib.HealthCare360.Modules.Billing.Domain.GatewayDiscrepancy;
using Migrations = HyFib.HealthCare360.Server.Persistence.Migrations;
using PharmacyPaymentMode = HyFib.HealthCare360.Modules.Pharmacy.Domain.PaymentMode;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// Free treatment of a victim, over the real API. Every hospital treats the victim of a sexual offence or an acid attack
/// free of cost (BNSS 2023, s.397; the person in charge of one that does not is punishable, BNS s.200). The MLC register
/// says who is such a victim: the ER visit of a sexual assault or an acid attack, the stay the patient is admitted to from
/// it and a later visit the desk links to the entry by its MLC number are billed at nothing, their medicines are given
/// free, nothing is asked first, and the bills print the law and the MLC entry, never the offence.
/// </summary>
public sealed class FreeTreatmentTests(HospitalServer server)
{
    private const string Er = "/api/v1/emergency";
    private const string Bills = "/api/v1/billing/bills";
    private const string ParacetamolCode = "DRG-PCM500";
    private const string CrpCode = "LAB-CRP";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_victim_is_billed_nothing_in_the_er_and_given_her_medicines_while_a_road_accident_is_billed_as_usual()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var pharmacist = await server.SignInAsync(HospitalServer.Users.Pharmacist);

        // The road accident's ER bill charges the registration and the ER consultation posted at check-in.
        var accident = await ErCaseAsync(frontOffice, doctor, MlcCause.RoadTrafficAccident, Gender.Female);
        var charged = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(accident.Visit.Id, null, null));
        charged.Lines.ShouldNotBeEmpty();
        (charged.NetAmount > 0m, charged.FreeTreatmentReason).ShouldBe((true, null));

        // The victim's comes to nothing: what it was worth is kept, and the law and the MLC entry are named.
        var victim = await ErCaseAsync(frontOffice, doctor, MlcCause.SexualAssault, Gender.Female);
        var reason = $"BNSS 2023 s.397, MLC no. {victim.Mlc.Number}";
        var draft = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(victim.Visit.Id, null, null));
        draft.Lines.Select(l => l.ServiceCode).ShouldBe(charged.Lines.Select(l => l.ServiceCode), ignoreOrder: true);
        (draft.NetAmount, draft.PatientAmount, draft.TaxAmount, draft.FreeTreatmentReason).ShouldBe((0m, 0m, 0m, reason));
        draft.FreeTreatmentAmount.ShouldBe(charged.NetAmount);
        draft.Lines.ShouldAllBe(l => l.NetAmount == 0m);

        // No discount is given on it, and finalized it is settled with nothing paid.
        using (var discount = await billing.SendRawAsync(HttpMethod.Post, $"{Bills}/{draft.Id}/discount",
                   new ApplyDiscountRequest(10m, null, null, "Staff", draft.Version)))
        {
            discount.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await discount.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.free_treatment");
        }

        var bill = await billing.PostAsync<BillDetail>($"{Bills}/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        (bill.Status, bill.DueAmount, bill.NetAmount).ShouldBe((BillStatus.Paid, 0m, 0m));
        var printed = await billing.GetAsync<PrintableDocumentDto>($"{Bills}/{bill.Id}/document");
        printed.Html.ShouldContain($"Less: free treatment by law ({reason})");
        printed.Html.ShouldNotContain("Sexual", Case.Insensitive);

        // The accounts' day book: the bill's sales voucher balances, its services given away whole and nothing owed.
        var accounts = await server.SignInAsync(HospitalServer.Users.Accountant);
        var day = DateOnly.FromDateTime(bill.FinalizedAt!.Value.ToOffset(TimeSpan.FromMinutes(330)).DateTime);
        var voucher = (await accounts.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={day:yyyy-MM-dd}"))
            .Single(v => v.Number == bill.BillNumber);
        voucher.Entries.Where(e => e.IsDebit).ShouldHaveSingleItem("Nothing is owed: the value given away is the only debit.").Amount.ShouldBe(bill.FreeTreatmentAmount);
        voucher.Entries.Where(e => !e.IsDebit).Sum(e => e.Amount).ShouldBe(bill.FreeTreatmentAmount);

        // The ER doctor's prescription is dispensed free: a payment offered is refused, and none is taken.
        var prescription = await PrescribeAsync(doctor, victim.PatientId, victim.Visit.Id);
        var store = (await pharmacist.GetAsync<List<StoreDto>>("/api/v1/inventory/stores?type=Pharmacy")).Single(s => s.Code == "OPPH");
        var waiting = (await pharmacist.GetAsync<PagedResult<QueueListItem>>($"/api/v1/pharmacy/queue?search={prescription.PrescriptionNumber}")).Items.ShouldHaveSingleItem();
        var entry = await pharmacist.GetAsync<QueueEntryDetail>($"/api/v1/pharmacy/queue/{waiting.Id}?storeId={store.Id}");
        entry.FreeTreatmentReason.ShouldBe(reason);
        var line = entry.Lines.ShouldHaveSingleItem();

        // One tablet is handed over: the shelf is shared with every test of the run.
        DispenseRequest Dispense(params SalePayment[] payments) => new(
            store.Id, [new DispenseLineRequest(line.Id, line.DrugItemId!.Value, 1m, null, null, false)], null, 0m, payments, false);
        using (var paid = await pharmacist.SendRawAsync(HttpMethod.Post, $"/api/v1/pharmacy/queue/{entry.Id}/dispense",
                   Dispense(new SalePayment(PharmacyPaymentMode.Cash, 20m, null))))
        {
            paid.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await paid.Content.ReadAsStringAsync(Ct)).ShouldContain("pharmacy.free_treatment");
        }

        var created = await pharmacist.PostAsync<PharmacySaleCreated>($"/api/v1/pharmacy/queue/{entry.Id}/dispense", Dispense());
        (created.NetAmount, created.PaidAmount, created.DueAmount, created.Status).ShouldBe((0m, 0m, 0m, SaleStatus.Paid));
        var sale = await pharmacist.GetAsync<PharmacySaleDetail>($"/api/v1/pharmacy/sales/{created.Id}");
        (sale.FreeTreatmentReason, sale.TaxableAmount, sale.CgstAmount + sale.SgstAmount).ShouldBe((reason, 0m, 0m));
        sale.Payments.ShouldBeEmpty();
        sale.Items.ShouldHaveSingleItem().DiscountAmount.ShouldBe(sale.GrossAmount, "The medicine is given at nothing.");
        sale.GrossAmount.ShouldBeGreaterThan(0m);
    }

    [Fact]
    public async Task A_victim_admitted_from_the_er_is_asked_no_advance_and_her_stay_is_billed_nothing()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var nurse = await server.SignInAsync(HospitalServer.Users.Nurse);

        // An acid attack, entered in the register; the patient is admitted from the ER to the ward.
        var victim = await ErCaseAsync(frontOffice, doctor, MlcCause.AcidAttack, Gender.Male);
        var reason = $"BNSS 2023 s.397, MLC no. {victim.Mlc.Number}";
        var admission = await AdmitAsync(frontOffice, doctor, victim.PatientId, victim.Visit.Id, "GWM");
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        account.FreeTreatmentReason.ShouldBe(reason);

        // No advance is taken at the counter, nor asked for by a payment link.
        using (var deposit = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/ip-accounts/{account.Id}/deposits",
                   new CollectDepositRequest([new PaymentLineInput(BillPaymentMode.Cash, 5_000m, null, null, null, null, null, null)], null)))
        {
            deposit.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await deposit.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.free_treatment");
        }

        using (var link = await billing.SendRawAsync(HttpMethod.Post, "/api/v1/billing/payment-links",
                   new CreatePaymentLinkRequest(null, account.Id, 5_000m, SendSms: false)))
        {
            link.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await link.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.free_treatment");
        }

        // The consultant sees the patient on the ward, which is charged; the running bill and the final bill come to nothing.
        var visit = await doctor.PostAsync<ConsultantVisitDto>($"/api/v1/inpatient/admissions/{admission.Id}/consultant-visits",
            new RecordConsultantVisitRequest(null, null, "Burns dressed; plan for grafting"));
        visit.IsCharged.ShouldBeTrue(visit.ChargeError);
        var running = await billing.GetAsync<RunningBill>($"/api/v1/billing/ip-accounts/{account.Id}/running-bill");
        (running.EstimatedPatientPayable, running.FreeTreatmentReason).ShouldBe((0m, reason));
        running.NetTotal.ShouldBeGreaterThan(0m, "The services are charged, and so valued, as usual.");

        // The ward initiates the discharge, which drafts the final bill: it comes to nothing, is settled at ₹0, and clears the
        // patient to go home. The bed is cleaned for the next.
        await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/initiate",
            new InitiateDischargeRequest(null, "Fit for discharge; grafting as a day case"));
        var final = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        (final.NetAmount, final.FreeTreatmentReason, final.FreeTreatmentAmount > 0m).ShouldBe((0m, reason, true));
        final = await billing.PostAsync<BillDetail>($"{Bills}/{final.Id}/finalize", new BillVersionRequest(final.Version));
        (final.Status, final.DueAmount).ShouldBe((BillStatus.Paid, 0m));
        var clearance = await billing.GetAsync<ClearanceDto>($"/api/v1/billing/ip-accounts/{account.Id}/clearance");
        (clearance.Cleared, clearance.Outstanding).ShouldBe((true, 0m), clearance.Reason);
        var stay = await nurse.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admission.Id}");
        (await nurse.PostAsync<DischargeResult>($"/api/v1/inpatient/admissions/{admission.Id}/discharge/complete",
            new CompleteDischargeRequest(DischargeType.Normal, null, "Discharged home", NotifyPatient: false))).Status.ShouldBe(AdmissionStatus.Discharged);
        await CleanBedAsync(nurse, stay);
    }

    [Fact]
    public async Task A_cause_corrected_bills_the_visit_as_usual_and_a_later_visit_linked_to_the_victims_mlc_entry_is_free()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var lab = await server.SignInAsync(HospitalServer.Users.Lab);

        var victim = await ErCaseAsync(frontOffice, doctor, MlcCause.SexualAssault, Gender.Female);
        var reason = $"BNSS 2023 s.397, MLC no. {victim.Mlc.Number}";
        var draft = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(victim.Visit.Id, null, null));
        draft.NetAmount.ShouldBe(0m);

        // Corrected to an assault, before the police are told: the draft is charged as usual once refreshed; corrected back,
        // it is free again.
        var assault = await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{victim.CaseId}/mlc", Mlc(MlcCause.Assault, victim.Mlc.Version));
        draft = await RefreshAsync(billing, draft);
        (draft.NetAmount > 0m, draft.FreeTreatmentReason).ShouldBe((true, null));
        await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{victim.CaseId}/mlc", Mlc(MlcCause.SexualAssault, assault.Mlc!.Version));
        (await RefreshAsync(billing, draft)).NetAmount.ShouldBe(0m);

        // She comes back to the OP for the same case: the desk links the visit to her MLC entry by its number, typed in
        // small letters. The visit is free, and the lab draws her blood with nothing paid first.
        var followUp = await OpVisitAsync(frontOffice, doctor, victim.PatientId, victim.Mlc.Number.ToLowerInvariant());
        await OrderCrpAsync(doctor, victim.PatientId, followUp.Id);
        var tube = (await lab.GetAsync<PagedResult<PendingCollectionItem>>($"/api/v1/lab/samples/pending-collection?search={victim.Uhid}")).Items
            .ShouldHaveSingleItem();
        (await lab.PostAsync<List<SampleLabel>>($"/api/v1/lab/orders/{tube.LabOrderId}/collect", new CollectSamplesRequest(null, null, "Follow-up sample")))
            .ShouldHaveSingleItem().CollectedAt.ShouldNotBeNull();
        var followUpBill = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(followUp.Id, null, null));
        followUpBill.Lines.Select(l => l.ServiceCode).ShouldBe(["CONS-NEW", CrpCode], ignoreOrder: true);
        (followUpBill.NetAmount, followUpBill.FreeTreatmentReason).ShouldBe((0m, reason));

        // Another patient's visit given her MLC number is billed as usual: the entry is hers alone.
        var other = await ErCaseAsync(frontOffice, doctor, MlcCause.Fall, Gender.Female);
        var misused = await OpVisitAsync(frontOffice, doctor, other.PatientId, victim.Mlc.Number);
        var otherBill = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(misused.Id, null, null));
        (otherBill.NetAmount > 0m, otherBill.FreeTreatmentReason).ShouldBe((true, null));

        // Admitted straight to the ward, her stay is billed as usual until the desk links it to her MLC entry.
        var stay = await AdmitAsync(frontOffice, doctor, victim.PatientId, null, "GWF");
        (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{stay.Id}")).FreeTreatmentReason.ShouldBeNull();
        var admitted = await frontOffice.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{stay.Id}");
        using (var linked = await frontOffice.SendRawAsync(HttpMethod.Put, $"/api/v1/inpatient/admissions/{stay.Id}", new UpdateAdmissionRequest(
                   admitted.DepartmentId, admitted.ConsultantPractitionerId!.Value, admitted.ProvisionalDiagnosis, admitted.ExpectedDischargeDate, null,
                   IsMlc: true, victim.Mlc.Number, null, null, null, null, admitted.Version)))
        {
            linked.EnsureSuccessStatusCode();
        }

        (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{stay.Id}")).FreeTreatmentReason.ShouldBe(reason);
        await CancelAdmissionAsync(frontOffice, await server.SignInAsync(HospitalServer.Users.Nurse), stay.Id);
    }

    [Fact]
    public async Task An_advance_link_paid_after_the_stay_was_found_free_is_not_taken_and_waits_to_be_refunded()
    {
        await using var gateway = await PaymentsToSimulatorAsync();
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // Admitted from the ER before the doctor entered the case, the stay is billed as usual: the desk sends a link for an
        // advance.
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice);
        var caseId = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;
        var admission = await AdmitAsync(frontOffice, doctor, patientId, visit.Id, "GWM");
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        account.FreeTreatmentReason.ShouldBeNull();
        var link = await billing.PostAsync<GatewayOrderDto>("/api/v1/billing/payment-links", new CreatePaymentLinkRequest(null, account.Id, 5_000m, false, false));

        // The doctor then enters an acid attack, and the stay is free. A relative pays the link: the advance is not taken,
        // and the payment waits for the accountants to refund it.
        var mlc = (await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", Mlc(MlcCause.AcidAttack, null))).Mlc.ShouldNotBeNull();
        (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}")).FreeTreatmentReason
            .ShouldBe($"BNSS 2023 s.397, MLC no. {mlc.Number}");
        var paid = await billing.PostAsync<GatewayOrderDto>($"/api/v1/billing/payment-links/{link.Id}/refresh");
        paid.ReceiptId.ShouldBeNull("No advance is recorded on a stay treated free.");
        paid.LastError.ShouldNotBeNull().ShouldContain("free of cost by law");
        (await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}")).DepositReceived.ShouldBe(0m);
        await using var scope = server.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<HospitalDbContext>().Set<GatewayDiscrepancy>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(d => d.Kind == DiscrepancyKind.UnrecordedPayment && d.Key == paid.PaymentId, Ct)).IpAccountId.ShouldBe(account.Id);
        await CancelAdmissionAsync(frontOffice, await server.SignInAsync(HospitalServer.Users.Nurse), admission.Id);
    }

    [Fact]
    public async Task A_discount_asked_for_before_the_case_was_known_does_not_hold_up_the_free_bill()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);

        // The desk drafts the ER bill and asks for half off, which waits for approval, before the doctor enters the case.
        var (_, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, Gender.Female);
        var caseId = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id).CaseId;
        var draft = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(visit.Id, null, null));
        (await billing.PostAsync<DiscountOutcome>($"{Bills}/{draft.Id}/discount", new ApplyDiscountRequest(50m, null, null, "Cannot afford", draft.Version)))
            .Applied.ShouldBeFalse("Half the bill is above the desk's limit, so it waits for approval.");

        // The doctor enters a sexual assault: the bill comes to nothing and is finalized, with no discount left to decide.
        await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{caseId}/mlc", Mlc(MlcCause.SexualAssault, null));
        draft = await RefreshAsync(billing, await billing.GetAsync<BillDetail>($"{Bills}/{draft.Id}"));
        draft.NetAmount.ShouldBe(0m);
        var bill = await billing.PostAsync<BillDetail>($"{Bills}/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
        (bill.Status, bill.PendingDiscount).ShouldBe((BillStatus.Paid, null));
    }

    [Fact]
    public async Task The_upgrade_marks_the_visits_of_sexual_assaults_already_in_the_register()
    {
        var frontOffice = await server.SignInAsync(HospitalServer.Users.FrontOffice);
        var doctor = await server.SignInAsync(HospitalServer.Users.Doctor);
        var billing = await server.SignInAsync(HospitalServer.Users.Billing);
        var accident = await ErCaseAsync(frontOffice, doctor, MlcCause.RoadTrafficAccident, Gender.Male);
        var victim = await ErCaseAsync(frontOffice, doctor, MlcCause.SexualAssault, Gender.Female);
        var followUp = await OpVisitAsync(frontOffice, doctor, victim.PatientId, victim.Mlc.Number);

        // As the visits were before the upgrade, when nothing marked them: the victim's ER visit is billed. Then the upgrade's
        // own statement marks it, and the visit linked to her entry, and a draft of hers comes to nothing once refreshed.
        BillDetail draft;
        await using (var scope = server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE patients.visits SET free_treatment_reason = NULL WHERE id IN ({accident.Visit.Id}, {victim.Visit.Id}, {followUp.Id})", Ct);
            draft = await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(victim.Visit.Id, null, null));
            draft.NetAmount.ShouldBeGreaterThan(0m);
            await db.Database.ExecuteSqlRawAsync(Migrations.FreeTreatment.MarkVictimsVisits, Ct);
        }

        var reason = $"BNSS 2023 s.397, MLC no. {victim.Mlc.Number}";
        (await RefreshAsync(billing, draft)).FreeTreatmentReason.ShouldBe(reason);
        (await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(followUp.Id, null, null))).FreeTreatmentReason.ShouldBe(reason);
        (await billing.PostAsync<BillDetail>(Bills, new CreateBillRequest(accident.Visit.Id, null, null))).FreeTreatmentReason.ShouldBeNull();
    }

    private sealed record ErCase(Guid PatientId, string Uhid, VisitDetail Visit, Guid CaseId, MlcDto Mlc);

    /// <summary>A new emergency patient whose case the doctor enters in the MLC register as <paramref name="cause"/>.</summary>
    private static async Task<ErCase> ErCaseAsync(ApiClient frontOffice, ApiClient doctor, MlcCause cause, Gender gender)
    {
        var (patientId, visit) = await EmergencyDepartmentTests.EmergencyVisitAsync(frontOffice, gender);
        var row = (await doctor.GetAsync<ErBoard>($"{Er}/board")).Rows.Single(r => r.VisitId == visit.Id);
        var saved = await doctor.PutAsync<EmergencyCaseDetail>($"{Er}/cases/{row.CaseId}/mlc", Mlc(cause, null));
        return new ErCase(patientId, row.Uhid, visit, row.CaseId, saved.Mlc.ShouldNotBeNull());
    }

    private static SaveMlcRequest Mlc(MlcCause cause, uint? version) => new(
        MlcBroughtBy.Relative, "Brother, Ramesh", cause, DateTimeOffset.UtcNow.AddHours(-2), "Near the bus stand, Adyar", "Injuries as recorded",
        true, false, "Adyar", null, version);

    /// <summary>
    /// The front office checks the patient in to the doctor's OP (a consultation is charged) as a medico-legal case under
    /// <paramref name="mlcNumber"/>.
    /// </summary>
    private static async Task<VisitDetail> OpVisitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId, string mlcNumber)
    {
        var departments = await frontOffice.GetAsync<List<LookupItem>>("/api/v1/masters/lookups/departments");
        return await frontOffice.PostAsync<VisitDetail>("/api/v1/visits", new CreateVisitRequest(
            patientId, VisitType.OutPatient, departments.Single(d => d.Code == "GM").Id, await PractitionerIdAsync(doctor), null, PayerType.Self, null,
            ReferralSource.Self, null, "Review after the incident", true, mlcNumber, null, TokenPriority.Normal, IssueToken: false, NotifyPatient: false));
    }

    private static async Task<Guid> PractitionerIdAsync(ApiClient doctor) =>
        (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();

    /// <summary>The doctor orders a CRP at the visit, from an encounter on it.</summary>
    private static async Task OrderCrpAsync(ApiClient doctor, Guid patientId, Guid visitId)
    {
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, visitId, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Review"));
        var crp = (await doctor.GetAsync<List<ServiceItemDto>>($"/api/v1/masters/lookups/services?category=Laboratory&search={CrpCode}")).Single(s => s.Code == CrpCode);
        await doctor.PostAsync<OrderDto>("/api/v1/clinical/orders", new PlaceOrderRequest(
            patientId, encounter.Id, VisitId: null, AdmissionId: null, OrderPriority.Routine, "Review", [new OrderLineRequest(crp.Id, Notes: null)],
            OrderSetId: null, ConfirmDuplicates: false));
    }

    /// <summary>The doctor prescribes paracetamol at the visit, from an encounter on it, and signs the e-prescription.</summary>
    private static async Task<PrescriptionDto> PrescribeAsync(ApiClient doctor, Guid patientId, Guid visitId)
    {
        var encounter = await doctor.PostAsync<EncounterDetail>("/api/v1/emr/encounters", new StartEncounterRequest(
            patientId, visitId, AdmissionId: null, Type: null, DepartmentId: null, PractitionerId: null, TemplateId: null, ChiefComplaint: "Pain"));
        var drug = (await doctor.GetAsync<List<PrescribableDrug>>("/api/v1/emr/drugs?search=Paracetamol")).Single(d => d.Code == ParacetamolCode);
        var prescription = await doctor.PostAsync<PrescriptionDto>("/api/v1/clinical/prescriptions", new CreatePrescriptionRequest(
            patientId, encounter.Id, VisitId: null, AdmissionId: null, IsInpatient: false, PractitionerId: null, Notes: null, Advice: null, RefillsAllowed: 0,
            [
                new PrescriptionItemRequest(
                    drug.Id, drug.Name, drug.GenericName, drug.Strength, drug.DosageForm, Route: "Oral", Dose: "1 tablet", DoseFrequency.TDS,
                    CustomFrequency: null, DosesPerDay: null, 3, Quantity: null, Instructions: "After food", drug.IsControlled, drug.ControlledSchedule),
            ]));
        var signed = await doctor.PostAsync<SignPrescriptionResult>($"/api/v1/clinical/prescriptions/{prescription.Id}/sign",
            new SignPrescriptionRequest(OverrideReason: null, AcknowledgeWarnings: false, ConfirmControlledDrugs: false, prescription.Version));
        return signed.Prescription;
    }

    /// <summary>
    /// The front office admits the patient under the doctor to a free bed of a general ward (<paramref name="wardCode"/>), from
    /// the ER visit when there is one.
    /// </summary>
    private static async Task<AdmissionCreated> AdmitAsync(ApiClient frontOffice, ApiClient doctor, Guid patientId, Guid? erVisitId, string wardCode)
    {
        var consultantId = await PractitionerIdAsync(doctor);
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == wardCode);
        var bed = (await frontOffice.GetAsync<List<AvailableBedDto>>($"/api/v1/beds/available?patientId={patientId}&wardId={ward.Id}"))
            .First(b => b.IsEligible && b.Status == BedStatus.Vacant);
        return await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patientId, BedId: bed.BedId, Type: AdmissionType.Emergency, Source: erVisitId is null ? AdmissionSource.Direct : AdmissionSource.Emergency,
            SourceVisitId: erVisitId,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Observation and treatment", ReferredBy: null, PayerType: PayerType.Self, PayerId: null, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
    }

    /// <summary>
    /// The front office cancels an admission made only to look at its account, and the bed is cleaned: the wards' beds are
    /// shared by every test of the run.
    /// </summary>
    private static async Task CancelAdmissionAsync(ApiClient frontOffice, ApiClient nurse, Guid admissionId)
    {
        var stay = await frontOffice.GetAsync<AdmissionDetail>($"/api/v1/inpatient/admissions/{admissionId}");
        using (var cancelled = await frontOffice.SendRawAsync(HttpMethod.Post, $"/api/v1/inpatient/admissions/{admissionId}/cancel",
                   new CancelAdmissionRequest("Admitted only to look at the account")))
        {
            cancelled.EnsureSuccessStatusCode();
        }

        await CleanBedAsync(nurse, stay);
    }

    /// <summary>Housekeeping cleans the bed a discharge or a cancelled admission released; the bed is vacant again.</summary>
    private static async Task CleanBedAsync(ApiClient nurse, AdmissionDetail stay)
    {
        var task = (await nurse.GetAsync<List<HousekeepingTaskDto>>($"/api/v1/beds/housekeeping?wardId={stay.WardId}"))
            .Single(t => t.BedId == stay.BedId && t.AdmissionId == stay.Id);
        await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/start");
        (await nurse.PostAsync<HousekeepingTaskDto>($"/api/v1/beds/housekeeping/{task.Id}/complete", new BedNoteRequest("Cleaned")))
            .Status.ShouldBe(HousekeepingStatus.Completed);
    }

    private static Task<BillDetail> RefreshAsync(ApiClient billing, BillDetail draft) =>
        billing.PostAsync<BillDetail>($"{Bills}/{draft.Id}/refresh", new BillVersionRequest(draft.Version));

    /// <summary>Payments go to the training gateway, which takes a link's payment at once, until the test ends.</summary>
    private async Task<IAsyncDisposable> PaymentsToSimulatorAsync()
    {
        const string key = "integrations.payment";
        const string secretKey = "integrations.payment.secret";
        var admin = await server.SignInAsync(HospitalServer.Users.Admin);
        var before = (await admin.GetAsync<List<SettingDto>>($"/api/v1/admin/settings?prefix={key}"))
            .FirstOrDefault(s => s.Key == key && s.FacilityId is null)?.Value;
        await admin.SaveSettingAsync(key, new SaveSettingRequest(
            new JsonObject { ["enabled"] = true, ["provider"] = "simulator", ["simulatorSettlementDays"] = 0 }, null, false, null));
        await admin.SaveSettingAsync(secretKey, new SaveSettingRequest(new JsonObject { ["simulatorSecret"] = "free-treatment-simulator-key" }, null, true, null));
        return new Restore(async () =>
        {
            await admin.SaveSettingAsync(key, new SaveSettingRequest(before?.DeepClone() ?? new JsonObject { ["enabled"] = false }, null, false, null));
            await admin.SaveSettingAsync(secretKey, new SaveSettingRequest(new JsonObject(), null, true, null));
        });
    }

    private sealed class Restore(Func<Task> restore) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await restore();
    }
}
