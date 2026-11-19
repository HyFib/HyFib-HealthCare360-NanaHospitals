using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HyFib.HealthCare360.Modules.Billing.Domain;
using HyFib.HealthCare360.Modules.Billing.Features.Bills;
using HyFib.HealthCare360.Modules.Billing.Features.Charges;
using HyFib.HealthCare360.Modules.Billing.Features.Inpatient;
using HyFib.HealthCare360.Modules.Billing.Features.Insurance;
using HyFib.HealthCare360.Modules.Billing.Features.Payments;
using HyFib.HealthCare360.Modules.Inpatient.Domain;
using HyFib.HealthCare360.Modules.Inpatient.Features.Admissions;
using HyFib.HealthCare360.Modules.Inpatient.Features.Masters;
using HyFib.HealthCare360.Modules.Patients.Features.Patients;
using HyFib.HealthCare360.Platform.Approvals;
using HyFib.HealthCare360.Platform.Contracts;
using HyFib.HealthCare360.Platform.Integrations;
using HyFib.HealthCare360.Platform.Organization;
using HyFib.HealthCare360.Platform.Persistence;
using HyFib.HealthCare360.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Users = HyFib.HealthCare360.IntegrationTests.HospitalServer.Users;

namespace HyFib.HealthCare360.IntegrationTests;

/// <summary>
/// A ward return after the final bill, over the real API and PostgreSQL: an unopened ₹3,000 injection the IP pharmacy
/// takes back after the stay's final bill was finalized. After an insured stay's final bill it went on a self-pay
/// supplementary bill of -₹3,000, refundable to the patient although the insurer paid for the injection. Now it goes on
/// no other bill: it waits on the final bill, holds up no discharge, and the billing desk credits it there, split as the
/// final bill split the sale: the insurer's share off what the insurer owes, nothing to the patient. After a self-paying
/// stay's final bill it goes on the supplementary bill as before, and the patient is refunded.
/// </summary>
public sealed class WardReturnAfterFinalBillTests(HospitalServer server)
{
    private const decimal Price = 3_000m;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task After_an_insured_stays_final_bill_a_ward_return_is_credited_on_it_to_the_insurer_and_refunds_the_patient_nothing()
    {
        var billing = await server.SignInAsync(Users.Billing);
        var accountant = await server.SignInAsync(Users.Accountant);
        var company = await CompanyAsync();
        var (admission, account) = await AdmitAsync(PayerType.Corporate, company.Id);

        // The IP pharmacy issues the injection; the final bill is finalized at discharge, the company paying all of it.
        var injection = $"Inj. Meropenem 1 g (batch MR{Letters(6)})";
        var issue = Guid.NewGuid().ToString();
        await PharmacyPostsAsync(admission, account, injection, 1m, "pharmacy.ip", issue);
        var final = await FinalBillAsync(billing, account);
        (final.PayerId, final.PatientAmount, final.PayerAmount).ShouldBe((company.Id, 0m, final.NetAmount));
        var injectionLine = final.Lines.Single(l => l.Description == injection);

        // What the injection came to on the final bill (₹3,000 less any concession of the stay's plan): what comes back.
        var value = injectionLine.NetAmount;
        value.ShouldBeGreaterThan(0m);

        // The ward sends it back unopened, after the final bill: the pharmacy credits the stay.
        await PharmacyPostsAsync(admission, account, $"Returned: {injection}", -1m, "pharmacy.ip.return", Guid.NewGuid().ToString());

        // Billing late services finds only the return, which the final bill charged to the company: no self-pay bill of
        // -₹3,000 for the patient, who paid nothing for it.
        using (var refused = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/ip-accounts/{account.Id}/final-bill"))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await CodeAsync(refused)).ShouldBe("billing.returns_to_credit");
        }

        (await BillsAsync(billing, admission.Id)).ShouldHaveSingleItem().Id.ShouldBe(final.Id);

        // The discharge clearance and the final bill show it, waiting to be credited on the final bill; it holds nothing up.
        var clearance = await billing.GetAsync<ClearanceDto>($"/api/v1/billing/ip-accounts/{account.Id}/clearance");
        (clearance.Cleared, clearance.UnbilledCount, clearance.ReturnsToCreditCount, clearance.ReturnsToCreditAmount, clearance.FinalBillId)
            .ShouldBe((true, 0, 1, value, (Guid?)final.Id));
        var waiting = (await BillOfAsync(billing, final)).ReturnsToCredit.ShouldNotBeNull().ShouldHaveSingleItem();
        (waiting.Description, waiting.Quantity, waiting.Amount).ShouldBe(($"Returned: {injection}", 1m, value));

        // The billing desk asks for the credit; the accountant, someone else, issues it.
        using (var response = await billing.SendRawAsync(HttpMethod.Post, $"/api/v1/billing/bills/{final.Id}/return-credit",
                   new CreditReturnsRequest("Injection returned unopened to the IP pharmacy")))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Ct));
            var asked = (await response.Content.ReadFromJsonAsync<ApprovalRequestDto>(ApiClient.Json, Ct)).ShouldNotBeNull();
            asked.Details.ShouldContain(new ApprovalDetail("Off what the payer owes", $"₹{Money.FormatInr(value)}"));
            asked.Details.ShouldContain(new ApprovalDetail("Off what the patient owes", "₹0.00"));
            await MakerChecker.ApproveAsync(accountant, asked, "Returned stock checked against the pharmacy's register");
        }

        // The credit reaches the final bill: a credit note to the company for its share, naming the injection; the company
        // owes ₹3,000 less, and the patient is refunded nothing.
        var credited = await BillOfAsync(billing, final);
        var note = credited.CreditNotes.ShouldHaveSingleItem();
        (note.Kind, note.Amount).ShouldBe((CreditNoteKind.PayerReturn, value));
        note.Lines.ShouldNotBeNull().ShouldHaveSingleItem().ShouldSatisfyAllConditions(l => l.BillLineId.ShouldBe(injectionLine.Id), l => l.Amount.ShouldBe(value));
        (credited.PayerDisallowedAmount, credited.CreditAmount, credited.RefundableAmount, credited.DueAmount).ShouldBe((value, 0m, 0m, 0m));
        credited.Refunds.ShouldBeEmpty();
        (credited.ReturnsToCredit ?? []).ShouldBeEmpty();
        (await ChargesAsync(billing, admission.Id)).Single(c => c.SourceModule == "pharmacy.ip.return")
            .ShouldSatisfyAllConditions(c => c.Status.ShouldBe(ChargeStatus.Billed), c => c.BillId.ShouldBe(final.Id));
        (await billing.GetAsync<ClearanceDto>($"/api/v1/billing/ip-accounts/{account.Id}/clearance")).ReturnsToCreditCount.ShouldBe(0);

        // In the books the credit note comes off the company's ledger, against the final bill.
        var vouchers = await accountant.GetAsync<List<AccountingVoucher>>($"/api/v1/billing/tally/day-book?date={EInvoicing.Today():yyyy-MM-dd}");
        var voucher = vouchers.Single(v => v.Number == note.Number);
        voucher.Entries.Single(e => !e.IsDebit).ShouldSatisfyAllConditions(
            e => e.Amount.ShouldBe(value),
            e => e.BillAllocations.ShouldBe([new AccountingBillAllocation(final.BillNumber, AccountingBillType.AgainstRef, value)]));
        voucher.Entries.ShouldNotContain(e => e.LedgerName == "Patient Receivables");
    }

    [Fact]
    public async Task After_an_insured_stays_final_bill_the_return_of_something_issued_since_goes_on_the_supplementary_bill_with_it()
    {
        var billing = await server.SignInAsync(Users.Billing);
        var company = await CompanyAsync();
        var (admission, account) = await AdmitAsync(PayerType.Corporate, company.Id);
        await PharmacyPostsAsync(admission, account, $"Inj. Meropenem 1 g (batch MR{Letters(6)})", 1m, "pharmacy.ip", Guid.NewGuid().ToString());
        var final = await FinalBillAsync(billing, account);

        // After the final bill the ward is issued two vials of another batch and sends one back unopened. The company paid
        // for none of them, so the return is not credited to it on the final bill: it goes with the issue, on the
        // supplementary bill.
        var later = $"Inj. Meropenem 1 g (batch MR{Letters(6)})";
        await PharmacyPostsAsync(admission, account, later, 2m, "pharmacy.ip", Guid.NewGuid().ToString());
        await PharmacyPostsAsync(admission, account, $"Returned: {later}", -1m, "pharmacy.ip.return", Guid.NewGuid().ToString());

        ((await BillOfAsync(billing, final)).ReturnsToCredit ?? []).ShouldBeEmpty();
        (await billing.GetAsync<ClearanceDto>($"/api/v1/billing/ip-accounts/{account.Id}/clearance")).ShouldSatisfyAllConditions(
            c => c.UnbilledCount.ShouldBe(2), c => c.ReturnsToCreditCount.ShouldBe(0));
        var supplementary = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        supplementary.IsFinalBill.ShouldBeFalse();
        supplementary.Lines.Select(l => (l.Description, l.Quantity, l.IsReturn)).OrderBy(l => l.Quantity)
            .ShouldBe([($"Returned: {later}", -1m, true), (later, 2m, false)]);
    }

    [Fact]
    public async Task After_a_self_paying_stays_final_bill_a_ward_return_goes_on_the_supplementary_bill_and_the_patient_is_refunded()
    {
        var billing = await server.SignInAsync(Users.Billing);
        var (admission, account) = await AdmitAsync(PayerType.Self, null);
        var injection = $"Inj. Meropenem 1 g (batch MR{Letters(6)})";
        await PharmacyPostsAsync(admission, account, injection, 1m, "pharmacy.ip", Guid.NewGuid().ToString());
        var final = await FinalBillAsync(billing, account);
        var value = final.Lines.Single(l => l.Description == injection).NetAmount;
        await billing.PostAsync<ReceiptDto>("/api/v1/billing/receipts", new CollectPaymentRequest(
            final.Id, [new PaymentLineInput(PaymentMode.Upi, final.DueAmount, "UTR" + Digits(9), null, null, null, null, null)], null));

        // The patient paid for the injection: its return goes on the supplementary bill, and is refundable to them.
        await PharmacyPostsAsync(admission, account, $"Returned: {injection}", -1m, "pharmacy.ip.return", Guid.NewGuid().ToString());
        var supplementary = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        supplementary.IsFinalBill.ShouldBeFalse();
        supplementary.Lines.ShouldHaveSingleItem().ShouldSatisfyAllConditions(l => l.IsReturn.ShouldBeTrue(), l => l.NetAmount.ShouldBe(-value));
        supplementary = await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{supplementary.Id}/finalize", new BillVersionRequest(supplementary.Version));
        (supplementary.PatientAmount, supplementary.RefundableAmount).ShouldBe((-Math.Round(value, 0, MidpointRounding.AwayFromZero), Math.Round(value, 0, MidpointRounding.AwayFromZero)));
        ((await BillOfAsync(billing, final)).ReturnsToCredit ?? []).ShouldBeEmpty();
    }

    /// <summary>A company that pays for its staff's care in full, not registered for GST.</summary>
    private async Task<PayerDetail> CompanyAsync()
    {
        var admin = await server.SignInAsync(Users.HospitalAdmin);
        return await server.CreatePayerAsync(admin, new SavePayerRequest(
            "CO" + Letters(8), $"Vaigai Mills {Letters(5)}", PayerCategory.Corporate, "Ramesh Pandian", "9876543210", null, null, null, null,
            CreditDays: 30, DefaultCoPayPercent: 0m, PortalUrl: null, IsActive: true, Notes: null, Version: null));
    }

    /// <summary>
    /// A new patient admitted under the doctor to a bed of the male general ward, on the payer (with a policy verified by
    /// someone other than whoever recorded it) or paying for himself; and the stay's IP account.
    /// </summary>
    private async Task<(AdmissionCreated Admission, IpAccountDetail Account)> AdmitAsync(PayerType payerType, Guid? payerId)
    {
        var frontOffice = await server.SignInAsync(Users.FrontOffice);
        var doctor = await server.SignInAsync(Users.Doctor);
        var billing = await server.SignInAsync(Users.Billing);
        var surname = Letters(8);
        var patient = (await frontOffice.PostAsync<PatientCreated>("/api/v1/patients", new RegisterPatientRequest(
            Title: "Mr.", FirstName: TestPatients.FirstName("Murugan"), MiddleName: null, LastName: surname[0] + surname[1..].ToLowerInvariant(),
            Gender: Gender.Male, DateOfBirth: new DateOnly(1960, 1, 1).AddDays(Random.Shared.Next(365 * 10)), AgeYears: null, AgeMonths: null,
            Phone: "9" + Digits(9), AlternatePhone: null, Email: null, Address: null, BloodGroup: null, MaritalStatus: null, GuardianName: null,
            GuardianRelation: null, EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelation: null, Occupation: null,
            Religion: null, Nationality: null, PreferredLanguage: null, Category: null, AbhaNumber: null, AbhaAddress: null, AadhaarNumber: null,
            PhotoFileId: null, IsVip: false, Notes: null, ConfirmedNotDuplicate: false))).Id;
        if (payerId is { } coveredBy)
        {
            await server.VerifiedPolicyAsync(patient, coveredBy);
        }

        var consultantId = (await doctor.GetAsync<JsonElement>("/api/v1/auth/me")).GetProperty("practitionerId").GetGuid();
        var ward = (await frontOffice.GetAsync<List<WardDto>>("/api/v1/inpatient/wards")).Single(w => w.Code == "GWM");
        var bed = await TestBeds.AddAsync(server, ward.Id);
        var admission = await frontOffice.PostAsync<AdmissionCreated>("/api/v1/inpatient/admissions", new AdmitPatientRequest(
            PatientId: patient, BedId: bed.BedId, Type: AdmissionType.Elective, Source: AdmissionSource.OutPatient, SourceVisitId: null,
            DepartmentId: null, AdmittingPractitionerId: consultantId, ConsultantPractitionerId: consultantId,
            ProvisionalDiagnosis: "Community-acquired pneumonia", ReferredBy: null, PayerType: payerType, PayerId: payerId, PolicyNumber: null,
            IsMlc: false, MlcNumber: null, ExpectedDischargeDate: null, AttendantName: null, AttendantRelation: null, AttendantPhone: null,
            AdmittedAt: null, Notes: null));
        var account = await billing.GetAsync<IpAccountDetail>($"/api/v1/billing/ip-accounts/by-admission/{admission.Id}");
        account.PayerId.ShouldBe(payerId);
        return (admission, account);
    }

    /// <summary>
    /// The IP pharmacy's charge of an issue line (<paramref name="quantity"/> 1) or of its return (-1), under its medicines
    /// service at <see cref="Price"/> a unit, posted as the pharmacy posts it through <see cref="IChargePoster"/>.
    /// </summary>
    private async Task PharmacyPostsAsync(
        AdmissionCreated admission, IpAccountDetail account, string description, decimal quantity, string sourceModule, string sourceReference)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HospitalDbContext>();
        var medicines = await db.Set<ServiceItem>().Where(s => s.Code == "PHARM-MED").Select(s => s.Id).SingleAsync(Ct);
        var posted = await scope.ServiceProvider.GetRequiredService<IChargePoster>().PostAsync(new ChargeRequest
        {
            PatientId = account.PatientId,
            VisitId = admission.VisitId,
            AdmissionId = admission.Id,
            ServiceItemId = medicines,
            Quantity = quantity,
            UnitPriceOverride = Price,
            Description = description,
            SourceModule = sourceModule,
            SourceReference = sourceReference,
            ServiceDate = DateTimeOffset.UtcNow,
        }, Ct);
        posted.IsSuccess.ShouldBeTrue(posted.Error?.Message);
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The billing desk prepares the stay's final bill and finalizes it.</summary>
    private static async Task<BillDetail> FinalBillAsync(ApiClient billing, IpAccountDetail account)
    {
        var draft = await billing.PostAsync<BillDetail>($"/api/v1/billing/ip-accounts/{account.Id}/final-bill");
        draft.IsFinalBill.ShouldBeTrue();
        return await billing.PostAsync<BillDetail>($"/api/v1/billing/bills/{draft.Id}/finalize", new BillVersionRequest(draft.Version));
    }

    private static Task<BillDetail> BillOfAsync(ApiClient client, BillDetail bill) => client.GetAsync<BillDetail>($"/api/v1/billing/bills/{bill.Id}");

    private static async Task<IReadOnlyList<BillListItem>> BillsAsync(ApiClient billing, Guid admissionId) =>
        (await billing.GetAsync<PagedResult<BillListItem>>($"/api/v1/billing/bills?admissionId={admissionId}")).Items;

    private static async Task<List<ChargeDto>> ChargesAsync(ApiClient billing, Guid admissionId) =>
        (await billing.GetAsync<PagedResult<ChargeDto>>($"/api/v1/billing/charges?admissionId={admissionId}&pageSize=200"))
        .Items.Where(c => c.Status != ChargeStatus.Cancelled).ToList();

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ApiClient.Json, Ct);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static string Letters(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('A' + Random.Shared.Next(26)))]);

    private static string Digits(int length) => new([.. Enumerable.Range(0, length).Select(_ => (char)('0' + Random.Shared.Next(10)))]);
}
